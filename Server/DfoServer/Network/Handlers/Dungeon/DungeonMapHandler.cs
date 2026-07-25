using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonMapHandler
    {
        private const byte HellPartyHiddenTemplateFlag = 1;
        private const byte HellPartyAttachAllWavesSelector = 0xFF;

        private readonly DungeonSharedServices _svc;

        internal DungeonMapHandler(DungeonSharedServices svc) => _svc = svc;

        internal async Task HandleMoveMap(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // 塔内分流: 在塔中时 MOVE_MAP = 推进下一层(不走普通地图切换)
            if (await _svc.DeathTower.TryHandleMoveMap(session))
                return;

            var run = session.Player.CurrentRun;
            if (run == null) return;

            var req = MoveMapRequest.Parse(body);

            if (run.Phase >= DungeonRunPhase.Cleared)
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP ignored after dungeon clear: current=({run.RoomKey.X},{run.RoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

            if (IsCurrentHellPartyLocked(session))
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP blocked by active hell party: current=({run.RoomKey.X},{run.RoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

            if (!DungeonRoomTopology.TryResolveMoveTarget(
                run.DungeonId,
                run.MazeIndex,
                run.RoomKey,
                req.NextX,
                req.NextY,
                run.BossMapPos,
                out var moveTarget,
                out var targetReason))
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP blocked outside maze: current=({run.RoomKey.X},{run.RoomKey.Y}) requested=({req.NextX},{req.NextY}) dungeon={run.DungeonId} maze={run.MazeIndex}");
                return;
            }

            if (moveTarget.X != req.NextX || moveTarget.Y != req.NextY)
                FileLogger.Log($"[DungeonHandler] MOVE_MAP normalized: current=({run.RoomKey.X},{run.RoomKey.Y}) requested=({req.NextX},{req.NextY}) target=({moveTarget.X},{moveTarget.Y}) reason={targetReason}");

            var timeSpiralTeleport =
                TimeSpiralDungeonCoordinator.ApplyTeleportOverride(
                    session,
                    req.NextX,
                    req.NextY,
                    ref moveTarget);

            int overrideMapId = -1;

            if (req.Unknown23 == 1)
            {
                var layeredIds = DungeonData.GetLayeredMapIds(run.DungeonId, moveTarget.X, moveTarget.Y, run.MazeIndex);
                if (layeredIds != null && layeredIds.Length > 0)
                {
                    var nextLayer = run.LayeredMapIndex + 1;
                    if (nextLayer < layeredIds.Length)
                    {
                        run.LayeredMapIndex = nextLayer;
                        overrideMapId = layeredIds[nextLayer];
                    }
                }
            }
            else
            {
                run.LayeredMapIndex = -1;
            }

            SpecialDungeonRunCoordinator.TryApplyGentWarpOverride(
                session,
                moveTarget,
                ref overrideMapId);
            await SendStartMapAsync(session, moveTarget.X, moveTarget.Y, overrideMapId);
            TimeSpiralDungeonCoordinator.LogDeferredBuff(
                session,
                timeSpiralTeleport,
                "leader_START_MAP");

            // ★组队副本联机: 队长移动到下一房间时, 带同队队员一起换图(队员是follower、不自发MOVE_MAP)。
            await BroadcastMoveMapToPartyAsync(
                session,
                moveTarget.X,
                moveTarget.Y,
                overrideMapId,
                timeSpiralTeleport);
        }

        // 队长换图时把同队【在副本里】的成员也移到同一房间(服务端驱动, 队员副本=队长迷宫拷贝)。⚠️待真机验证。
        private async Task BroadcastMoveMapToPartyAsync(
            EnhancedClientSession leader,
            int nextX,
            int nextY,
            int overrideMapId,
            TimeSpiralDungeonCoordinator.TeleportMoveContext timeSpiralTeleport)
        {
            var pm = _svc.PartyManager;
            var sessions = _svc.Sessions;
            if (pm == null || sessions == null || leader?.Player == null) return;
            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = pm.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;   // 只有队长换图带全队

            foreach (var m in party.MembersBySlot())
            {
                if (m.UserId == leaderUid) continue;
                sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player?.CurrentRun == null || bs.TcpClient == null || !bs.TcpClient.Connected) continue;
                try
                {
                    bs.Player.CurrentRun.LayeredMapIndex = leader.Player.CurrentRun.LayeredMapIndex;
                    TimeSpiralDungeonCoordinator.CopyTeleportStateForPartyMove(
                        leader.Player.CurrentRun,
                        bs.Player.CurrentRun);
                    await SendStartMapAsync(bs, nextX, nextY, overrideMapId);
                    TimeSpiralDungeonCoordinator.LogDeferredBuff(
                        bs,
                        timeSpiralTeleport,
                        $"party_START_MAP leader={leader.Player.CharacterId}");
                    FileLogger.Log($"[DungeonHandler] PARTY_MOVE_MAP: 带队员 cid={bs.Player.CharacterId} 到 ({nextX},{nextY})");
                }
                catch (System.Exception ex)
                {
                    FileLogger.Log($"[DungeonHandler] PARTY_MOVE_MAP ERROR: member uid={m.UserId}: {ex.Message}");
                }
            }
        }

        internal async Task SendStartMapAsync(EnhancedClientSession session, int nextX, int nextY, int overrideMapId)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var effectiveOverrideMapId =
                SpecialDungeonRunCoordinator.ResolveStartMapOverride(
                    run,
                    nextX,
                    nextY,
                    overrideMapId);
            var maze = DungeonData.GetDungeonMapMonsterSummaryInformation(
                run.DungeonId,
                nextX,
                nextY,
                run.MazeIndex,
                effectiveOverrideMapId,
                run.BossMapPos);
            if (overrideMapId <= 0
                && run.HellMode
                && run.HellMapId > 0
                && maze.X == run.HellMapX
                && maze.Y == run.HellMapY)
            {
                var hellMapId = run.HellMapId;
                effectiveOverrideMapId = hellMapId;
                if (hellMapId != maze.Index)
                    maze = DungeonData.GetDungeonMapMonsterSummaryInformation(run.DungeonId, maze.X, maze.Y, run.MazeIndex, hellMapId, run.BossMapPos);
                FileLogger.Log($"[DungeonHandler] START_MAP hell override: room=({maze.X},{maze.Y}) map={maze.Index}");
            }

            var roomKey = new RoomKey(maze.X, maze.Y, effectiveOverrideMapId);
            CacheQuestConnectedStartMapId(session, maze);

            byte[] startMapBody;
            List<KeyValuePair<int, int>> hellPartyMonsterInfoAfterStartMap = null;

            // 锁内绝不 await: 把 START_MAP 对 run 房间态(RoomKey/RoomStates/RoomKilledSeqIds/RoomMonsters/
            // MonsterCount)的整段读改写与队友击杀 relay(PropagateKillForClearAsync 在别的线程读这些结构)互斥,
            // 防 Dict/HashSet 跨线程并发改崩。此块 138-241 全为同步逻辑, 所有 await 发包都在 lock 之外。
            lock (run.SyncRoot)
            {
            run.RoomKey = roomKey;
            if (run.RoomStates.TryGetValue(roomKey, out var cached))
            {
                run.RoomMonsters = cached.Maze.Monsters;
                run.RoomStartSequence = cached.FirstSeqId;
                run.RoomKilledSeqIds = cached.KilledSeqIds;
                run.RoomLcg = cached.Lcg;
                run.Seed = cached.Seed;
                run.RoomKey = roomKey;
                TimeSpiralDungeonCoordinator.RestoreHiddenBoss(run, cached);

                startMapBody = DungeonNotificationBuilder.BuildStartMapRevisit(cached.Maze, cached.Seed);
                FileLogger.Log($"[DungeonHandler] START_MAP revisit: room=({maze.X},{maze.Y}) killed={cached.KilledSeqIds.Count}/{cached.MonsterCount} cleared={cached.IsCleared}");
            }
            else
            {
                var startSequence = run.MonsterCount;
                run.RoomStartSequence = (ushort)(startSequence + 1);
                // TODO：真实服务端房间切换时序号会出现跳号，当前仍按 firstMonsterSequence+index+1 近似。
                var seed = (uint)(ServerRandom.Next() & ~0x40000);
                run.Seed = seed;
                var lcg = new DnfLcg(seed);
                run.RoomLcg = lcg;
                var killedSet = new HashSet<ushort>();
                run.RoomKilledSeqIds = killedSet;

                var hellRoomInfo = run.HellRoomInfo;
                var isHellPartyRoom = run.HellMode
                    && hellRoomInfo != null
                    && effectiveOverrideMapId == hellRoomInfo.MapId
                    && maze.X == hellRoomInfo.X
                    && maze.Y == hellRoomInfo.Y;

                var startMapMaze = isHellPartyRoom
                    ? BuildHellPartyStartMapMaze(session, maze, hellRoomInfo)
                    : maze;

                if (!isHellPartyRoom)
                {
                    ApplyChampionPromotion(session, startMapMaze.Monsters);
                    SpecialDungeonRunCoordinator.AppendStartMapActors(
                        session,
                        startMapMaze);
                }

                run.RoomMonsters = startMapMaze.Monsters;

                var roomState = new RoomState
                {
                    Maze = startMapMaze,
                    FirstSeqId = run.RoomStartSequence,
                    MonsterCount = (ushort)CountServerTrackedMonsters(startMapMaze),
                    KilledSeqIds = killedSet,
                    Seed = seed,
                    Lcg = lcg,
                };
                run.RoomStates[roomKey] = roomState;
                TimeSpiralDungeonCoordinator.RegisterHiddenBossAfterStartMap(
                    session,
                    roomState);

                byte layeredFlag = (byte)(effectiveOverrideMapId > 0 ? 1 : 0);

                if (isHellPartyRoom)
                {
                    var state = run.RoomStates[roomKey];
                    state.IsHellPartyRoom = true;
                    state.HellPartyVeryDifficult = run.VeryDifficultHell;
                    state.HellPartyPillarObjectCode = hellRoomInfo.PillarObjectCode;
                    state.HellPartySpawnX = hellRoomInfo.SpawnX;
                    state.HellPartySpawnY = hellRoomInfo.SpawnY;
                    state.HellPartyWaves = hellRoomInfo.Waves;
                    state.HellPartyPhase = HellPartyPhase.WaitingStart;
                    state.HellPartyGroupRemaining = BuildHellPartyGroupRemaining(startMapMaze.Monsters);
                    var difficultyRule = hellRoomInfo.DifficultyRule;
                    FileLogger.Log($"[DungeonHandler] HELLPARTY room initialized: pillar={state.HellPartyPillarObjectCode} spawn=({state.HellPartySpawnX},{state.HellPartySpawnY}) waves={state.HellPartyWaves?.Count ?? 0} tracked={state.MonsterCount}/{startMapMaze.Monsters.Count} rewardRolls={difficultyRule?.RewardRollCount ?? 0} probability={difficultyRule?.Probability ?? 0} ratioProbability={difficultyRule?.RatioProbability ?? 0} groups={FormatHellPartyGroups(state.HellPartyGroupRemaining)}");
                    hellPartyMonsterInfoAfterStartMap = BuildHellPartyMonsterInfoEntries(hellRoomInfo);
                }

                // df_game_r：掉落物序号使用独立随机计数，和怪物序号分离。
                var itemSeqCounter = (ushort)ServerRandom.Next(60000);
                var extraEntries = GeneratePassiveObjectDrops(
                    run.DungeonId, run.MazeIndex,
                    ref itemSeqCounter);

                if (extraEntries != null)
                {
                    foreach (var e in extraEntries)
                        run.Drops[e.GlobalSeq] = e.ToDropInfo();
                }

                var ridableForRoom = GetRidableEntriesForRoom(session, maze.X, maze.Y);
                var hellPartyMapMode = run.HellMode ? run.HellPartyMode : (byte)0;
                var startMapFogFlag = run.HellMode ? (byte)1 : (byte)0;

                startMapBody = DungeonNotificationBuilder.BuildStartMap(startMapMaze, startSequence, (int)seed,
                    layeredRoomFlag: layeredFlag,
                    hellPartyMode: hellPartyMapMode,
                    hellPartyFogFlag: startMapFogFlag,
                    extraEntries: extraEntries,
                    ridableEntries: ridableForRoom);
                run.MonsterCount += (ushort)startMapMaze.Monsters.Count;
            }
            } // end lock(run.SyncRoot)

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001D, startMapBody));
            if (TowerOfDespairApcInfoBuilder.TryBuild(
                run.DungeonId,
                session.Player,
                out var towerBaseApcInfoBody,
                out var towerCurrentApcInfoBody))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.USER_APC_INFO_TOD,
                    towerBaseApcInfoBody));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.USER_APC_INFO_TOD,
                    towerCurrentApcInfoBody));
                FileLogger.Log(
                    $"[TowerOfDespair] base/current APC info sent after START_MAP: " +
                    $"dungeon={run.DungeonId} layers=0,{towerCurrentApcInfoBody[0]} " +
                    $"job={session.Player.Job} grow={session.Player.GrowType}");
            }
            await SpecialDungeonNotifier.SendStartMapStateAsync(session);

            if (hellPartyMonsterInfoAfterStartMap != null && hellPartyMonsterInfoAfterStartMap.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x02A6,
                    DungeonNotificationBuilder.BuildHellPartyMonsterInfo(hellPartyMonsterInfoAfterStartMap)));
                FileLogger.Log($"[DungeonHandler] HELLPARTY monster info sent after hell START_MAP: entries={hellPartyMonsterInfoAfterStartMap.Count} actorLevels={string.Join(",", hellPartyMonsterInfoAfterStartMap.Select(x => $"{x.Key}:{x.Value}"))}");
            }
        }

        private static void CacheQuestConnectedStartMapId(EnhancedClientSession session, DungeonData.MazeSumInfo maze)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !run.MazeQuestConnected)
                return;
            if (maze.X != run.MazeStartX || maze.Y != run.MazeStartY || maze.Index <= 0)
                return;

            run.MazeStartMapId = maze.Index;
        }

        private static List<KeyValuePair<int, int>> BuildHellPartyMonsterInfoEntries(DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            var seen = new HashSet<int>();
            var result = new List<KeyValuePair<int, int>>();
            if (hellRoomInfo?.Waves != null && hellRoomInfo.Waves.Count > 0)
            {
                foreach (var wave in hellRoomInfo.Waves)
                {
                    if (wave?.Monsters == null)
                        continue;

                    foreach (var monster in wave.Monsters)
                    {
                        if (monster.Code <= 0 || seen.Contains(monster.Code))
                            continue;

                        seen.Add(monster.Code);
                        result.Add(new KeyValuePair<int, int>(monster.Code, Math.Max(1, (int)monster.Level)));
                    }
                }
            }

            return result;
        }

        private static DungeonData.MazeSumInfo BuildHellPartyStartMapMaze(
            EnhancedClientSession session,
            DungeonData.MazeSumInfo maze,
            DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (hellRoomInfo == null || hellRoomInfo.NormalMapId <= 0)
                return maze;

            var run = session.Player.CurrentRun;
            if (run == null)
                return maze;

            try
            {
                var normalMaze = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    run.DungeonId,
                    hellRoomInfo.X,
                    hellRoomInfo.Y,
                    run.MazeIndex,
                    hellRoomInfo.NormalMapId,
                    run.BossMapPos);

                var monsters = new List<DungeonData.MonsterSumInfo>(
                    normalMaze.Monsters ?? new List<DungeonData.MonsterSumInfo>());
                ApplyChampionPromotion(session, monsters);
                var normalCount = monsters.Count;
                var hiddenCount = AppendHellPartyTemplateRows(monsters, hellRoomInfo);

                FileLogger.Log($"[DungeonHandler] HELLPARTY using normal room monsters: hellMap={maze.Index} normalMap={hellRoomInfo.NormalMapId} normal={normalCount} hidden={hiddenCount}");
                return new DungeonData.MazeSumInfo
                {
                    X = maze.X,
                    Y = maze.Y,
                    Index = maze.Index,
                    Monsters = monsters,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY normal room monster fallback: normalMap={hellRoomInfo.NormalMapId} error={ex.Message}");
            }

            return maze;
        }

        private static void ApplyChampionPromotion(EnhancedClientSession session, List<DungeonData.MonsterSumInfo> monsters)
        {
            if (monsters == null || monsters.Count == 0)
                return;

            var run = session.Player.CurrentRun;
            if (run == null)
                return;

            var champCount = DungeonData.GetChampionCount(
                run.DungeonId,
                run.Difficulty,
                run.MazeIndex,
                out var namedMonsters);
            DungeonData.PromoteChampions(monsters, champCount, namedMonsters);
        }

        private static int AppendHellPartyTemplateRows(List<DungeonData.MonsterSumInfo> monsters, DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (monsters == null || hellRoomInfo?.Waves == null)
                return 0;

            var hiddenCount = 0;
            foreach (var wave in hellRoomInfo.Waves)
            {
                if (wave?.Monsters == null || wave.Monsters.Count == 0)
                    continue;

                var order = wave.Order > 0 && wave.Order <= ushort.MaxValue
                    ? (ushort)wave.Order
                    : (ushort)0;
                var waveIndex = HellPartyAttachAllWavesSelector;

                foreach (var monster in wave.Monsters)
                {
                    var template = monster;
                    template.TemplateOrder = order;
                    template.PacketIndex = null;
                    template.Flag0 = HellPartyHiddenTemplateFlag;
                    template.Flag1 = waveIndex;
                    template.ExtraState = 0;
                    monsters.Add(template);
                    hiddenCount++;
                }

                FileLogger.Log($"[DungeonHandler] HELLPARTY template wave: order={order} index={waveIndex} group={wave.GroupId} count={wave.Monsters.Count} rows={string.Join(",", wave.Monsters.Select(x => $"{x.Code}:{x.Type}:{x.Level}:{waveIndex}"))}");
            }

            return hiddenCount;
        }

        private static Dictionary<int, int> BuildHellPartyGroupRemaining(IReadOnlyList<DungeonData.MonsterSumInfo> monsters)
        {
            var result = new Dictionary<int, int>();
            if (monsters == null)
                return result;

            for (var i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (!monster.IsHellPartyActor || monster.HellPartyGroupId <= 0)
                    continue;

                result.TryGetValue(monster.HellPartyGroupId, out var count);
                result[monster.HellPartyGroupId] = count + 1;
            }
            return result;
        }

        private static string FormatHellPartyGroups(Dictionary<int, int> groups)
        {
            if (groups == null || groups.Count == 0)
                return "-";

            return string.Join(",", groups.Select(x => $"{x.Key}={x.Value}"));
        }

        internal static int CountServerTrackedMonsters(DungeonData.MazeSumInfo maze)
        {
            if (maze.Monsters == null)
                return 0;

            return maze.Monsters.Count(monster => monster.Type != 9);
        }

        private static bool TryGetCurrentRoomState(EnhancedClientSession session, out RoomState roomState)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
            {
                roomState = null;
                return false;
            }

            return run.RoomStates.TryGetValue(run.RoomKey, out roomState);
        }

        internal static bool IsCurrentHellPartyLocked(EnhancedClientSession session)
        {
            if (!TryGetCurrentRoomState(session, out var roomState) || !roomState.IsHellPartyRoom)
                return false;

            return roomState.HellPartyPhase == HellPartyPhase.Started && !roomState.IsCleared;
        }

        internal Task HandleHellPartyStart(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryGetCurrentRoomState(session, out var roomState) || !roomState.IsHellPartyRoom)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: not in hell room cmd=0x{header.type:X4} bodyLen={body?.Length ?? 0}");
                return Task.CompletedTask;
            }

            if (roomState.HellPartyPhase == HellPartyPhase.WaitingStart)
            {
                roomState.HellPartyPhase = HellPartyPhase.Started;
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START: room=({roomState.Maze.X},{roomState.Maze.Y}) tracked={roomState.MonsterCount}/{roomState.Maze.Monsters.Count}");
            }
            else
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: phase={roomState.HellPartyPhase}");
            }

            return Task.CompletedTask;
        }

        internal static List<RidableObjectSpawnEntry> InitRidableObjects(MazeInfo maze)
        {
            var result = new List<RidableObjectSpawnEntry>();
            if (maze.RidableScript == null || maze.RidableScript.Objects.Count == 0)
                return result;

            var script = maze.RidableScript;
            var candidates = new List<RidableObject>(script.Objects);

            if (script.SelectCount > 0 && script.SelectCount < candidates.Count)
            {
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = ServerRandom.Next(i + 1);
                    var tmp = candidates[i];
                    candidates[i] = candidates[j];
                    candidates[j] = tmp;
                }
                candidates = candidates.GetRange(0, script.SelectCount);
            }

            foreach (var obj in candidates)
            {
                result.Add(new RidableObjectSpawnEntry
                {
                    ObjectIndex = obj.ObjectIndex,
                    MonsterIndex = 0,
                    PosX = obj.PosX,
                    PosY = obj.PosY,
                    Faction = obj.Faction,
                    MapX = (byte)obj.MapX,
                    MapY = (byte)obj.MapY,
                });
            }

            if (result.Count > 0)
                FileLogger.Log($"[DungeonHandler] RIDABLE: selected {result.Count}/{script.Objects.Count} objects (select={script.SelectCount})");

            return result;
        }

        private static List<RidableObjectSpawnEntry> GetRidableEntriesForRoom(
            EnhancedClientSession session, int roomX, int roomY)
        {
            var all = session.Player.CurrentRun?.RidableObjects;
            if (all == null || all.Count == 0) return null;
            var result = new List<RidableObjectSpawnEntry>();
            foreach (var r in all)
            {
                if (r.MapX == roomX && r.MapY == roomY)
                    result.Add(r);
            }
            return result.Count > 0 ? result : null;
        }

        private static List<PassiveObjectDropEntry> GeneratePassiveObjectDrops(
            int dungeonId, int mazeIndex, ref ushort itemSeqCounter)
        {
            try
            {
                var dgn = DungeonData.GetDungeonFile(dungeonId);
                if (dgn.SpecialPassiveObjectItems.Count == 0) return null;

                var result = new List<PassiveObjectDropEntry>();

                foreach (var item in dgn.SpecialPassiveObjectItems)
                {
                    int roll = ServerRandom.Next(10000);
                    if (roll >= item.DropRate) continue;

                    itemSeqCounter++;
                    var drop = DropInfo.CreateItem(itemSeqCounter, item.ItemId, 1);
                    result.Add(new PassiveObjectDropEntry
                    {
                        ObjectIndex = (byte)item.Index,
                        GlobalSeq = itemSeqCounter,
                        ItemId = drop.TemplateId,
                        StackCount = drop.StackCount,
                        Endurance = drop.Endurance,
                        Core = drop.Core != null ? drop.Core.Copy() : null,
                    });
                }

                if (result.Count > 0)
                    FileLogger.Log($"[DungeonHandler] PASSIVE_OBJ_DROP: {result.Count} items generated for dungeon={dungeonId}");
                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] GeneratePassiveObjectDrops ERROR: {ex.Message}");
                return null;
            }
        }
    }
}
