using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DeathTowerEntrySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_ENTRY selftest ===");
            var failures = 0;

            var config = new DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 3,
                StageMapIds = new[] { 33060, 33061, 33062 },
                BasisLevel = 50,
                MaxClearItemCount = 10,
            };

            var tower = new DeathTowerSession(config);
            Check("tower session starts on the first configured floor",
                tower.CurrentStage == 0
                && tower.State == 0
                && tower.GetCurrentMapId() == 33060,
                ref failures);

            var liveEntryCreated = new DeathTowerHandler()
                .TryCreateSession(11000, out var liveEntryTower);
            var liveConfig = DeathTowerData.GetConfig(11000);
            Check("handler-created death tower session starts on the first configured floor",
                liveEntryCreated
                && liveConfig != null
                && liveEntryTower.CurrentStage == 0
                && liveEntryTower.GetCurrentMapId() == liveConfig.StageMapIds[0],
                ref failures);

            VerifySyncCombatStageAtomicity(config, ref failures);

            var towerInfo = DeathTowerPacketBuilder.BuildTowerInfo(11000, 3);
            Check("0x008E body remains 8 bytes",
                towerInfo.Length == 8,
                ref failures);
            Check("0x008E encodes dungeon and stage count",
                BitConverter.ToUInt32(towerInfo, 0) == 11000
                && BitConverter.ToUInt16(towerInfo, 4) == 3,
                ref failures);
            Check("0x008E normal tower mode tail is 00 0B",
                towerInfo[6] == 0
                && towerInfo[7] == DeathTowerPacketBuilder.ObservedRandomBuffType
                && towerInfo[7] == 11,
                ref failures);

            var rewardConfig = DeathTowerRewardConfig.Load();
            Check("death tower PVF reward config exposes floor-45 weights and item cap inputs",
                rewardConfig != null
                && Math.Abs(rewardConfig.GetExpWeight(45) - 8.413f) < 0.0001f
                && rewardConfig.GetRewardCardCount(45) == 11
                && Math.Abs(rewardConfig.GoldWeight - 11f) < 0.0001f
                && rewardConfig.NormalItemWeight == 50
                && rewardConfig.MagicItemWeight == 49
                && rewardConfig.ItemWeightTotal == 100,
                ref failures);
            var unavailableRewardConfig = DeathTowerRewardConfig.Parse(string.Empty);
            Check("missing death tower reward PVF fails closed",
                unavailableRewardConfig.GoldWeight == 0
                && unavailableRewardConfig.GetExpWeight(45) == 0
                && unavailableRewardConfig.GetRewardCardCount(45) == 0
                && unavailableRewardConfig.ItemWeightTotal == 0,
                ref failures);

            var rewardBody = DeathTowerPacketBuilder.BuildReward(
                0,
                new[]
                {
                    (IReadOnlyList<DeathTowerRewardItem>)new[]
                    {
                        new DeathTowerRewardItem(10089420, 2),
                        new DeathTowerRewardItem(6515, 1),
                    },
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                });
            Check("0x0091 non-empty reward body is u32 plus four count/item groups",
                rewardBody.Length == 24
                && BitConverter.ToUInt32(rewardBody, 0) == 0
                && rewardBody[4] == 2
                && BitConverter.ToUInt32(rewardBody, 5) == 10089420
                && BitConverter.ToUInt32(rewardBody, 9) == 2
                && BitConverter.ToUInt32(rewardBody, 13) == 6515
                && BitConverter.ToUInt32(rewardBody, 17) == 1
                && rewardBody[21] == 0
                && rewardBody[22] == 0
                && rewardBody[23] == 0,
                ref failures);
            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDungeonHandler();
                handler
                    .Handle_ENUM_CMDPACKET_SELECT_DUNGEON(
                        fixture.Session,
                        new GamePacketHeader(),
                        BuildSelectDungeonBody(11000, difficulty: 2))
                    .GetAwaiter()
                    .GetResult();

                var sentTypes = fixture.ReadSentTypes(expectedPackets: 3);
                Check("select-dungeon tower creates CurrentRun payload",
                    fixture.Session.Player.CurrentRun != null
                    && fixture.Session.Player.CurrentRun.DungeonId == 11000
                    && fixture.Session.Player.CurrentRun.Tower != null,
                    ref failures);
                Check("tower stage monsters are available to the combat/experience pipeline",
                    fixture.Session.Player.CurrentRun.RoomMonsters.Count > 0
                    && fixture.Session.Player.CurrentRun.RoomStartSequence > 0,
                    ref failures);
                Check("select-dungeon tower packet order starts with 0x008E then tower packets",
                    sentTypes.Count >= 3
                    && sentTypes[0] == 0x008E
                    && sentTypes[1] == 0x008F
                    && sentTypes[2] == 0x001E,
                    ref failures);
                Check("tower guaranteed drop completes DIE_MONSTER with one authoritative stage LCG",
                    TowerGuaranteedDropCompletesCombatHandler(fixture, handler),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDungeonHandler();
                var mapHandler = typeof(DungeonHandler).GetField(
                        "_map",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(handler) as DungeonMapHandler;
                fixture.Session.Player.Name = new byte[]
                {
                    (byte)'f', (byte)'l', (byte)'o', (byte)'o', (byte)'r', (byte)'1', (byte)'0',
                };
                var expectedTowerAppearance = new[]
                {
                    510000, 510001, 510002, 510003, 510004,
                    510005, 510006, 510007, 510008, 510009,
                    511011,
                };
                var appearanceEntries = new Game.Characters.CharacterAppearanceEntry[12];
                for (byte slot = 0; slot < 10; slot++)
                {
                    appearanceEntries[slot] = new Game.Characters.CharacterAppearanceEntry(
                        slot,
                        expectedTowerAppearance[slot],
                        4,
                        Array.Empty<byte>(),
                        0,
                        0,
                        0,
                        0);
                }
                appearanceEntries[10] = new Game.Characters.CharacterAppearanceEntry(
                    10,
                    599999,
                    4,
                    Array.Empty<byte>(),
                    0,
                    0,
                    0,
                    0);
                appearanceEntries[11] = new Game.Characters.CharacterAppearanceEntry(
                    11,
                    expectedTowerAppearance[10],
                    4,
                    Array.Empty<byte>(),
                    0,
                    0,
                    0,
                    0);
                var expectedCreatureName = new byte[]
                {
                    (byte)'m', (byte)'i', (byte)'r', (byte)'r', (byte)'o', (byte)'r',
                };
                const uint expectedCreatureItemId = 512345;
                fixture.Session.Player.AppearanceEntries = appearanceEntries;
                fixture.Session.Player.Subtype0Tail =
                    new Game.SelectCharacter.UserInfoMinimumTailSnapshot
                    {
                        EquippedCreatureNameBytes = expectedCreatureName,
                        EquippedCreatureItemId = expectedCreatureItemId,
                    };
                fixture.Session.Player.CurrentRun =
                    new Game.Dungeon.DungeonRun(11017, 0) { MazeIndex = 0 };

                mapHandler?.SendStartMapAsync(
                        fixture.Session,
                        0xFF,
                        0xFF,
                        overrideMapId: -1)
                    .GetAwaiter()
                    .GetResult();

                var sentPackets = mapHandler == null
                    ? new List<SelectDungeonFixture.CapturedPacket>()
                    : fixture.ReadSentPackets(expectedPackets: 1);
                while (sentPackets.Count < 3 && fixture.HasPendingPacket())
                    sentPackets.AddRange(fixture.ReadSentPackets(expectedPackets: 1));
                Check("despair floor 10 sends START_MAP then base and current dynamic APC information",
                    sentPackets.Count == 3
                    && sentPackets[0].Type == 0x001D
                    && sentPackets[1].Type == (ushort)Network.NotiPacketType.USER_APC_INFO_TOD
                    && sentPackets[2].Type == (ushort)Network.NotiPacketType.USER_APC_INFO_TOD
                    && HasRoomMonster(fixture.Session, 31505)
                    && IsTowerApcInfoBody(
                        sentPackets[1].Body,
                        fixture.Session,
                        0,
                        expectedTowerAppearance,
                        expectedCreatureName,
                        expectedCreatureItemId)
                    && IsTowerApcInfoBody(
                        sentPackets[2].Body,
                        fixture.Session,
                        10,
                        expectedTowerAppearance,
                        expectedCreatureName,
                        expectedCreatureItemId),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var rollbackTower = CreateFinalFloorTower(config);
                var maxLevelEntryExp = (uint)Math.Max(
                    0,
                    Game.Dungeon.ExpTableProvider.GetLevelThreshold(
                        Game.Dungeon.ExpTableProvider.MaxLevel - 1));
                fixture.SetCharacterProgress(
                    (byte)(Game.Dungeon.ExpTableProvider.MaxLevel - 1),
                    maxLevelEntryExp - 1);
                var previousExp = fixture.Session.Player.Exp;
                var previousLevel = fixture.Session.Player.Level;
                var previousGold = fixture.LoadGold();
                var previousItems = fixture.CountPersistentMainItems();
                var previousAccountProgress = fixture.LoadAccountProgress();
                var failed = false;
                try
                {
                    new DeathTowerSettlementService(
                            fixture.AssetService,
                            fixture.CreateAccountExperienceService(),
                            (scope, characterId, accountId, level, exp, gain) =>
                                CharacterExperienceService.Plan(
                                    level,
                                    exp,
                                    gain,
                                    normalizeMaxLevelExp: gain > 0))
                        .Grant(fixture.Session, rollbackTower);
                }
                catch (InvalidOperationException)
                {
                    failed = true;
                }

                Check("tower settlement rolls back gold, items and memory exp when progress write fails",
                    failed
                    && fixture.Session.Player.Exp == previousExp
                    && fixture.Session.Player.Level == previousLevel
                    && fixture.LoadGold() == previousGold
                    && fixture.CountPersistentMainItems() == previousItems,
                    ref failures);
                var rolledBackAccountProgress = fixture.LoadAccountProgress();
                Check("tower settlement rollback also restores character and account exp progress",
                    rolledBackAccountProgress.HonorExp == previousAccountProgress.HonorExp
                    && rolledBackAccountProgress.GrowthCapsuleExp == previousAccountProgress.GrowthCapsuleExp
                    && fixture.PersistedProgressMatches(previousLevel, previousExp),
                    ref failures);
                Check("failed settlement gate can be explicitly reopened",
                    rollbackTower.TryBeginSettlement()
                    && (AbortAndRetrySettlement(rollbackTower)),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDungeonHandler();
                var settlementTower = CreateFinalFloorTower(config);

                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();
                var previousExp = fixture.Session.Player.Exp;
                var previousGold = fixture.LoadGold();
                handler
                    .Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                var sentPackets = fixture.ReadSentPackets(expectedPackets: 6);
                Check("tower settlement sends ranking, non-empty reward and EPLP packets first",
                    sentPackets.Count == 6
                    && sentPackets[0].Type == 0x0090
                    && sentPackets[1].Type == 0x0091
                    && sentPackets[1].Body.Length >= 16
                    && sentPackets[1].Body[4] > 0
                    && sentPackets[2].Type == 0x0092,
                    ref failures);
                Check("tower settlement reuses the authoritative 0x0025 experience layout",
                    sentPackets.Count == 6
                    && sentPackets[3].Type == 0x0025
                    && sentPackets[3].Body.Length == ExpNotificationBuilder.BodyLength
                    && sentPackets[3].Body[0] == fixture.Session.Player.Level
                    && BitConverter.ToUInt32(sentPackets[3].Body, 1) == fixture.Session.Player.Exp
                    && sentPackets[4].Type == 0x000E
                    && sentPackets[5].Type == 0x000E,
                    ref failures);
                Check("tower settlement item 0x000E matches committed temporary-database slots",
                    fixture.ItemUpdateMatchesDatabase(sentPackets[5].Body),
                    ref failures);
                Check("tower settlement persists PVF-scaled exp, gold and reward items",
                    fixture.Session.Player.Exp > previousExp
                    && fixture.LoadGold() > previousGold
                    && fixture.CountPersistentMainItems() > 0,
                    ref failures);

                var settledExp = fixture.Session.Player.Exp;
                var settledGold = fixture.LoadGold();
                var settledItems = fixture.CountPersistentMainItems();
                var settledAccountProgress = fixture.LoadAccountProgress();
                handler
                    .Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("duplicate final-floor stage command cannot grant settlement twice",
                    fixture.Session.Player.Exp == settledExp
                    && fixture.LoadGold() == settledGold
                    && fixture.CountPersistentMainItems() == settledItems
                    && fixture.LoadAccountProgress().Equals(settledAccountProgress),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var maxLevelEntryExp = (uint)Math.Max(
                    0,
                    Game.Dungeon.ExpTableProvider.GetLevelThreshold(
                        Game.Dungeon.ExpTableProvider.MaxLevel - 1));
                fixture.SetCharacterProgress(
                    (byte)(Game.Dungeon.ExpTableProvider.MaxLevel - 1),
                    maxLevelEntryExp - 1);
                fixture.Session.Player.Subtype0Tail = new Game.SelectCharacter.UserInfoMinimumTailSnapshot
                {
                    ProgressA = 250,
                    ProgressB = 0xDEADBEEF,
                };
                var failingSummaryAccountExperience = fixture.CreateAccountExperienceService(
                    new ThrowingListCharacterRepository(fixture.CreateCharacterRepository()));
                var handler = fixture.CreateDungeonHandler(failingSummaryAccountExperience);
                var settlementTower = CreateFinalFloorTower(config);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();

                handler
                    .Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                var sentPackets = fixture.ReadSentPackets(expectedPackets: 7);
                var accountProgress = fixture.LoadAccountProgress();
                var honor = HonorLevelDataProvider.CalculateFromHonorExp(
                    accountProgress.HonorExp,
                    fullLevelCount: 1);
                var growth = GrowthCapsuleDataProvider.Calculate(
                    accountProgress.GrowthCapsuleExp);
                var committedGold = fixture.LoadGold();
                var committedItems = fixture.CountPersistentMainItems();
                Check("85-to-86 tower settlement sends exp then level-up followups before wallet and items",
                    fixture.Session.Player.Level == Game.Dungeon.ExpTableProvider.MaxLevel
                    && fixture.Session.Player.Exp == maxLevelEntryExp
                    && sentPackets.Count == 7
                    && sentPackets[0].Type == 0x0090
                    && sentPackets[1].Type == 0x0091
                    && sentPackets[2].Type == 0x0092
                    && sentPackets[3].Type == 0x0025
                    && sentPackets[4].Type == 0x0015
                    && sentPackets[5].Type == 0x0002
                    && sentPackets[6].Type == 0x000E,
                    ref failures);
                Check("tower 0x0025 reloads committed honor and growth after summary construction fails",
                    accountProgress.HonorExp > 0
                    && accountProgress.GrowthCapsuleExp > 0
                    && BitConverter.ToUInt32(
                        sentPackets[3].Body,
                        ExpNotificationBuilder.HonorLevelOffset) == honor.HonorLevel
                    && BitConverter.ToUInt32(
                        sentPackets[3].Body,
                        ExpNotificationBuilder.HonorExpOffset) == honor.HonorExp
                    && BitConverter.ToUInt32(
                        sentPackets[3].Body,
                        ExpNotificationBuilder.GrowthCapsuleExpOffset)
                        == GrowthCapsuleDataProvider.GetDisplayProgress(
                            fixture.Session.Player.Level,
                            growth),
                    ref failures);
                Check("85-to-86 settlement persists normalized character progress",
                    fixture.PersistedProgressMatches(
                        fixture.Session.Player.Level,
                        fixture.Session.Player.Exp),
                    ref failures);
                handler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("summary-fallback settlement assets remain single-grant",
                    fixture.LoadGold() == committedGold
                    && fixture.CountPersistentMainItems() == committedItems
                    && fixture.LoadAccountProgress().Equals(accountProgress)
                    && !fixture.HasPendingPacket(),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                const byte previousLevel = 50;
                var nextLevelThreshold = (uint)Math.Max(
                    1,
                    Game.Dungeon.ExpTableProvider.GetLevelThreshold(previousLevel));
                fixture.SetCharacterProgress(previousLevel, nextLevelThreshold - 1);
                var handler = fixture.CreateDungeonHandler();
                var settlementTower = CreateFinalFloorTower(config);
                PrepareSettlementItemReward(settlementTower, fixture.Session.Player.Level);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();

                handler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                var sentPackets = fixture.ReadSentPackets(expectedPackets: 8);
                Check("level-up settlement sends complete sequence through final item refresh",
                    fixture.Session.Player.Level > previousLevel
                    && sentPackets.Count == 8
                    && sentPackets[0].Type == 0x0090
                    && sentPackets[1].Type == 0x0091
                    && sentPackets[2].Type == 0x0092
                    && sentPackets[3].Type == 0x0025
                    && sentPackets[4].Type == 0x0015
                    && sentPackets[5].Type == 0x0002
                    && sentPackets[6].Type == 0x000E
                    && sentPackets[7].Type == 0x000E
                    && fixture.ItemUpdateMatchesDatabase(sentPackets[7].Body),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var persistAttempts = 0;
                var handler = fixture.CreateDeathTowerHandler(
                    (session, settlement) => session.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            0x0025,
                            new byte[ExpNotificationBuilder.BodyLength])),
                    (scope, characterId, accountId, level, exp, gain) =>
                    {
                        persistAttempts++;
                        if (persistAttempts == 1)
                        {
                            return CharacterExperienceService.Plan(
                                level,
                                exp,
                                gain,
                                normalizeMaxLevelExp: gain > 0);
                        }

                        return CharacterExperienceService.GrantInTransaction(
                                scope.Connection,
                                scope.Transaction,
                                characterId,
                                accountId,
                                level,
                                exp,
                                gain,
                                normalizeMaxLevelExp: gain > 0);
                    });
                var settlementTower = CreateFinalFloorTower(config);
                PrepareSettlementItemReward(settlementTower, fixture.Session.Player.Level);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();
                var previousLevel = fixture.Session.Player.Level;
                var previousExp = fixture.Session.Player.Exp;
                var previousGold = fixture.LoadGold();
                var previousItems = fixture.CountPersistentMainItems();

                handler.HandleStageCommand(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("handler automatically reopens settlement after pre-commit persistence failure",
                    persistAttempts == 1
                    && fixture.Session.Player.Level == previousLevel
                    && fixture.Session.Player.Exp == previousExp
                    && fixture.LoadGold() == previousGold
                    && fixture.CountPersistentMainItems() == previousItems
                    && !fixture.HasPendingPacket(),
                    ref failures);

                handler.HandleStageCommand(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                var settledGold = fixture.LoadGold();
                var settledItems = fixture.CountPersistentMainItems();
                var successPacketCount = settledItems > previousItems ? 6 : 5;
                var successPackets = fixture.ReadSentPackets(successPacketCount);
                handler.HandleStageCommand(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("handler retry commits once and the third final-floor command is idempotent",
                    persistAttempts == 2
                    && successPackets.Count == successPacketCount
                    && successPackets[0].Type == 0x0090
                    && successPackets[1].Type == 0x0091
                    && successPackets[2].Type == 0x0092
                    && successPackets[3].Type == 0x0025
                    && successPackets[4].Type == 0x000E
                    && fixture.LoadGold() == settledGold
                    && fixture.CountPersistentMainItems() == settledItems
                    && !fixture.HasPendingPacket(),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDeathTowerHandler(
                    (session, settlement) =>
                        throw new InvalidOperationException("Injected post-commit notification failure."));
                var settlementTower = CreateFinalFloorTower(config);
                PrepareSettlementItemReward(settlementTower, fixture.Session.Player.Level);
                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();
                var previousGold = fixture.LoadGold();
                var previousItems = fixture.CountPersistentMainItems();

                handler.HandleStageCommand(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                var committedGold = fixture.LoadGold();
                var committedItems = fixture.CountPersistentMainItems();
                var preFailurePackets = fixture.ReadSentPackets(expectedPackets: 3);
                handler.HandleStageCommand(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("post-commit notification failure keeps settlement gate closed",
                    committedGold > previousGold
                    && committedItems > previousItems
                    && preFailurePackets[0].Type == 0x0090
                    && preFailurePackets[1].Type == 0x0091
                    && preFailurePackets[2].Type == 0x0092
                    && fixture.LoadGold() == committedGold
                    && fixture.CountPersistentMainItems() == committedItems
                    && !fixture.HasPendingPacket(),
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static DeathTowerSession CreateFinalFloorTower(DeathTowerData.TowerConfig config)
        {
            var tower = new DeathTowerSession(config);
            while (tower.CurrentStage < tower.EndStage)
            {
                tower.SetFighting();
                if (!tower.TryAdvanceStage())
                    throw new InvalidOperationException("Unable to advance settlement test to final floor.");
            }
            return tower;
        }

        private static void PrepareSettlementItemReward(DeathTowerSession tower, byte level)
        {
            var rewardConfig = DeathTowerRewardConfig.Load();
            for (uint seed = 1; seed < 100000; seed++)
            {
                var lcg = new Game.Dungeon.DnfLcg(seed);
                var rarity = rewardConfig.RollItemRarity(lcg);
                var itemId = Game.Dungeon.MonsterDropConfig.ChooseEquipment(lcg, level, rarity);
                if (itemId <= 0)
                    itemId = Game.Dungeon.MonsterDropConfig.ChooseStackable(lcg, level, rarity);
                if (itemId <= 0)
                    continue;

                tower.BeginStage(seed, Array.Empty<StageTowerItem>());
                return;
            }

            throw new InvalidOperationException($"Unable to find deterministic settlement item seed for level {level}.");
        }

        private static bool AbortAndRetrySettlement(DeathTowerSession tower)
        {
            tower.AbortSettlement();
            return tower.TryBeginSettlement();
        }

        private static byte[] BuildSelectDungeonBody(ushort dungeonId, byte difficulty)
        {
            var body = new byte[5];
            BitConverter.GetBytes(dungeonId).CopyTo(body, 0);
            body[2] = difficulty;
            return body;
        }

        private static bool TowerGuaranteedDropCompletesCombatHandler(
            SelectDungeonFixture fixture,
            DungeonHandler handler)
        {
            var run = fixture.Session.Player.CurrentRun;
            if (run?.Tower == null || run.RoomMonsters.Count == 0 || run.RoomStartSequence == 0)
                return false;

            var monster = run.RoomMonsters[0];
            var monsterUniqueId = run.RoomStartSequence;
            monster.Code = 10504;
            monster.Type = 5;
            run.Tower.BeginStage(0x12345678, new[]
            {
                new StageTowerItem
                {
                    SourceListIndex = monster.TemplateOrder,
                    SourceMonsterUniqueId = monsterUniqueId,
                    ItemUniqueId = 51,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });

            var syncCombatStage = typeof(DeathTowerHandler).GetMethod(
                "SyncCombatStage",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (syncCombatStage == null)
                return false;

            syncCombatStage.Invoke(null, new object[]
            {
                fixture.Session,
                run.Tower,
                new List<StageMonster>
                {
                    new StageMonster
                    {
                        ListIndex = monster.TemplateOrder,
                        MonsterUniqueId = monsterUniqueId,
                        MonsterIndex = monster.Code,
                        MonsterLevel = monster.Level,
                        MonsterType = monster.Type,
                        IsBoxMonster = monster.IsBlocking ? (byte)0 : (byte)1,
                    },
                },
            });

            try
            {
                var body = new byte[4];
                BitConverter.GetBytes(monsterUniqueId).CopyTo(body, 0);
                BitConverter.GetBytes((ushort)fixture.Session.Player.UserId).CopyTo(body, 2);
                handler.Handle_ENUM_CMDPACKET_DIE_MONSTER(
                        fixture.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] tower DIE_MONSTER threw: {ex.GetBaseException().Message}");
                return false;
            }

            var stageLcg = typeof(DeathTowerSession).GetProperty(
                "StageLcg",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(run.Tower);
            return stageLcg != null
                && ReferenceEquals(run.RoomLcg, stageLcg)
                && run.Tower.GroundItems.Count == 1
                && fixture.CountPersistentItem(10089420) == 1;
        }

        private static void VerifySyncCombatStageAtomicity(
            DeathTowerData.TowerConfig config,
            ref int failures)
        {
            var syncCombatStage = typeof(DeathTowerHandler).GetMethod(
                "SyncCombatStage",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (syncCombatStage == null)
            {
                Check("SyncCombatStage reflection entry exists", false, ref failures);
                return;
            }

            using (var tcp = new TcpClient())
            {
                var session = new EnhancedClientSession(tcp, new GamePacketHeader());
                var staleTower = new DeathTowerSession(config);
                staleTower.BeginStage(0x11111111, Array.Empty<StageTowerItem>());
                var oldTower = new DeathTowerSession(config);
                oldTower.BeginStage(0x22222222, Array.Empty<StageTowerItem>());
                var originalMonsters = new List<GameWorld.Dungeon.MonsterSumInfo>
                {
                    new GameWorld.Dungeon.MonsterSumInfo { Code = 77, Level = 50, Type = 1 },
                };
                var originalKilled = new HashSet<ushort> { 41 };
                var originalDrops = new Dictionary<ushort, Game.Dungeon.DropInfo>
                {
                    [9] = new Game.Dungeon.DropInfo { SceneSlot = 9, TemplateId = 6515, StackCount = 1 },
                };
                var originalLcg = new Game.Dungeon.DnfLcg(0x33333333);
                var run = new Game.Dungeon.DungeonRun(11000, 0)
                {
                    Tower = oldTower,
                    RoomKilledSeqIds = originalKilled,
                    Drops = originalDrops,
                    RoomMonsters = originalMonsters,
                    RoomStartSequence = 41,
                    Seed = 0x33333333,
                    RoomLcg = originalLcg,
                };
                session.Player.CurrentRun = run;

                var staleMonsters = new CoordinatedStageMonsterList(new[]
                {
                    new StageMonster
                    {
                        ListIndex = 2,
                        MonsterUniqueId = 55,
                        MonsterIndex = 10504,
                        MonsterLevel = 50,
                        MonsterType = 5,
                    },
                });
                syncCombatStage.Invoke(null, new object[]
                {
                    session,
                    staleTower,
                    staleMonsters,
                });

                Check("stale tower combat sync rejects before building combat DTOs",
                    !staleMonsters.WasEnumerated
                    && ReferenceEquals(run.RoomKilledSeqIds, originalKilled)
                    && originalKilled.SetEquals(new[] { (ushort)41 })
                    && ReferenceEquals(run.Drops, originalDrops)
                    && originalDrops.Count == 1
                    && ReferenceEquals(run.RoomMonsters, originalMonsters)
                    && run.RoomStartSequence == 41
                    && run.Seed == 0x33333333
                    && ReferenceEquals(run.RoomLcg, originalLcg),
                    ref failures);

                var invoked = new ManualResetEventSlim(false);
                var dtoBuilt = new ManualResetEventSlim(false);
                oldTower.BeginStage(0x44444444, Array.Empty<StageTowerItem>());
                var newTower = new DeathTowerSession(config);
                newTower.BeginStage(0x55555555, Array.Empty<StageTowerItem>());
                var racingMonsters = new CoordinatedStageMonsterList(
                    new[]
                    {
                        new StageMonster
                        {
                            ListIndex = 3,
                            MonsterUniqueId = 60,
                            MonsterIndex = 10505,
                            MonsterLevel = 51,
                            MonsterType = 6,
                        },
                    },
                    dtoBuilt);
                Task syncTask;
                lock (run.SyncRoot)
                {
                    syncTask = Task.Run(() =>
                    {
                        invoked.Set();
                        syncCombatStage.Invoke(null, new object[]
                        {
                            session,
                            oldTower,
                            racingMonsters,
                        });
                    });
                    invoked.Wait();
                    dtoBuilt.Wait();
                    run.Tower = newTower;
                }

                syncTask.GetAwaiter().GetResult();
                Check("tower replacement during SyncCombatStage wait leaves every combat field unchanged",
                    ReferenceEquals(run.Tower, newTower)
                    && ReferenceEquals(run.RoomKilledSeqIds, originalKilled)
                    && originalKilled.SetEquals(new[] { (ushort)41 })
                    && ReferenceEquals(run.Drops, originalDrops)
                    && originalDrops.Count == 1
                    && ReferenceEquals(run.RoomMonsters, originalMonsters)
                    && run.RoomStartSequence == 41
                    && run.Seed == 0x33333333
                    && ReferenceEquals(run.RoomLcg, originalLcg),
                    ref failures);

                var publishMonsters = new List<StageMonster>
                {
                    new StageMonster
                    {
                        ListIndex = 4,
                        MonsterUniqueId = 61,
                        MonsterIndex = 10506,
                        MonsterLevel = 52,
                        MonsterType = 7,
                    },
                };
                syncCombatStage.Invoke(null, new object[]
                {
                    session,
                    newTower,
                    publishMonsters,
                });
                var stageLcg = typeof(DeathTowerSession).GetProperty(
                    "StageLcg",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(newTower);
                Check("SyncCombatStage publishes monsters, seed, and the exact stage LCG together",
                    run.RoomKilledSeqIds.Count == 0
                    && run.Drops.Count == 0
                    && run.RoomMonsters.Count == 1
                    && run.RoomMonsters[0].Code == 10506
                    && run.RoomStartSequence == 61
                    && run.Seed == 0x55555555
                    && stageLcg != null
                    && ReferenceEquals(run.RoomLcg, stageLcg),
                    ref failures);
                invoked.Dispose();
                dtoBuilt.Dispose();
            }
        }

        private sealed class CoordinatedStageMonsterList : IReadOnlyList<StageMonster>
        {
            private readonly IReadOnlyList<StageMonster> _items;
            private readonly ManualResetEventSlim _enumerationCompleted;

            public CoordinatedStageMonsterList(
                IReadOnlyList<StageMonster> items,
                ManualResetEventSlim enumerationCompleted = null)
            {
                _items = items ?? throw new ArgumentNullException(nameof(items));
                _enumerationCompleted = enumerationCompleted;
            }

            public bool WasEnumerated { get; private set; }
            public int Count => _items.Count;
            public StageMonster this[int index] => _items[index];

            public IEnumerator<StageMonster> GetEnumerator()
            {
                WasEnumerated = true;
                for (var index = 0; index < _items.Count; index++)
                    yield return _items[index];
                _enumerationCompleted?.Set();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static bool HasRoomMonster(
            EnhancedClientSession session,
            int monsterCode)
        {
            var monsters = session?.Player?.CurrentRun?.RoomMonsters;
            if (monsters == null)
                return false;
            foreach (var monster in monsters)
            {
                if (monster.Code == monsterCode)
                    return true;
            }
            return false;
        }

        private static bool IsTowerApcInfoBody(
            byte[] body,
            EnhancedClientSession session,
            byte expectedLayer,
            IReadOnlyList<int> expectedAppearance,
            byte[] expectedCreatureName,
            uint expectedCreatureItemId)
        {
            var name = session?.Player?.Name ?? Array.Empty<byte>();
            expectedCreatureName = expectedCreatureName ?? Array.Empty<byte>();
            if (body == null || body.Length != 112 + name.Length + expectedCreatureName.Length)
                return false;
            if (body[0] != expectedLayer || BitConverter.ToInt32(body, 1) != name.Length)
                return false;
            for (var index = 0; index < name.Length; index++)
            {
                if (body[5 + index] != name[index])
                    return false;
            }

            var offset = 5 + name.Length;
            if (body[offset++] != session.Player.Level
                || body[offset++] != session.Player.Job
                || body[offset++] != session.Player.GrowType)
            {
                return false;
            }

            var guildNameLength = BitConverter.ToInt32(body, offset);
            offset += 4;
            if (guildNameLength != 0 || BitConverter.ToInt32(body, offset) != 0)
                return false;
            offset += 4;

            for (var index = 0; index < 22; index++)
            {
                var expectedItemId = index < expectedAppearance.Count
                    ? expectedAppearance[index]
                    : 0;
                if (BitConverter.ToInt32(body, offset) != expectedItemId)
                    return false;
                offset += 4;
            }

            if (BitConverter.ToInt32(body, offset) != expectedCreatureName.Length)
                return false;
            offset += 4;
            for (var index = 0; index < expectedCreatureName.Length; index++)
            {
                if (body[offset + index] != expectedCreatureName[index])
                    return false;
            }

            offset += expectedCreatureName.Length;
            return BitConverter.ToUInt32(body, offset) == expectedCreatureItemId;
        }

        private sealed class ThrowingListCharacterRepository : Game.Characters.ICharacterRepository
        {
            private readonly Game.Characters.ICharacterRepository _inner;

            public ThrowingListCharacterRepository(Game.Characters.ICharacterRepository inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public Game.Characters.CharacterRecord GetById(int characterId) => _inner.GetById(characterId);
            public IReadOnlyList<Game.Characters.CharacterRecord> ListByAccount(int accountId)
                => throw new InvalidOperationException("Injected summary construction failure.");
            public int Create(Game.Characters.CharacterRecord record) => _inner.Create(record);
            public void UpdatePosition(int characterId, byte townId, byte areaId, short posX, short posY, byte direction, byte areaState)
                => _inner.UpdatePosition(characterId, townId, areaId, posX, posY, direction, areaState);
            public void UpdateSeedFields(
                int characterId,
                byte[] name,
                byte job,
                byte growType,
                byte level,
                byte pvpGrade,
                byte pvpRatingGrade,
                byte userState,
                Game.Characters.CharacterAppearanceEntry[] appearance,
                DateTime? createdAt = null)
                => _inner.UpdateSeedFields(
                    characterId,
                    name,
                    job,
                    growType,
                    level,
                    pvpGrade,
                    pvpRatingGrade,
                    userState,
                    appearance,
                    createdAt);
            public void UpdateAppearance(int characterId, Game.Characters.CharacterAppearanceEntry[] appearance)
                => _inner.UpdateAppearance(characterId, appearance);
            public void SoftDelete(int characterId) => _inner.SoftDelete(characterId);
            public Game.Characters.CharacterRecord GetByName(string name) => _inner.GetByName(name);
            public int CountByAccount(int accountId) => _inner.CountByAccount(accountId);
            public void SwapSlotIndexes(int accountId, byte slotA, byte slotB)
                => _inner.SwapSlotIndexes(accountId, slotA, slotB);
        }

        private sealed class SelectDungeonFixture : IDisposable
        {
            private const int CharacterId = 484101;
            private const int AccountId = 484101;

            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _accepted;
            private readonly string _dbPath;
            private readonly SqliteInventoryStore _inventoryStore;
            private readonly SqliteAssetService _assetService;
            private readonly DailyResetService _dailyReset;
            private readonly string _previousDatabasePath;

            public EnhancedClientSession Session { get; }
            public IAssetService AssetService => _assetService;

            private SelectDungeonFixture(
                TcpListener listener,
                TcpClient client,
                TcpClient accepted,
                EnhancedClientSession session,
                string dbPath,
                SqliteInventoryStore inventoryStore,
                SqliteAssetService assetService,
                DailyResetService dailyReset,
                string previousDatabasePath)
            {
                _listener = listener;
                _client = client;
                _accepted = accepted;
                Session = session;
                _dbPath = dbPath;
                _inventoryStore = inventoryStore;
                _assetService = assetService;
                _dailyReset = dailyReset;
                _previousDatabasePath = previousDatabasePath;
            }

            public static SelectDungeonFixture Create()
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
                Directory.CreateDirectory(tempDir);
                var dbPath = Path.Combine(
                    tempDir,
                    $"death-tower-entry-{Guid.NewGuid():N}.db");
                var previousDatabasePath = Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", dbPath);

                SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                SeedAccountAndCharacter(dbPath);
                Game.Quests.QuestService.SaveActiveQuests(
                    SqliteDatabaseBootstrap.BuildConnectionString(dbPath),
                    CharacterId,
                    new List<Game.Quests.ActiveQuest>
                    {
                        new Game.Quests.ActiveQuest
                        {
                            Slot = 0,
                            QuestId = 932,
                            TriggerValue = 10,
                        },
                    });

                var inventoryStore = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
                var assetService = new SqliteAssetService(dbPath, ServerPaths.SchemaFilePath, inventoryStore);
                var dailyReset = new DailyResetService(dbPath, ServerPaths.SchemaFilePath);

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var accepted = listener.AcceptTcpClient();
                connectTask.GetAwaiter().GetResult();

                var session = new EnhancedClientSession(accepted, new GamePacketHeader());
                session.Player.CharacterId = CharacterId;
                session.Player.UserId = 1;
                session.Player.Level = 50;
                session.Player.Job = 4;
                session.Player.GrowType = 4;
                session.Account = new AccountRecord
                {
                    AccountId = AccountId,
                    MId = "death-tower-entry",
                };

                return new SelectDungeonFixture(
                    listener,
                    client,
                    accepted,
                    session,
                    dbPath,
                    inventoryStore,
                    assetService,
                    dailyReset,
                    previousDatabasePath);
            }

            public Game.Characters.SqliteCharacterRepository CreateCharacterRepository()
            {
                return new Game.Characters.SqliteCharacterRepository(
                    _dbPath,
                    ServerPaths.SchemaFilePath);
            }

            public DungeonHandler CreateDungeonHandler(
                AccountExperienceProgressService accountExperience = null)
            {
                var characterRepository = CreateCharacterRepository();
                var selectCharacterDataSource = new Game.SelectCharacter.SqliteSelectCharacterDataSource(
                    _dbPath,
                    ServerPaths.SchemaFilePath,
                    characterRepository,
                    _assetService,
                    _inventoryStore,
                    SystemRentalTimeProvider.Instance);
                var reviveCoin = new ReviveCoinService(_inventoryStore, _assetService, _dailyReset);

                var inventoryRefresh = new Network.Handlers.InventoryRefreshSender(
                    _inventoryStore, selectCharacterDataSource, characterRepository);
                var questDropService = new Game.Quests.QuestDropService(
                    _assetService,
                    inventoryRefresh,
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath),
                    (candidate, held) => 1);
                return new DungeonHandler(
                    _assetService,
                    reviveCoin,
                    characterRepository,
                    selectCharacterDataSource,
                    SystemRentalTimeProvider.Instance,
                    _inventoryStore,
                    inventoryRefresh,
                    questDropService: questDropService,
                    accountExperience: accountExperience);
            }

            public DeathTowerHandler CreateDeathTowerHandler(
                Func<EnhancedClientSession, DeathTowerSettlementResult, Task> sendExpGrantNotification,
                DeathTowerExperienceGrantInTransaction grantExperienceInTransaction = null)
            {
                var characterRepository = CreateCharacterRepository();
                var selectCharacterDataSource = new Game.SelectCharacter.SqliteSelectCharacterDataSource(
                    _dbPath,
                    ServerPaths.SchemaFilePath,
                    characterRepository,
                    _assetService,
                    _inventoryStore,
                    SystemRentalTimeProvider.Instance);
                var inventoryRefresh = new Network.Handlers.InventoryRefreshSender(
                    _inventoryStore,
                    selectCharacterDataSource,
                    characterRepository);
                return new DeathTowerHandler(
                    _inventoryStore,
                    _assetService,
                    grantExperienceInTransaction,
                    sendExpGrantNotification,
                    CreateAccountExperienceService(),
                    inventoryRefresh: inventoryRefresh);
            }

            public int CountPersistentItem(int itemId)
            {
                using (var scope = _assetService.OpenScope(CharacterId, AccountId))
                    return _assetService.CountItem(scope, itemId);
            }

            public int LoadGold()
            {
                using (var scope = _assetService.OpenScope(CharacterId, AccountId))
                    return _assetService.LoadWallet(scope).Gold;
            }

            public int CountPersistentMainItems()
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT COUNT(*)
FROM character_items
WHERE owner_scope = 'character'
  AND owner_id = @cid
  AND list_type = 0;";
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        return Convert.ToInt32(command.ExecuteScalar());
                    }
                }
            }

            public AccountExperienceProgressService CreateAccountExperienceService(
                Game.Characters.ICharacterRepository characterRepository = null)
            {
                return new AccountExperienceProgressService(
                    characterRepository ?? CreateCharacterRepository(),
                    _dbPath,
                    ServerPaths.SchemaFilePath);
            }

            public bool HasPendingPacket() => _client.Available > 0;

            public void SetCharacterProgress(byte level, uint exp)
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
UPDATE characters
SET level = @level, exp = @exp
WHERE character_id = @cid;";
                        command.Parameters.AddWithValue("@level", level);
                        command.Parameters.AddWithValue("@exp", (long)exp);
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        command.ExecuteNonQuery();
                    }
                }

                Session.Player.Level = level;
                Session.Player.Exp = exp;
            }

            public AccountProgressSnapshot LoadAccountProgress()
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT honor_exp, growth_capsule_exp
FROM accounts
WHERE account_id = @aid;";
                        command.Parameters.AddWithValue("@aid", AccountId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                                return default;
                            return new AccountProgressSnapshot(
                                (ulong)reader.GetInt64(0),
                                (uint)reader.GetInt64(1));
                        }
                    }
                }
            }

            public bool PersistedProgressMatches(byte level, uint exp)
            {
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath)))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT level, exp
FROM characters
WHERE character_id = @cid;";
                        command.Parameters.AddWithValue("@cid", CharacterId);
                        using (var reader = command.ExecuteReader())
                        {
                            return reader.Read()
                                && reader.GetInt32(0) == level
                                && (uint)reader.GetInt64(1) == exp;
                        }
                    }
                }
            }

            public bool ItemUpdateMatchesDatabase(byte[] body)
            {
                if (body == null
                    || body.Length < 3
                    || body[0] != (byte)InventoryListType.Main)
                    return false;

                var count = BitConverter.ToUInt16(body, 1);
                if (count == 0 || body.Length != 3 + count * 84)
                    return false;

                for (var index = 0; index < count; index++)
                {
                    var offset = 3 + index * 84;
                    var slot = BitConverter.ToInt16(body, offset);
                    var itemId = BitConverter.ToInt32(body, offset + 2);
                    var stackCount = BitConverter.ToInt32(body, offset + 6);
                    var persisted = _inventoryStore.LoadCommonItemForRefresh(
                        CharacterId,
                        AccountId,
                        InventoryListType.Main,
                        slot);
                    if (persisted == null
                        || persisted.SlotIndex != slot
                        || persisted.ItemTemplateId != itemId
                        || persisted.CountOrInstanceValue != stackCount)
                    {
                        return false;
                    }
                }

                return true;
            }

            public List<ushort> ReadSentTypes(int expectedPackets)
            {
                var result = new List<ushort>();
                foreach (var packet in ReadSentPackets(expectedPackets))
                    result.Add(packet.Type);
                return result;
            }

            public List<CapturedPacket> ReadSentPackets(int expectedPackets)
            {
                var result = new List<CapturedPacket>();
                _client.ReceiveTimeout = 2000;

                for (var i = 0; i < expectedPackets; i++)
                {
                    var header = ReadExact(15);
                    var type = BitConverter.ToUInt16(header, 1);
                    var packetLength = BitConverter.ToInt32(header, 3);
                    var bodyLength = packetLength - 15;
                    var body = bodyLength > 0 ? ReadExact(bodyLength) : Array.Empty<byte>();
                    result.Add(new CapturedPacket(type, body));
                }

                return result;
            }

            public readonly struct CapturedPacket
            {
                public CapturedPacket(ushort type, byte[] body)
                {
                    Type = type;
                    Body = body;
                }

                public ushort Type { get; }
                public byte[] Body { get; }
            }

            public readonly struct AccountProgressSnapshot : IEquatable<AccountProgressSnapshot>
            {
                public AccountProgressSnapshot(ulong honorExp, uint growthCapsuleExp)
                {
                    HonorExp = honorExp;
                    GrowthCapsuleExp = growthCapsuleExp;
                }

                public ulong HonorExp { get; }
                public uint GrowthCapsuleExp { get; }

                public bool Equals(AccountProgressSnapshot other)
                {
                    return HonorExp == other.HonorExp
                        && GrowthCapsuleExp == other.GrowthCapsuleExp;
                }

                public override bool Equals(object obj)
                {
                    return obj is AccountProgressSnapshot other && Equals(other);
                }

                public override int GetHashCode()
                {
                    unchecked
                    {
                        return ((int)HonorExp * 397) ^ (int)GrowthCapsuleExp;
                    }
                }
            }

            private byte[] ReadExact(int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = _client.GetStream().Read(buffer, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }

                return buffer;
            }

            private static void SeedAccountAndCharacter(string dbPath)
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');
INSERT OR IGNORE INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@cid, @aid, @name, 4, 4, 50);
INSERT OR IGNORE INTO character_subtype1_fields (character_id)
VALUES (@cid);";
                        cmd.Parameters.AddWithValue("@aid", AccountId);
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.Parameters.AddWithValue("@mid", "death-tower-entry");
                        cmd.Parameters.AddWithValue("@name", "death-tower-entry");
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            public void Dispose()
            {
                _accepted.Dispose();
                _client.Dispose();
                _listener.Stop();
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    _previousDatabasePath);
            }
        }
    }
}
