using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonEntryHandler
    {
        private const int GorgeousChallengeGoldCost = 190000;
        internal const ushort StartGameResponseType = 0x000F;
        internal const byte MercenaryContentErrorCode = 0xEB;

        private readonly DungeonSharedServices _svc;
        private readonly DungeonMapHandler _mapHandler;

        internal DungeonEntryHandler(DungeonSharedServices svc, DungeonMapHandler mapHandler)
        {
            _svc = svc;
            _mapHandler = mapHandler;
        }

        internal async Task HandleEnterSelectDungeon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: cid={session.Player.CharacterId} uid={session.Player.UserId} town={session.Player.CurTownId} area={session.Player.CurAreaId}");
            if (_svc.MercenaryRestrictions != null
                && !_svc.MercenaryRestrictions.CanEnterContent(session.Player.CharacterId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: " +
                    $"MERCENARY_CONTENT_BLOCKED cid={session.Player.CharacterId}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    StartGameResponseType,
                    BuildMercenaryContentErrorBody()));
                return;
            }

            try
            {
                var selection = BeginDungeonSelection(session.Player);
                if (selection != null)
                {
                    var anchor = selection.ReturnAnchor;
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON return anchor: " +
                        $"selection={selection.SelectionId} town={anchor.TownId} " +
                        $"area={anchor.AreaId} pos=({anchor.X},{anchor.Y})");
                }
                session.Player.UserState = 0x01;

                var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                snapshot.AreaId = 0xFF;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snapshot)));

                // NOTI 0x0002 subtype1 (ADDITION): dynamically built from structured table (same path as init flow)
                int cid = session.Player.CharacterId;
                HonorLevelSummary honorSummary = null;
                if (cid <= 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: CharacterId<=0, USERINFO not sent");
                }
                else
                {
                    var record = _svc.CharacterRepository.GetById(cid);
                    var addition = _svc.Subtype1Repository.HasData(cid) ? _svc.Subtype1Repository.Load(cid) : null;
                    if (record != null && addition != null)
                    {
                        var accountId = session.Account?.AccountId ?? record.AccountId;
                        var accountCharacters = _svc.CharacterRepository.ListByAccount(accountId);
                        honorSummary = _svc.HonorLevel.LoadSummary(accountId, accountCharacters);
                        AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, accountCharacters);
                        _svc.HonorLevel.ApplyToUserInfoAddition(
                            addition, accountId, accountCharacters, honorSummary);
                        var skillSnap = _svc.ProgressNotifications
                            .LoadSyncedSkillState(cid, record.Level).Skills;
                        var w = new GamePacketWriter();
                        w.WriteByte(1); // subtype 1 ADDITION
                        w.WriteUInt16(1);
                        w.WriteUInt16((ushort)record.CharacterId);
                        w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skillSnap));
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, w.ToArray()));
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: NOTI 2 type1 dynamic body");
                    }
                    else
                    {
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: record={record != null} addition={addition != null}, USERINFO not sent (no fallback)");
                    }
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003, EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001A, UdpHostBuilder.BuildUnavailable()));
                var towerOfDespairFloor = 1;
                if (!_svc.TowerOfDespairProgress.TryGetNextFloor(
                        session.Player.CharacterId,
                        out towerOfDespairFloor,
                        out var towerProgressError))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON tower floor fallback: " +
                        $"cid={session.Player.CharacterId} " +
                        $"error={towerProgressError?.Message}");
                }
                await _svc.PersistentMechanisms.RestoreBeforeSelectionAsync(session);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x001B,
                    EnterSelectDungeonStateBuilder.BuildEnterSelectDungeon(
                        session.Player,
                        towerOfDespairFloor)));
                await _svc.GrowthCapsuleSync.SendExpProgressAsync(
                    session, "enter-select-dungeon", honor: honorSummary);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: state packets and account EXP progress sent OK");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON EXCEPTION: {ex}");
            }
        }

        private static DungeonSelectionContext BeginDungeonSelection(
            Game.Session.PlayerContext player)
        {
            if (player == null)
                return null;

            var townId = player.CurTownId;
            var areaId = player.CurAreaId;
            var x = player.CurPosX;
            var y = player.CurPosY;
            if (Town.TryGetDungeonGateReturnInfo(
                    townId,
                    areaId,
                    out var configured))
            {
                townId = configured.Town;
                areaId = configured.Area;
                x = configured.X;
                y = configured.Y;
            }

            return player.BeginDungeonSelection(new DungeonTownReturnAnchor(
                townId,
                areaId,
                x,
                y,
                player.CurDirection,
                player.CurAreaState));
        }

        internal Task HandleSelectDungeon(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => HandleSelectDungeonCore(
                session,
                header,
                body,
                linkedSourceDungeonId: 0,
                expectedPredecessorIdentity: null);

        private async Task HandleSelectDungeonCore(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int linkedSourceDungeonId,
            DungeonRunIdentity? expectedPredecessorIdentity)
        {
            var req = Network.Parsers.Dungeon.SelectDungeonRequest.Parse(body);
            var predecessorRun = session?.Player?.CurrentRun;
            var predecessorGeneration =
                session?.Player?.CurrentDungeonRunGeneration ?? 0;
            if (expectedPredecessorIdentity.HasValue
                && (predecessorRun == null
                    || !predecessorRun.Matches(
                        expectedPredecessorIdentity.Value)))
            {
                return;
            }
            try
            {
                var resolvedDungeonId = _svc.TowerOfDespairProgress.ResolveEntryDungeonId(
                    session.Player.CharacterId,
                    req.DungeonId);
                if (resolvedDungeonId != req.DungeonId)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TOWER_OF_DESPAIR_ENTRY: cid={session.Player.CharacterId} requested={req.DungeonId} resolved={resolvedDungeonId}");
                    req = new Network.Parsers.Dungeon.SelectDungeonRequest(
                        (ushort)resolvedDungeonId,
                        req.Difficulty,
                        req.Flag1,
                        req.Flag2);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TOWER_OF_DESPAIR_ENTRY ERROR: cid={session.Player.CharacterId} requested={req.DungeonId}: {ex.Message}");
            }

            linkedSourceDungeonId =
                await ResolveLinkedDungeonSelectionSourceAsync(
                    session,
                    header,
                    req.DungeonId,
                    req.Difficulty,
                    linkedSourceDungeonId);
            if (linkedSourceDungeonId < 0)
            {
                return;
            }
            if (!IsRunSlotUnchanged(
                    session,
                    predecessorRun,
                    predecessorGeneration))
            {
                return;
            }

            List<ActiveQuest> activeQuests = null;
            HashSet<int> activeQuestIds = null;
            HashSet<int> clearedQuestIds = null;
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                activeQuests = QuestService.LoadActiveQuests(
                    connStr,
                    session.Player.CharacterId);
                if (activeQuests.Count > 0)
                {
                    activeQuestIds = new HashSet<int>(
                        activeQuests.ConvertAll(q => (int)q.QuestId));
                }
                var clearedFlags = new Game.Quests.QuestRepository(connStr)
                    .LoadClearedFlags(session.Player.CharacterId);
                if (clearedFlags.Count > 0)
                    clearedQuestIds = new HashSet<int>(clearedFlags.Keys);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SELECT_DUNGEON ERROR: " +
                    $"quest load failed: {ex.Message}");
            }

            if (!WorldMap.IsTaskExclusiveDungeonAvailable(
                    req.DungeonId,
                    activeQuestIds))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON task-exclusive entry rejected: " +
                    $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                    $"requiredQuests={string.Join(",", WorldMap.GetTaskExclusiveQuestIds(req.DungeonId))}");
                await SendDungeonSelectionErrorAsync(session, header);
                return;
            }

            // 塔类副本分流: dungeonKind==1 走专属流程(NOTI 142+143, 非普通副本的 START_MAP)
            if (_svc.DeathTower.TryCreateSession(req.DungeonId, out var tower))
            {
                await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                    session,
                    "select_tower_replace_run");
                if (!IsRunSlotUnchanged(
                        session,
                        predecessorRun,
                        predecessorGeneration))
                {
                    return;
                }
                DungeonRunLifecycle.BeginTowerRun(
                    session,
                    req.DungeonId,
                    tower,
                    req.Difficulty,
                    _svc.InstanceRegistry);
                var towerRunIdentity = session.Player.CurrentRun?.CaptureIdentity()
                    ?? default(DungeonRunIdentity);
                await _svc.DeathTower.SendEntryPacketsAsync(session, tower, req.Difficulty);
                if (!session.Player.IsCurrentDungeonRun(towerRunIdentity))
                    return;
                return;
            }

            await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                session,
                "select_dungeon_replace_run");
            if (!IsRunSlotUnchanged(
                    session,
                    predecessorRun,
                    predecessorGeneration))
            {
                return;
            }
            DungeonRunLifecycle.BeginRun(
                session,
                req.DungeonId,
                req.Difficulty,
                instanceRegistry: _svc.InstanceRegistry);
            var run = session.Player.CurrentRun;
            var runIdentity = run.CaptureIdentity();
            run.HellMode = req.HellPartyRequestFlag != 0 && DungeonData.IsHellDungeon(req.DungeonId);

            WarmUpDropConfigs(run.HellMode);

            if (req.HellPartyRequestFlag != 0)
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: manual hell requested dungeon={req.DungeonId} enabled={run.HellMode}");

            run.QuestSnapshot = QuestRunSnapshot.Capture(activeQuests);
            string mazeSelectionDiagnostic = null;
            var selection = DungeonData.SelectDungeonMaze(
                req.DungeonId,
                req.Difficulty,
                activeQuestIds,
                clearedQuestIds,
                diagnostic => mazeSelectionDiagnostic = diagnostic);
            run.MazeIndex = selection.Index;
            run.MazeQuestConnected = DungeonData.IsQuestConnectedSelection(
                req.DungeonId,
                selection.Maze,
                activeQuestIds,
                req.Difficulty);
            var startPos = DungeonData.RandomizeStartPosition(selection.Maze.StartMap);
            run.MazeStartX = startPos != null ? startPos[0] : -1;
            run.MazeStartY = startPos != null ? startPos[1] : -1;
            run.MazeStartMapId = ResolveMazeStartMapId(
                selection.Maze,
                run.MazeStartX,
                run.MazeStartY);
            var bossPos = DungeonData.RandomizeBossPosition(selection.Maze.BossMap);
            run.BossMapPos = bossPos;
            var bossMapId = bossPos != null && bossPos.Length >= 2
                ? ResolveMazeStartMapId(selection.Maze, bossPos[0], bossPos[1])
                : 0;
            FileLogger.Log(
                $"[DungeonHandler] SELECT_DUNGEON route: " +
                $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                $"{mazeSelectionDiagnostic ?? $"difficulty={req.Difficulty} selectedMaze={selection.Index}"} " +
                $"questConnected={run.MazeQuestConnected} " +
                $"start=({run.MazeStartX},{run.MazeStartY}) startMap={run.MazeStartMapId} " +
                $"boss=({(bossPos != null && bossPos.Length >= 2 ? bossPos[0] : -1)}," +
                $"{(bossPos != null && bossPos.Length >= 2 ? bossPos[1] : -1)}) bossMap={bossMapId}");
            run.RidableObjects = DungeonMapHandler.InitRidableObjects(selection.Maze);
            run.ClearCondition = new ClearConditionState(selection.Maze.ClearConditions);
            DungeonMechanismCoordinator.ConfigureSelection(
                session,
                selection.Maze,
                bossPos,
                activeQuests,
                "select_dungeon");
            ConfigureLinkedDungeonRunState(req.DungeonId, run);
            if (run.HellMode)
                await PrepareManualHellPartyAsync(session, req, selection.Maze, selection.Index);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var selectionSnapshot = CaptureSelectionSnapshot(
                run,
                selection.Maze,
                ResolveEntryPartyMemberCount(session));
            if (!run.Instance.TryFreezeSelection(selectionSnapshot))
                throw new InvalidOperationException("Dungeon selection was already frozen for this instance.");
            if (!run.TryActivate())
                throw new InvalidOperationException("Dungeon run could not enter the active state after selection.");
            RegisterActiveParticipant(session, run);

            if (run.ClearCondition.HasConditions)
                FileLogger.Log($"[DungeonHandler] ClearCondition init: {selection.Maze.ClearConditions.Count} conditions, totalRequired={run.ClearCondition.TotalRequired}");
            else
                FileLogger.Log($"[DungeonHandler] WARNING: dungeon={req.DungeonId} maze={selection.Index} has no [clear condition]");
            await SendDungeonSelectPacketsTo(session, req, bossPos, (byte)selection.Index);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            // ★组队副本联机: 队长进本时把整队队员也驱动进【同一实例】。⚠️待真机验证(见 DFO_PARTY_DUNGEON_COOP)。
            await TryFanOutDungeonEntryToPartyAsync(session, header, req, bossPos, (byte)selection.Index);
        }

        internal static byte[] BuildMercenaryContentErrorBody()
            => CommonPacketBodyBuilder.BuildCmdError(MercenaryContentErrorCode);

        internal async Task EnterLinkedDungeonAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int dungeonId,
            byte difficulty)
        {
            if (session?.Player == null
                || dungeonId <= 0
                || dungeonId > ushort.MaxValue)
            {
                return;
            }

            var sourceRun = session.Player.CurrentRun;
            if (sourceRun == null)
                return;
            var sourceRunIdentity = sourceRun.CaptureIdentity();
            var sourceDungeonId = sourceRun.DungeonId;
            if (!DungeonData.CanEnterLinkedDungeonFrom(
                    dungeonId,
                    sourceDungeonId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"LINKED_DUNGEON enter rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"source={sourceDungeonId} target={dungeonId}");
                return;
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"LINKED_DUNGEON enter next: " +
                $"cid={session.Player.CharacterId} " +
                $"source={sourceDungeonId} dungeon={dungeonId} " +
                $"diff={difficulty}");
            if (!session.Player.IsCurrentDungeonRun(sourceRunIdentity))
                return;
            await HandleSelectDungeonCore(
                session,
                header,
                BuildLinkedDungeonSelectBody(dungeonId, difficulty),
                sourceDungeonId,
                sourceRunIdentity);
        }

        internal static byte[] BuildLinkedDungeonSelectBody(
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));

            return new[]
            {
                (byte)(dungeonId & 0xFF),
                (byte)((dungeonId >> 8) & 0xFF),
                difficulty,
                (byte)0,
                (byte)0,
            };
        }

        internal static bool IsLinkedDungeonSelectionAllowed(
            IReadOnlyCollection<int> previousDungeonIds,
            int linkedSourceDungeonId)
        {
            if (previousDungeonIds == null || previousDungeonIds.Count == 0)
                return linkedSourceDungeonId <= 0;
            if (linkedSourceDungeonId <= 0)
                return false;

            foreach (var previousDungeonId in previousDungeonIds)
            {
                if (previousDungeonId == linkedSourceDungeonId)
                    return true;
            }

            return false;
        }

        private static async Task<int> ResolveLinkedDungeonSelectionSourceAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int dungeonId,
            byte difficulty,
            int linkedSourceDungeonId)
        {
            var previousDungeonIds =
                DungeonData.GetLinkedDungeonPreviousIds(dungeonId);

            // A server-internal transition already carries its predecessor. Any
            // notification authorization for the same transition is now stale.
            if (linkedSourceDungeonId > 0)
            {
                LinkedDungeonEntryAuthorizationStore.Clear(session?.Player);
                if (IsLinkedDungeonSelectionAllowed(
                        previousDungeonIds,
                        linkedSourceDungeonId))
                {
                    return linkedSourceDungeonId;
                }

                LogLinkedDungeonSelectionRejected(
                    session,
                    dungeonId,
                    linkedSourceDungeonId,
                    previousDungeonIds,
                    "internal predecessor mismatch");
                return -1;
            }

            if (previousDungeonIds.Count == 0)
            {
                // Choosing an ordinary dungeon abandons any pending linked offer.
                // The ordinary selection itself remains valid.
                LinkedDungeonEntryAuthorizationStore.TryConsume(
                    session?.Player,
                    dungeonId,
                    difficulty,
                    out _,
                    out var discardReason);
                if (!string.Equals(
                        discardReason,
                        "no authorization",
                        StringComparison.Ordinal))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON discarded linked authorization: " +
                        $"cid={session?.Player?.CharacterId ?? 0} " +
                        $"target={dungeonId} diff={difficulty} " +
                        $"reason={discardReason}");
                }
                return 0;
            }

            if (!LinkedDungeonEntryAuthorizationStore.TryConsume(
                    session?.Player,
                    dungeonId,
                    difficulty,
                    out linkedSourceDungeonId,
                    out var authorizationReason))
            {
                LogLinkedDungeonSelectionRejected(
                    session,
                    dungeonId,
                    linkedSourceDungeonId,
                    previousDungeonIds,
                    authorizationReason);
                await SendDungeonSelectionErrorAsync(
                    session,
                    header);
                return -1;
            }

            if (IsLinkedDungeonSelectionAllowed(
                    previousDungeonIds,
                    linkedSourceDungeonId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON linked authorization consumed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"source={linkedSourceDungeonId} target={dungeonId} " +
                    $"diff={difficulty}");
                return linkedSourceDungeonId;
            }

            LogLinkedDungeonSelectionRejected(
                session,
                dungeonId,
                linkedSourceDungeonId,
                previousDungeonIds,
                "PVF predecessor mismatch");
            await SendDungeonSelectionErrorAsync(
                session,
                header);
            return -1;
        }

        private static void LogLinkedDungeonSelectionRejected(
            EnhancedClientSession session,
            int dungeonId,
            int linkedSourceDungeonId,
            IReadOnlyCollection<int> previousDungeonIds,
            string reason)
        {
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"SELECT_DUNGEON linked destination rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"source={linkedSourceDungeonId} target={dungeonId} " +
                $"prev={string.Join(",", previousDungeonIds)} " +
                $"reason={reason}");
        }

        private static Task SendDungeonSelectionErrorAsync(
            EnhancedClientSession session,
            GamePacketHeader header)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CommonPacketBodyBuilder.BuildCmdError(0x11)));
        }

        // 给指定会话发送 SELECT_DUNGEON 出站序列；秘密商店 NPC 上下文只在通关后发送。
        // Hell 等参数从该会话自己的 CurrentRun 读(队员的 run 已拷贝队长 selection)。
        private async Task SendDungeonSelectPacketsTo(
            EnhancedClientSession s,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            int[] bossPos,
            byte mazeModeFlag)
        {
            var run = s.Player.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();
            var extraPairGroups =
                DungeonMechanismCoordinator.ResolveSelectionMinimapIconGroups(
                    run,
                    req.DungeonId,
                    mazeModeFlag);
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001C, DungeonNotificationBuilder.BuildDungeonInfo(
                dungeonId: req.DungeonId,
                difficulty: req.Difficulty,
                modeFlag: mazeModeFlag,
                bossX: bossPos != null ? (byte)bossPos[0] : (byte)0,
                bossY: bossPos != null ? (byte)bossPos[1] : (byte)0,
                hellPartyRoomX: run.HellMode ? run.HellMapX : (byte)0xFF,
                hellPartyRoomY: run.HellMode ? run.HellMapY : (byte)0xFF,
                dungeonMode: 0,
                extraPairGroups: extraPairGroups,
                hellPartyEnabled: run.HellMode ? (ushort)1 : (ushort)0,
                value2: run.HellMode ? (byte)0x0B : (byte)0,
                flagA: extraPairGroups != null ? (byte)1 : (byte)0)));
            if (!s.Player.IsCurrentDungeonRun(runIdentity))
                return;

            await DungeonMechanismCoordinator.SendSelectionStateAsync(
                s,
                "after_dungeon_info");
            if (!s.Player.IsCurrentDungeonRun(runIdentity))
                return;
            var hasSelectedStart = run.MazeStartX >= 0 && run.MazeStartY >= 0;
            var startRoomIdentity = await _mapHandler.SendStartMapAsync(
                s,
                run,
                hasSelectedStart ? run.MazeStartX : 0xFF,
                hasSelectedStart ? run.MazeStartY : 0xFF,
                overrideMapId: -1);
            if (!startRoomIdentity.HasValue
                || !s.Player.IsCurrentDungeonRoom(startRoomIdentity.Value))
                return;

            if (StrikerSupportTagCharacterPacketBuilder.TryBuildOwnerSupportBody(s.Player.CharacterId, out var strikerBody))
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x019F, strikerBody));
            else
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x019F,
                    StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()));
        }

        // ★组队副本联机 fan-out(⚠️协议+客户端渲染, 待真机验证; DFO_PARTY_DUNGEON_COOP=0 可隔离):
        // df 模型=队长进本调 CParty::dungeon_start, 建【一个共享实例】广播全队、goto_dungeon 把每个队员推进去。
        // 队员是【服务端驱动】换图、不走传送门→不触发本地"该地下城已锁定"门。这里复刻: 拷队长迷宫 selection
        // 到每个队员 run(同一实例) → 给队员重放 SELECT 序列 → 全队进入同一副本实例。
        private async Task TryFanOutDungeonEntryToPartyAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            int[] bossPos,
            byte mazeModeFlag)
        {
            if (System.Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            var pm = _svc.PartyManager;
            var sessions = _svc.Sessions;
            if (pm == null || sessions == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;   // 队伍成员 UserId==(ushort)CharacterId(见 BuildMember)
            var party = pm.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            var lr = leader.Player.CurrentRun;
            if (lr == null)
                return;
            var leaderRunIdentity = lr.CaptureIdentity();
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} dungeon={req.DungeonId} → fan-out");
            foreach (var m in party.MembersBySlot())
            {
                if (m.UserId == leaderUid) continue;
                sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member uid={m.UserId} 不在线/无会话, 跳过");
                    continue;
                }
                try
                {
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    var memberPredecessorRun = bs.Player.CurrentRun;
                    var memberPredecessorGeneration =
                        bs.Player.CurrentDungeonRunGeneration;
                    // ★前奏: 队员从没"打开副本选择页", 直接收 SELECT 会半悬空(显示进房间但不真换图)。
                    //   先给队员补发 ENTER_SELECT(0x17/0x02/0x03/0x1A/0x1B, =A 发 0x000F 时收到的),
                    //   让其客户端进入"进副本"状态, 再重放 SELECT 才能真换图。
                    await HandleEnterSelectDungeon(bs, header, System.Array.Empty<byte>());
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    if (!IsRunSlotUnchanged(
                            bs,
                            memberPredecessorRun,
                            memberPredecessorGeneration))
                        continue;

                    await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                        bs,
                        "party_select_dungeon_replace_run");
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    if (!IsRunSlotUnchanged(
                            bs,
                            memberPredecessorRun,
                            memberPredecessorGeneration))
                        continue;
                    DungeonRunLifecycle.BeginRun(
                        bs,
                        req.DungeonId,
                        req.Difficulty,
                        lr.Instance,
                        _svc.InstanceRegistry);
                    var br = bs.Player.CurrentRun;
                    if (br == null)
                        continue;
                    var memberRunIdentity = br.CaptureIdentity();
                    var sharedSelection = lr.Instance.Selection;
                    if (sharedSelection == null)
                        throw new InvalidOperationException("Party dungeon selection snapshot is missing.");
                    sharedSelection.ApplyTo(br);
                    br.HellMode = lr.HellMode;
                    br.HellPartyMode = lr.HellPartyMode;
                    br.HellMapId = lr.HellMapId;
                    br.HellMapX = lr.HellMapX;
                    br.HellMapY = lr.HellMapY;
                    br.HellRoomInfo = lr.HellRoomInfo;
                    br.LinkedDungeonNextId = lr.LinkedDungeonNextId;
                    br.LinkedDungeonNextRate = lr.LinkedDungeonNextRate;
                    br.LinkedDungeonNextCondition =
                        lr.LinkedDungeonNextCondition;
                    DungeonMechanismCoordinator.CloneSelection(
                        bs,
                        lr,
                        br,
                        "party_select_dungeon");
                    if (!br.TryActivate())
                        throw new InvalidOperationException("Party member run could not enter the active state.");
                    RegisterActiveParticipant(bs, br);
                    bs.Player.UserState = 0x01;
                    await SendDungeonSelectPacketsTo(bs, req, bossPos, mazeModeFlag);
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    if (!bs.Player.IsCurrentDungeonRun(memberRunIdentity))
                        continue;
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member cid={bs.Player.CharacterId} 驱动进副本 maze={br.MazeIndex}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member uid={m.UserId} 驱动异常: {ex.Message}");
                }
            }
        }

        internal Task HandleGorgeousChallengeToggle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null)
                return Task.CompletedTask;

            var enabled = ParseGorgeousChallengeEnabled(body);
            session.Player.HellPartyGorgeousChallengeEnabled = enabled;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GORGEOUS_CHALLENGE_TOGGLE: enabled={enabled} cmd=0x{header.cmd:X2} type=0x{header.type:X4} bodyLen={body?.Length ?? 0} body={(body != null ? BitConverter.ToString(body) : string.Empty)}");
            return Task.CompletedTask;
        }

        private static byte ResolveHellPartyMode(byte requestFlag)
        {
            if (requestFlag == 1 || requestFlag == 2)
                return requestFlag;

            return HellPartyData.PickManualHellPartyMode();
        }

        private static void ConfigureLinkedDungeonRunState(
            int dungeonId,
            DungeonRun run)
        {
            if (run == null)
                return;

            run.LinkedDungeonNextId = 0;
            run.LinkedDungeonNextRate = 0;
            run.LinkedDungeonNextCondition = 0;

            if (!DungeonData.SupportsLinkedDungeonContinue(dungeonId))
                return;

            var next = DungeonData.PickLinkedDungeonNext(dungeonId);
            if (next == null)
                return;

            run.LinkedDungeonNextId = next.DungeonId;
            run.LinkedDungeonNextRate = next.Rate;
            run.LinkedDungeonNextCondition = next.Condition;
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"LINKED_DUNGEON next selected: dungeon={dungeonId} " +
                $"next={next.DungeonId} rate={next.Rate} " +
                $"condition={next.Condition}");
        }

        private static bool ParseGorgeousChallengeEnabled(byte[] body)
        {
            if (body == null || body.Length <= 13)
                return false;

            // 86 client CMD 0x03B6: body[12] is always 7; body[13] is 0 for checked, 1 for unchecked.
            return body[13] == 0;
        }

        private static int ResolveMazeStartMapId(
            PvfLib.MazeInfo maze,
            int startX,
            int startY)
        {
            if (maze == null
                || startX < 0
                || startY < 0
                || maze.MapSpecifications == null)
                return 0;

            foreach (var spec in maze.MapSpecifications)
            {
                if (spec.X != startX || spec.Y != startY || spec.Index <= 0)
                    continue;
                return spec.Index;
            }

            return 0;
        }

        private static DungeonSelectionSnapshot CaptureSelectionSnapshot(
            DungeonRun run,
            PvfLib.MazeInfo maze,
            int partyMemberCount)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            return new DungeonSelectionSnapshot
            {
                MazeIndex = run.MazeIndex,
                MazeQuestConnected = run.MazeQuestConnected,
                MazeStartMapId = run.MazeStartMapId,
                MazeStartX = run.MazeStartX,
                MazeStartY = run.MazeStartY,
                TotalRoomCount = DungeonRoomTopology.CountConfiguredRooms(maze),
                PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount)),
                BossMapPosition = run.BossMapPos == null
                    ? null
                    : (int[])run.BossMapPos.Clone(),
                RidableObjects = run.RidableObjects == null
                    ? Array.Empty<RidableObjectSpawnEntry>()
                    : run.RidableObjects.ToArray(),
                ClearConditionTemplate = run.ClearCondition?.CloneFresh(),
            };
        }

        private int ResolveEntryPartyMemberCount(EnhancedClientSession session)
        {
            var party = session?.Player == null
                ? null
                : _svc.PartyManager?.GetPartyByUser(session.Player.UserId);
            return party == null
                ? 1
                : Math.Max(1, Math.Min(4, party.Count));
        }

        private async Task PrepareManualHellPartyAsync(
            EnhancedClientSession session,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            PvfLib.MazeInfo maze,
            int mazeIndex)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();
            var area = WorldMap.GetAreaByDungeonId(req.DungeonId);
            var dungeonMinLevel = DungeonData.GetDungeonMinimumRequiredLevel(req.DungeonId);
            var hellPartyMode = ResolveHellPartyMode(req.HellPartyDifficultyFlag);
            DungeonData.HellPartyRoomInfo hellRoom = null;
            var gorgeousGoldBefore = 0;
            var gorgeousGoldAfter = -1;
            var gorgeousCanApply = false;
            if (!TryGetOwnedInventoryLease(session, out var inventoryLease))
            {
                DisableCurrentHellParty(run);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: online inventory missing for hell entry cid={session.Player.CharacterId}");
                return;
            }

            if (session.Player.HellPartyGorgeousChallengeEnabled)
            {
                var veryHardRoom = DungeonData.FindHellMapRoom(req.DungeonId, maze, mazeIndex, 1);
                gorgeousGoldBefore = ReadGold(inventoryLease);
                if (veryHardRoom.Found && gorgeousGoldBefore >= GorgeousChallengeGoldCost)
                {
                    hellPartyMode = 1;
                    hellRoom = veryHardRoom;
                    gorgeousCanApply = true;
                }
                else
                {
                    hellPartyMode = HellPartyData.PickManualHellPartyMode();
                    if (!veryHardRoom.Found)
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge ignored, very hard hell room missing dungeon={req.DungeonId}");
                    else
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge insufficient gold need={GorgeousChallengeGoldCost} have={gorgeousGoldBefore}, use weighted hell difficulty mode={hellPartyMode}");
                }
            }

            if (hellRoom == null || !hellRoom.Found)
                hellRoom = DungeonData.FindHellMapRoom(req.DungeonId, maze, mazeIndex, hellPartyMode);

            if (hellRoom == null || !hellRoom.Found)
            {
                DisableCurrentHellParty(run);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell requested but no hell map found dungeon={req.DungeonId}");
                return;
            }

            EntryCostResult ticketResult;
            lock (inventoryLease.SyncRoot)
                ticketResult = _svc.EntryCost.TryConsumeAbyssPartyTicket(
                    inventoryLease.Inventory,
                    area,
                    dungeonMinLevel);
            if (!ticketResult.Success)
            {
                DisableCurrentHellParty(run);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell ticket check failed dungeon={req.DungeonId} area={area?.AreaId ?? -1} minLevel={dungeonMinLevel} reason={ticketResult.FailReason}");
                return;
            }

            var gorgeousApplied = false;
            if (gorgeousCanApply)
            {
                if (TrySpendGold(inventoryLease, GorgeousChallengeGoldCost, out gorgeousGoldBefore, out gorgeousGoldAfter))
                {
                    gorgeousApplied = true;
                    run.HellGorgeousChallenge = true;
                }
                else
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge spend failed after ticket, keep selected hell mode need={GorgeousChallengeGoldCost} have={gorgeousGoldBefore}");
                }
            }

            run.HellPartyMode = hellPartyMode;
            run.VeryDifficultHell = run.HellPartyMode == 1;
            run.HellMapId = hellRoom.MapId;
            run.HellMapX = (byte)hellRoom.X;
            run.HellMapY = (byte)hellRoom.Y;
            run.HellRoomInfo = hellRoom;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell room=({hellRoom.X},{hellRoom.Y}) map={hellRoom.MapId} normalMap={hellRoom.NormalMapId} waves={hellRoom.Waves.Count} requestFlag={req.HellPartyRequestFlag} difficultyFlag={req.HellPartyDifficultyFlag} mode={run.HellPartyMode} veryDifficult={run.VeryDifficultHell} area={area?.AreaId ?? -1} minLevel={dungeonMinLevel} ticket={(ticketResult.IsFreePass ? "freepass" : "normal")} updates={ticketResult.ConsumedItems.Count}");

            await SendHellPartyTicketUpdates(
                session,
                runIdentity,
                ticketResult);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;
            if (gorgeousApplied && gorgeousGoldAfter >= 0)
            {
                if (_svc.InventoryRefresh != null)
                    await _svc.InventoryRefresh.SendGoldUpdate(session);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge applied cost={GorgeousChallengeGoldCost} gold={gorgeousGoldBefore}->{gorgeousGoldAfter}");
            }
        }

        private async Task SendHellPartyTicketUpdates(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            EntryCostResult ticketResult)
        {
            foreach (var update in ticketResult.ConsumedItems)
            {
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;
                if (_svc.InventoryRefresh != null)
                    await _svc.InventoryRefresh.SendUpdateItemList(session, InventoryListType.Main, update.SlotIndex);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell ticket consumed item={update.ItemId} count={update.Count} slot={update.SlotIndex} remain={update.RemainingCount}");
            }
        }

        private static void DisableCurrentHellParty(DungeonRun run)
        {
            if (run == null) return;
            run.HellMode = false;
            run.HellPartyMode = 0;
            run.VeryDifficultHell = false;
            run.HellGorgeousChallenge = false;
            run.HellMapId = -1;
            run.HellMapX = 0xFF;
            run.HellMapY = 0xFF;
            run.HellRoomInfo = null;
        }

        private static void WarmUpDropConfigs(bool includeHellParty)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (includeHellParty)
                        DropService.WarmUpAbyssParty();
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_CONFIG_WARMUP ERROR: {ex.Message}");
                }
            });
        }

        private static int ReadGold(InventoryLease lease)
        {
            if (lease == null)
                return 0;

            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(0);
        }

        private static bool IsRunSlotUnchanged(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            long expectedGeneration)
        {
            var player = session?.Player;
            return player != null
                && player.CurrentDungeonRunGeneration == expectedGeneration
                && ReferenceEquals(player.CurrentRun, expectedRun);
        }

        private void RegisterActiveParticipant(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null || run == null)
                return;

            var (characterId, accountId) =
                SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || accountId <= 0)
            {
                FileLogger.Log(
                    $"[DungeonInstanceRegistry] registration skipped " +
                    $"cid={characterId} aid={accountId} " +
                    $"instance={run.PartyDungeonInstanceId}");
                return;
            }

            var party = _svc.PartyManager?.GetPartyByUser(
                session.Player.UserId);
            var attachment = _svc.InstanceRegistry.RegisterActive(
                new DungeonParticipantRegistration(
                    accountId,
                    characterId,
                    session.Player.UserId,
                    party?.PartyId ?? 0,
                    session.SessionId,
                    run));
            FileLogger.Log(
                $"[DungeonInstanceRegistry] participant registered " +
                $"cid={characterId} party={attachment.PartyId} " +
                $"instance={attachment.RunIdentity.PartyDungeonInstanceId} " +
                $"run={attachment.RunIdentity.RunId}/" +
                $"{attachment.RunIdentity.RunGeneration} " +
                $"attachmentGeneration={attachment.AttachmentGeneration}");
        }

        private static bool TrySpendGold(InventoryLease lease, int goldCost, out int currentGold, out int updatedGold)
        {
            currentGold = 0;
            updatedGold = 0;
            if (lease == null)
                return false;

            lock (lease.SyncRoot)
            {
                currentGold = lease.Inventory.CountMainItem(0);
                updatedGold = currentGold;
                if (!lease.Inventory.TryConsumeMainItem(0, goldCost, out var consumed) || !consumed.Success)
                    return false;

                updatedGold = consumed.RemainingCount;
                return true;
            }
        }

        private static bool TryGetOwnedInventoryLease(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            return session?.Player != null
                && InventoryContext.TryGetLease(session.Player.CharacterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        // 小地图特殊图标坐标。两种来源:
        // 1. [randomized object creation] → 已有 [map] 坐标(根特系列)
        // 2. [boss room entrance condition] → [hunt monster] 条件怪需随机分配房间(陷落的村庄)
        //    照 df_game_r SetGridPath: 从非起点/非BOSS房的有效房间里随机选。
        private static IReadOnlyList<IReadOnlyList<(byte, byte)>> BuildMinimapIconGroups(int dungeonId, int mazeIndex)
        {
            PvfLib.MazeInfo maze;
            try { maze = DungeonData.GetDungeonMaze(dungeonId, mazeIndex); }
            catch { return null; }

            // 来源1: [randomized object creation]
            if (maze?.RidableScript != null && maze.RidableScript.Objects.Count > 0)
            {
                var groups = new List<IReadOnlyList<(byte, byte)>>();
                var pairs = new List<(byte, byte)>();
                foreach (var obj in maze.RidableScript.Objects)
                    pairs.Add(((byte)obj.MapX, (byte)obj.MapY));
                groups.Add(pairs);
                return groups;
            }
            return null;
        }
    }
}
