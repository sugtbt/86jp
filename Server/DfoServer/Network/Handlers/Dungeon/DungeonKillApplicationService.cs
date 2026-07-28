using DfoServer.Game.Dungeon;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal enum DungeonKillOrigin
    {
        LocalReport,
        PartyRelay,
    }

    internal sealed class KillContext
    {
        internal KillContext(
            EnhancedClientSession session,
            DungeonEventEnvelope envelope,
            ushort sequenceId,
            ushort sourceUserId,
            DungeonKillOrigin origin)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            SequenceId = sequenceId;
            SourceUserId = sourceUserId;
            Origin = origin;
        }

        internal EnhancedClientSession Session { get; }
        internal DungeonEventEnvelope Envelope { get; }
        internal ushort SequenceId { get; }
        internal ushort SourceUserId { get; }
        internal DungeonKillOrigin Origin { get; }
        internal bool IsLocalReport => Origin == DungeonKillOrigin.LocalReport;
    }

    internal sealed class DungeonKillApplicationService
    {
        private readonly DungeonSharedServices _services;
        private readonly DungeonSettlementHandler _settlement;

        internal DungeonKillApplicationService(
            DungeonSharedServices services,
            DungeonSettlementHandler settlement)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        }

        internal async Task ProcessAsync(KillContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var session = context.Session;
            var run = session.Player?.CurrentRun;
            if (!IsCurrent(run, context.Envelope))
                return;

            if (run.Phase >= DungeonRunPhase.Cleared)
            {
                await SendMonsterDieAsync(session, context, null);
                return;
            }

            bool firstApplication;
            lock (run.SyncRoot)
                firstApplication = run.RoomKilledSeqIds.Add(context.SequenceId);
            if (!firstApplication)
            {
                FileLogger.Log(
                    $"[DungeonKill] duplicate ignored: cid={session.Player.CharacterId} " +
                    $"origin={context.Origin} seq={context.SequenceId} " +
                    $"event={context.Envelope.SourceEventId:N}");
                await SendMonsterDieAsync(session, context, null);
                if (context.IsLocalReport && run.Tower == null)
                    await RelayToPartyAsync(context, run);
                return;
            }

            var identity = run.CaptureIdentity();
            var roomLocalIndex = context.SequenceId - run.RoomStartSequence;
            var monsters = run.RoomMonsters;
            DungeonData.MonsterSumInfo? monster = null;
            if (roomLocalIndex >= 0 && roomLocalIndex < monsters.Count)
                monster = monsters[roomLocalIndex];

            IReadOnlyList<DropInfo> drops = null;
            if (monster != null)
            {
                run.Instance.TryRecordMonsterKill(
                    run.CurrentRoomInstanceId,
                    run.RoomKey,
                    context.SequenceId,
                    monster.Value.Type);
                drops = await ApplyParticipantRewardAsync(
                    session,
                    run,
                    identity,
                    context.SequenceId,
                    monster.Value);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return;
            }
            else if (TryGetCurrentRoomState(run, out var outOfRangeRoomState)
                && outOfRangeRoomState.IsHellPartyRoom)
            {
                FileLogger.Log(
                    $"[DungeonKill] HELLPARTY out-of-start-map: " +
                    $"cid={session.Player.CharacterId} seq={context.SequenceId} " +
                    $"local={roomLocalIndex} tracked={outOfRangeRoomState.MonsterCount} " +
                    $"killed={run.RoomKilledSeqIds.Count}");
            }

            await SendMonsterDieAsync(session, context, drops);
            if (!IsCurrent(run, context.Envelope))
                return;

            var killedMonsterCode = monster?.Code ?? 0;
            var killedMonsterType = monster?.Type ?? (byte)0;
            if (killedMonsterCode > 0)
            {
                if (DungeonCombatHandler.IsAiCharacterActorType(killedMonsterType))
                    await _services.QuestDrops.CheckAiCharacterDrop(session, killedMonsterCode);
                else if (run.Tower == null)
                    await _services.QuestDrops.CheckMonsterDrop(session, killedMonsterCode);
                if (!IsCurrent(run, context.Envelope))
                    return;
            }

            await DungeonHuntMonsterQuestSync.SyncAsync(
                session,
                killedMonsterCode,
                context.Envelope);
            if (!IsCurrent(run, context.Envelope))
                return;

            var mechanismKill = await DungeonMechanismCoordinator.OnMonsterKilledAsync(
                session,
                context.Envelope,
                context.SequenceId,
                killedMonsterCode,
                killedMonsterType);
            if (!IsCurrent(run, context.Envelope))
                return;

            int blockingCount;
            int killedBlockingCount;
            bool roomCleared;
            lock (run.SyncRoot)
            {
                roomCleared = DungeonRoomTopology.ComputeRoomClearedLocked(
                    run,
                    out blockingCount,
                    out killedBlockingCount);
            }

            if (roomCleared)
            {
                await ApplyRoomClearedAsync(
                    session,
                    run,
                    context,
                    killedMonsterCode,
                    blockingCount,
                    killedBlockingCount);
                if (!IsCurrent(run, context.Envelope))
                    return;
            }

            if (roomCleared
                && TryGetCurrentRoomState(run, out var hellRoomState)
                && hellRoomState.IsHellPartyRoom
                && hellRoomState.HellPartyPhase == HellPartyPhase.Started)
            {
                hellRoomState.HellPartyPhase = HellPartyPhase.Complete;
                FileLogger.Log("[DungeonKill] HELLPARTY complete: tracked monsters cleared");
            }

            if (run.ClearCondition != null)
            {
                var conditionType = IsBossActorType(killedMonsterType)
                    ? 4
                    : killedMonsterType >= 5 ? 3 : 2;
                if (DungeonCombatHandler.ShouldClearDungeon(
                        run.ClearCondition.Check(conditionType, killedMonsterCode),
                        reachedBossEndpoint: false,
                        run.IgnoreDefaultDungeonClear))
                {
                    await _settlement.SubmitClearIntentAsync(
                        session,
                        new DungeonClearIntent(
                            context.Envelope,
                            $"ClearCondition type={conditionType} target={killedMonsterCode}",
                            killedMonsterCode));
                }
                if (!IsCurrent(run, context.Envelope))
                    return;
            }

            if (mechanismKill.ShouldClearDungeon)
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        context.Envelope,
                        mechanismKill.ClearReason,
                        mechanismKill.BossCode));
                if (!IsCurrent(run, context.Envelope))
                    return;
            }

            if (IsBossActorType(killedMonsterType)
                && run.Phase < DungeonRunPhase.Cleared)
            {
                WriteUnclearedBossDiagnostic(
                    session,
                    run,
                    context.SequenceId,
                    killedMonsterCode,
                    killedMonsterType,
                    roomCleared,
                    blockingCount,
                    killedBlockingCount);
            }

            if (context.IsLocalReport && run.Tower == null)
                await RelayToPartyAsync(context, run);
        }

        private async Task<IReadOnlyList<DropInfo>> ApplyParticipantRewardAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            ushort sequenceId,
            DungeonData.MonsterSumInfo monster)
        {
            var isDeathTowerRun = run.Tower != null;
            IReadOnlyList<DropInfo> towerDrops = null;
            if (isDeathTowerRun)
                _services.DeathTower.TryGenerateDropsForMonster(
                    session,
                    sequenceId,
                    out towerDrops);

            TryGetCurrentRoomState(run, out var roomState);
            var allowsExperience = run.RewardPolicy.AllowsMonsterExperience;
            var allowsDrops = run.RewardPolicy.AllowsMonsterDrops;
            var rewardMonsterType = GetRewardMonsterType(monster.Type);
            var isBoss = IsBossActorType(monster.Type);
            var isChampion = monster.Type == 1;
            var isNamed = !isBoss
                && DungeonData.IsNamedMonster(run.DungeonId, monster.Code);
            var isSuperChampion = monster.Type == 2 && !isNamed;
            var weight = DungeonData.GetExperienceWeight(run.DungeonId);
            var gainedExp = allowsExperience
                ? (uint)MonsterRewardTable.CalcExp(
                    monster.Level,
                    weight,
                    run.Difficulty,
                    rewardMonsterType,
                    isNamed)
                : 0;
            var playerRate = allowsExperience
                ? MonsterRewardTable.BaseExpPenalty(session.Player.Level, monster.Level)
                : 0;
            var scaledExp = (uint)(gainedExp * playerRate);
            var growthContractBonus = allowsExperience
                ? CalculateGrowthContractMonsterBonus(session, scaledExp)
                : 0;
            var awardedExp = CharacterExperienceService.AddSaturating(
                scaledExp,
                growthContractBonus);

            var dungeonBasisLevel = (int)monster.Level;
            var dungeonMinimumLevel = (int)monster.Level;
            if (allowsDrops)
            {
                try
                {
                    dungeonBasisLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] basic level fallback: dungeon={run.DungeonId} " +
                        $"default={dungeonBasisLevel}: {ex.Message}");
                }

                try
                {
                    dungeonMinimumLevel = DungeonData.GetDungeonMinimumRequiredLevel(
                        run.DungeonId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] minimum level fallback: dungeon={run.DungeonId} " +
                        $"default={dungeonMinimumLevel}: {ex.Message}");
                }
            }

            IReadOnlyList<DropInfo> generatedDrops;
            int goldGained;
            if (!allowsDrops)
            {
                generatedDrops = Array.Empty<DropInfo>();
                goldGained = 0;
            }
            else if (isDeathTowerRun)
            {
                generatedDrops = towerDrops ?? Array.Empty<DropInfo>();
                goldGained = 0;
            }
            else if (monster.IsHellPartyActor
                && roomState != null
                && roomState.IsHellPartyRoom)
            {
                generatedDrops = _services.Drops.GenerateAbyssPartyAndRegister(
                    run,
                    BuildAbyssPartyDropRequest(
                        roomState,
                        monster,
                        dungeonMinimumLevel,
                        dungeonBasisLevel));
                goldGained = 0;
            }
            else
            {
                var dropRateLevel = run.HellMode
                    ? dungeonBasisLevel
                    : monster.Level;
                var dropResult = _services.Drops.GenerateAndRegister(
                    run,
                    new MonsterDropRequest
                    {
                        DropRateLevel = dropRateLevel,
                        MonsterType = rewardMonsterType,
                        MonsterCode = monster.Code,
                        DungeonBasisLevel = dungeonBasisLevel,
                    });
                generatedDrops = dropResult.Drops;
                goldGained = dropResult.GoldAmount;
            }

            ExperienceGrantResult grant = null;
            if (allowsExperience)
            {
                grant = _services.CharacterExperience.Grant(
                    session.Player,
                    session.Account?.AccountId ?? 0,
                    awardedExp,
                    ExperiencePersistMode.OnLevelUpOnly,
                    "dungeon-kill");
            }

            lock (run.SyncRoot)
            {
                run.TotalExp = CharacterExperienceService.AddSaturating(
                    run.TotalExp,
                    gainedExp);
                run.MonsterGrowthContractBonusExp =
                    CharacterExperienceService.AddSaturating(
                        run.MonsterGrowthContractBonusExp,
                        growthContractBonus);
                if (isBoss)
                    run.BossTotalExp = CharacterExperienceService.AddSaturating(
                        run.BossTotalExp,
                        gainedExp);
                if (isChampion)
                    run.ChampionTotalExp = CharacterExperienceService.AddSaturating(
                        run.ChampionTotalExp,
                        gainedExp);
                if (isSuperChampion)
                    run.SuperChampionTotalExp = CharacterExperienceService.AddSaturating(
                        run.SuperChampionTotalExp,
                        gainedExp);
                if (isNamed)
                    run.NamedMonsterTotalExp = CharacterExperienceService.AddSaturating(
                        run.NamedMonsterTotalExp,
                        gainedExp);
                run.TotalGold = checked(run.TotalGold + goldGained);
            }

            if (grant != null)
            {
                await _services.ProgressNotifications.SendExpGrantNotificationAsync(
                    session,
                    grant,
                    "DUNGEON_KILL",
                    growthContractBonus);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return generatedDrops;
                if (grant.LeveledUp)
                    await _services.ProgressNotifications.SendInDungeonLevelUpFollowups(session);
            }

            return generatedDrops;
        }

        private async Task ApplyRoomClearedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            KillContext context,
            int killedMonsterCode,
            int blockingCount,
            int killedBlockingCount)
        {
            TryGetCurrentRoomState(run, out var roomState);
            DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    context.Envelope,
                    DungeonEncounterDirectiveKind.Succeed,
                    cause: "tracked room actors cleared"));
            roomState?.TryClear();

            var endPoint = roomState != null
                && run.BossMapPos != null
                && run.BossMapPos.Length >= 2
                && roomState.Maze.X == run.BossMapPos[0]
                && roomState.Maze.Y == run.BossMapPos[1];
            var currentMapId = roomState?.Maze.Index ?? 0;
            var explicitMapClear = run.ClearCondition != null
                && run.ClearCondition.Check(1, currentMapId);

            await PetCreatureRuntimeService.GrantRoomClearExperienceOnceAsync(
                session,
                roomState,
                1);
            if (!IsCurrent(run, context.Envelope))
                return;

            if (DungeonCombatHandler.ShouldClearDungeon(
                    explicitMapClear,
                    endPoint,
                    run.IgnoreDefaultDungeonClear))
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        context.Envelope,
                        $"prepare_dungeon_clear ccType1={explicitMapClear} endPoint={endPoint}",
                        killedMonsterCode));
            }
            if (!IsCurrent(run, context.Envelope))
                return;

            FileLogger.Log(
                $"[DungeonKill] room cleared: cid={session.Player.CharacterId} " +
                $"origin={context.Origin} dungeon={run.DungeonId} " +
                $"room=({run.RoomKey.X},{run.RoomKey.Y}) map={currentMapId} " +
                $"blocking={killedBlockingCount}/{blockingCount} " +
                $"killedTotal={run.RoomKilledSeqIds.Count}");

            if (currentMapId <= 0)
                return;

            if (ShouldDeferQuestConnectedStartMapSync(run, currentMapId)
                && session.GameSession?.QuestManager != null
                && session.GameSession.QuestManager
                    .HasDeferredQuestConnectedStartMapClearQuest(
                        session.Player.CharacterId,
                        currentMapId))
            {
                FileLogger.Log(
                    $"[DungeonKill] CLEAR_MAP deferred: dungeon={run.DungeonId} " +
                    $"maze={run.MazeIndex} map={currentMapId}");
                return;
            }

            await DungeonClearMapQuestSync.SyncAsync(
                session,
                0,
                currentMapId,
                "room_clear",
                context.Envelope);
        }

        private async Task RelayToPartyAsync(KillContext source, DungeonRun sourceRun)
        {
            var partyManager = _services.PartyManager;
            var sessions = _services.Sessions;
            if (partyManager == null
                || sessions == null
                || source.Session.Player == null
                || !IsCurrent(sourceRun, source.Envelope))
            {
                return;
            }

            var sourceCharacterId = (ushort)source.Session.Player.CharacterId;
            var party = partyManager.GetPartyByUser(sourceCharacterId);
            if (party == null || party.Count <= 1)
                return;

            foreach (var member in party.MembersBySlot())
            {
                if (member.UserId == sourceCharacterId)
                    continue;
                sessions.TryGet(member.CharacterId, out var memberSession);
                var memberRun = memberSession?.Player?.CurrentRun;
                if (memberRun == null
                    || memberSession.TcpClient == null
                    || !memberSession.TcpClient.Connected
                    || !sourceRun.SharesCurrentRoomWith(memberRun))
                {
                    continue;
                }

                var memberEvent = source.Envelope.ForAffectedPlayer(
                    memberRun.CaptureIdentity(),
                    memberRun.CurrentRoomInstanceId,
                    memberSession.Player.CharacterId);
                try
                {
                    await ProcessAsync(new KillContext(
                        memberSession,
                        memberEvent,
                        source.SequenceId,
                        source.SourceUserId,
                        DungeonKillOrigin.PartyRelay));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] party relay failed: " +
                        $"source={sourceCharacterId} member={member.UserId} " +
                        $"seq={source.SequenceId} event={source.Envelope.SourceEventId:N} " +
                        $"error={ex.Message}");
                }
            }
        }

        private static Task SendMonsterDieAsync(
            EnhancedClientSession session,
            KillContext context,
            IReadOnlyList<DropInfo> drops)
        {
            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0026,
                    DungeonNotificationBuilder.BuildMonsterDie(
                        context.SequenceId,
                        drops,
                        context.SourceUserId)));
        }

        private static bool IsCurrent(
            DungeonRun run,
            DungeonEventEnvelope envelope)
        {
            if (run == null
                || envelope == null
                || !run.Matches(envelope.RunIdentity))
            {
                return false;
            }

            return !envelope.RoomInstanceId.HasValue
                || run.CurrentRoomInstanceId == envelope.RoomInstanceId.Value;
        }

        private static bool TryGetCurrentRoomState(
            DungeonRun run,
            out RoomState roomState)
        {
            if (run == null)
            {
                roomState = null;
                return false;
            }

            return run.RoomStates.TryGetValue(run.RoomKey, out roomState);
        }

        private static bool ShouldDeferQuestConnectedStartMapSync(
            DungeonRun run,
            int currentMapId)
        {
            return run != null
                && run.MazeQuestConnected
                && run.MazeStartMapId > 0
                && run.MazeStartMapId == currentMapId
                && run.RoomKey.X == run.MazeStartX
                && run.RoomKey.Y == run.MazeStartY;
        }

        private static bool IsBossActorType(byte monsterType) =>
            monsterType == 3 || monsterType == 8;

        private static int GetRewardMonsterType(byte monsterType) =>
            monsterType == 8 ? 3 : monsterType;

        private static AbyssPartyDropRequest BuildAbyssPartyDropRequest(
            RoomState roomState,
            DungeonData.MonsterSumInfo monster,
            int dungeonMinimumLevel,
            int dungeonBasisLevel)
        {
            var isLastGroupMonster = false;
            if (roomState.HellPartyGroupRemaining != null
                && monster.HellPartyGroupId > 0
                && roomState.HellPartyGroupRemaining.TryGetValue(
                    monster.HellPartyGroupId,
                    out var remaining))
            {
                var after = Math.Max(0, remaining - 1);
                if (after == 0)
                {
                    roomState.HellPartyGroupRemaining.Remove(monster.HellPartyGroupId);
                    isLastGroupMonster = true;
                }
                else
                {
                    roomState.HellPartyGroupRemaining[monster.HellPartyGroupId] = after;
                }
            }

            return new AbyssPartyDropRequest
            {
                MonsterCode = monster.Code,
                DungeonMinimumLevel = dungeonMinimumLevel,
                DungeonBasisLevel = dungeonBasisLevel,
                AbyssPartyDifficulty = monster.HellPartyDifficulty,
                RewardRollCount = monster.HellRewardRollCount,
                IsLastGroupMonster = isLastGroupMonster,
                IsAbyssMonsterScript = monster.IsHellMonsterScript,
            };
        }

        private static uint CalculateGrowthContractMonsterBonus(
            EnhancedClientSession session,
            uint baseMonsterExp)
        {
            if (baseMonsterExp == 0)
                return 0;

            var accountId = session.Account?.AccountId ?? 0;
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            return Game.Premium.PremiumEffectProvider
                .GetCombinedEffects(connectionString, accountId)
                .ComputeBonusExp(baseMonsterExp);
        }

        private static void WriteUnclearedBossDiagnostic(
            EnhancedClientSession session,
            DungeonRun run,
            ushort sequenceId,
            int monsterCode,
            byte monsterType,
            bool roomCleared,
            int blockingCount,
            int killedBlockingCount)
        {
            TryGetCurrentRoomState(run, out var room);
            var roomX = room?.Maze.X ?? -999;
            var roomY = room?.Maze.Y ?? -999;
            var bossX = run.BossMapPos != null && run.BossMapPos.Length >= 2
                ? run.BossMapPos[0]
                : -1;
            var bossY = run.BossMapPos != null && run.BossMapPos.Length >= 2
                ? run.BossMapPos[1]
                : -1;
            FileLogger.Log(
                $"[DungeonKill] boss not cleared: cid={session.Player.CharacterId} " +
                $"seq={sequenceId} code={monsterCode} type={monsterType} " +
                $"roomCleared={roomCleared} blocking={killedBlockingCount}/{blockingCount} " +
                $"ccNull={run.ClearCondition == null} " +
                $"ccCleared={run.ClearCondition?.IsCleared} " +
                $"room=({roomX},{roomY}) boss=({bossX},{bossY}) " +
                $"phase={run.Phase}");
        }
    }
}
