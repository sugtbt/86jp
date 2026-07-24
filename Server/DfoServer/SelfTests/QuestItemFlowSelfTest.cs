using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestItemFlowSelfTest
    {
        private const int CharacterId = 135001;
        private const int LevelUpCharacterId = 135002;
        private const int MaxLevelCharacterId = 135003;
        private const int MaxLevelOverflowCharacterId = 135004;
        private const int AccountId = 135001;
        private const ushort GiveLetterQuestId = 2042;
        private const ushort UseLetterQuestId = 2043;
        private const int AganzoLetterItemId = 10089292;
        private const ushort NonCarryEventQuestId = 2578;
        private const int NonCarryEventItemId = 10100257;
        private const ushort GreenStoneQuestId = 1849;
        private const int GreenStonePassiveObjectCode = 52853;
        private const int ChessboardDespairDungeonId = 160;
        private const int GreenLightStoneFragmentItemId = 10099811;
        private const ushort HelixMechanicalFragmentQuestId = 8402;
        private const ushort HelixMv002QuestId = 8404;
        private const ushort HelixEnergyDebrisQuestId = 8406;
        private const int HelixLabDungeonId = 3900;
        private const int MechanicalFragmentItemId = 10092628;
        private const int Mv002PartItemId = 10092629;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_ITEM_FLOW selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-item-flow.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-item-flow-test"),
                Job = 0,
                GrowType = 0,
                Level = 49,
            });
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = LevelUpCharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-level-up-test"),
                Job = 0,
                GrowType = 0,
                Level = 1,
            });
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = MaxLevelCharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-honor-exp-test"),
                Job = 0,
                GrowType = 0,
                Level = ExpTableProvider.MaxLevel,
            });
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = MaxLevelOverflowCharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-honor-overflow-test"),
                Job = 0,
                GrowType = 0,
                Level = ExpTableProvider.MaxLevel - 1,
            });
            SeedSubtype1Stats(dbPath, schemaPath, MaxLevelCharacterId, job: 0, level: ExpTableProvider.MaxLevel);
            SeedSubtype1Stats(dbPath, schemaPath, MaxLevelOverflowCharacterId, job: 0, level: ExpTableProvider.MaxLevel - 1);

            var assetService = new SqliteAssetService(dbPath, schemaPath);
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr, assetService);
            MarkQuestCleared(connStr, 2041);
            var failures = 0;

            foreach (var questId in new[] { 1776, 1777, 1778 })
            {
                var defaultGoldReward = QuestData.GetRewardExp(
                    questId, playerLevel: 6, playerJob: 0, playerGrowType: 0);
                Check($"quest {questId} applies default gold multiplier",
                    defaultGoldReward.Gold > 0,
                    ref failures);
            }
            Check("quest 1778 uses PVF quest level as gold table index",
                QuestData.GetRewardExp(1778, playerLevel: 5, playerJob: 0, playerGrowType: 0).Gold == 216,
                ref failures);
            Check("quest 1778 gold remains stable after character level-up",
                QuestData.GetRewardExp(1778, playerLevel: 6, playerJob: 0, playerGrowType: 0).Gold == 216,
                ref failures);
            Check("quest 2490 level 85 gold matches client display",
                QuestData.GetRewardExp(2490, playerLevel: 85, playerJob: 11, playerGrowType: 4).Gold == 7344,
                ref failures);

            var greenStoneQuest = QuestData.GetQuestFile(GreenStoneQuestId);
            Check("green stone quest parses passive object reward",
                greenStoneQuest != null
                    && greenStoneQuest.EnemyRewardItems.Exists(e =>
                        e.EnemyCode == GreenStonePassiveObjectCode
                        && e.EnemyType == QuestDropProvider.EnemyTypePassiveObject
                        && e.DungeonId == ChessboardDespairDungeonId
                        && e.ItemId == GreenLightStoneFragmentItemId
                        && e.Count == 1
                        && e.DropRate == 100
                        && e.MaxStack == 5),
                ref failures);

            var greenStonePassiveCandidates = QuestDropProvider.CheckEnemyDrop(
                new[] { (int)GreenStoneQuestId },
                ChessboardDespairDungeonId,
                0,
                GreenStonePassiveObjectCode,
                QuestDropProvider.EnemyTypePassiveObject);
            Check("green stone passive object reward matches",
                greenStonePassiveCandidates != null
                    && greenStonePassiveCandidates.Count == 1
                    && greenStonePassiveCandidates[0].ItemId == GreenLightStoneFragmentItemId
                    && greenStonePassiveCandidates[0].Count == 1
                    && greenStonePassiveCandidates[0].DropRate == 100
                    && greenStonePassiveCandidates[0].MaxStack == 5,
                ref failures);

            var greenStoneMonsterCandidates = QuestDropProvider.CheckMonsterDrop(
                new[] { (int)GreenStoneQuestId },
                ChessboardDespairDungeonId,
                0,
                GreenStonePassiveObjectCode);
            Check("green stone passive object is not monster reward",
                greenStoneMonsterCandidates == null,
                ref failures);

            var mechanicalFragmentQuest =
                QuestData.GetQuestFile(HelixMechanicalFragmentQuestId);
            Check(
                "Helix mechanical fragment clear reward parses",
                mechanicalFragmentQuest != null
                    && mechanicalFragmentQuest.ClearRewardItems.Exists(
                        entry =>
                            entry.DungeonId == HelixLabDungeonId
                            && entry.Difficulty == -1
                            && entry.ItemId == MechanicalFragmentItemId
                            && entry.Count == 10
                            && entry.DropRate == 170
                            && entry.MaxStack == -1),
                ref failures);
            Check(
                "Helix mechanical fragment quest has no per-monster source",
                mechanicalFragmentQuest != null
                    && mechanicalFragmentQuest.MonsterRewardItems.Count == 0,
                ref failures);

            var energyDebrisQuest =
                QuestData.GetQuestFile(HelixEnergyDebrisQuestId);
            Check(
                "Helix energy debris quest owns the per-monster source",
                energyDebrisQuest != null
                    && energyDebrisQuest.MonsterRewardItems.Count == 33
                    && energyDebrisQuest.MonsterRewardItems.TrueForAll(
                        entry =>
                            entry.MonsterCode >= 64900
                            && entry.MonsterCode <= 64932
                            && entry.DungeonId == HelixLabDungeonId
                            && entry.Difficulty == -1
                            && entry.ItemId == MechanicalFragmentItemId
                            && entry.Count == 1
                            && entry.DropRate == 50
                            && entry.MaxStack == 10),
                ref failures);

            var mv002Candidates = QuestDropProvider.CheckClearReward(
                new[] { (int)HelixMv002QuestId },
                HelixLabDungeonId,
                0);
            Check(
                "MV-002 clear reward targets quest inventory",
                mv002Candidates != null
                    && mv002Candidates.Count == 1
                    && mv002Candidates[0].QuestId == HelixMv002QuestId
                    && mv002Candidates[0].ItemId == Mv002PartItemId
                    && mv002Candidates[0].Count == 1
                    && mv002Candidates[0].PreferQuestInventory,
                ref failures);

            using (var scope = assetService.OpenScope(
                CharacterId,
                AccountId))
            {
                Check(
                    "quest reward placement uses quest inventory slots",
                    assetService.TryAddItem(
                        scope,
                        Mv002PartItemId,
                        1,
                        ItemPlacementHint.QuestInventory,
                        out var questSlot)
                        && questSlot >=
                            SqliteInventoryStore.QuestBagSlotStart
                        && questSlot <=
                            SqliteInventoryStore.QuestBagSlotEnd,
                    ref failures);
                scope.Commit();
            }
            RemoveItem(assetService, Mv002PartItemId, 1);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = GiveLetterQuestId, TriggerValue = 0 },
            });
            var legacyFinish2042 = questService.HandleFinishQuest(CharacterId,
                BuildQuestBody(GiveLetterQuestId));
            Check("legacy active 2042 finish succeeds", IsSuccessAck(legacyFinish2042), ref failures);
            Check("legacy active 2042 finish grants missing letter", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);
            Check("legacy active 2042 finish ack inserts letter",
                TryReadFinishInsertedItem(legacyFinish2042, out _, out var finishItemId, out var finishCount)
                    && finishItemId == AganzoLetterItemId
                    && finishCount == 1,
                ref failures);

            ClearIssue135State(connStr);

            var accept2042 = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(GiveLetterQuestId), AccountId);
            Check("accept 2042 succeeds", IsSuccessAck(accept2042), ref failures);
            Check("accept 2042 gives letter event item", TryReadAcceptEventItem(accept2042, out var slot, out var itemId, out var count)
                && slot > 0
                && itemId == AganzoLetterItemId
                && count == 1,
                ref failures);
            Check("letter persisted after accept 2042", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = GiveLetterQuestId, TriggerValue = 0 },
            });

            var finish2042 = questService.HandleFinishQuest(CharacterId,
                BuildQuestBody(GiveLetterQuestId));
            Check("finish 2042 succeeds", IsSuccessAck(finish2042), ref failures);
            Check("letter remains for next quest after finish 2042", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);

            var accept2043 = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(UseLetterQuestId), AccountId);
            Check("accept 2043 succeeds", IsSuccessAck(accept2043), ref failures);
            Check("accept 2043 starts with only npc trigger after held letter is counted", TryReadAcceptTrigger(accept2043, out var initTrigger) && initTrigger == 512, ref failures);

            var matched = questService.SyncMonsterRewardItemProgress(CharacterId, AccountId,
                new[] { AganzoLetterItemId });
            Check("letter progress sync matches active quest", matched, ref failures);
            Check("letter progress clears only item channel", LoadTrigger(connStr, UseLetterQuestId) == 512, ref failures);

            var setNpcTrigger = questService.HandleSetTrigger(CharacterId, BuildSetTriggerBody(UseLetterQuestId, 0x20, false));
            Check("npc trigger ack succeeds", IsSuccessAck(setNpcTrigger), ref failures);
            Check(
                "set-trigger result preserves previous trigger",
                setNpcTrigger.PreviousTriggerValue == 512
                    && setNpcTrigger.TriggerValue == 0,
                ref failures);
            Check("npc trigger clears remaining channel", LoadTrigger(connStr, UseLetterQuestId) == 0, ref failures);

            var finish2043 = questService.HandleFinishQuest(CharacterId,
                BuildQuestBody(UseLetterQuestId));
            Check("finish 2043 succeeds", IsSuccessAck(finish2043), ref failures);
            Check("letter consumed by seek quest finish", CountItem(assetService, AganzoLetterItemId) == 0, ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = UseLetterQuestId, TriggerValue = 1 },
            });
            AddItem(assetService, AganzoLetterItemId, 1);
            var sender = new RecordingQuestSender(CharacterId, AccountId);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.SyncItemSeekingQuestProgressAsync(new[] { AganzoLetterItemId }).GetAwaiter().GetResult();
            Check("generic item-seeking sync clears active quest item channel", LoadTrigger(connStr, UseLetterQuestId) == 0, ref failures);
            Check("generic item-seeking sync sends active quest refresh", sender.LastNotiType == 0x023F && sender.NotiCount == 1, ref failures);

            RemoveItem(assetService, AganzoLetterItemId, 1);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = UseLetterQuestId, TriggerValue = 1 },
            });
            var towerSender = new RecordingQuestSender(CharacterId, AccountId);
            var towerQuestManager = new QuestManager(towerSender, connStr, assetService);
            towerQuestManager.SyncItemSeekingQuestProgressAsync(
                new[] { AganzoLetterItemId },
                new Dictionary<int, int> { { AganzoLetterItemId, 1 } })
                .GetAwaiter().GetResult();
            Check("tower temporary holding clears item-seeking progress without persistent item",
                CountItem(assetService, AganzoLetterItemId) == 0
                    && LoadTrigger(connStr, UseLetterQuestId) == 0,
                ref failures);
            towerQuestManager.SyncItemSeekingQuestProgressAsync(new[] { AganzoLetterItemId })
                .GetAwaiter().GetResult();
            Check("pure SQLite recalibration rolls tower-only quest progress back",
                LoadTrigger(connStr, UseLetterQuestId) == 1
                    && towerSender.LastNotiType == 0x023F
                    && towerSender.NotiCount == 2,
                ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = UseLetterQuestId, TriggerValue = 0 },
            });
            var silentRollbackSender = new RecordingQuestSender(CharacterId, AccountId);
            var silentRollbackManager = new QuestManager(silentRollbackSender, connStr, assetService);
            silentRollbackManager.RecalibrateItemSeekingQuestProgressWithoutNotification(
                new[] { AganzoLetterItemId });
            Check("old-run replacement recalibrates tower quest progress without notification",
                LoadTrigger(connStr, UseLetterQuestId) == 1
                    && silentRollbackSender.NotiCount == 0,
                ref failures);

            AddItem(assetService, NonCarryEventItemId, 1);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = NonCarryEventQuestId, TriggerValue = 0 },
            });
            var finishNonCarryEventQuest = questService.HandleFinishQuest(CharacterId,
                BuildQuestBody(NonCarryEventQuestId));
            Check("non-carry event item quest finish succeeds", IsSuccessAck(finishNonCarryEventQuest), ref failures);
            Check("non-carry event item is consumed on finish", CountItem(assetService, NonCarryEventItemId) == 0, ref failures);

            RunQuestLevelUpStatsChecks(connStr, dbPath, schemaPath, characterRepository, assetService, ref failures);
            RunMaxLevelQuestHonorChecks(connStr, characterRepository, assetService, ref failures);
            RunMaxLevelOverflowQuestChecks(connStr, characterRepository, assetService, ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildQuestBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static byte[] BuildSetTriggerBody(ushort questId, byte triggerType, bool increment)
        {
            var body = new byte[4];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            body[2] = triggerType;
            body[3] = increment ? (byte)1 : (byte)0;
            return body;
        }

        private static bool IsSuccessAck(QuestAcceptResult result)
        {
            return result != null && result.Success;
        }

        private static bool IsSuccessAck(QuestFinishResult result)
        {
            return result != null && result.Success;
        }

        private static bool IsSuccessAck(QuestSetTriggerResult result)
        {
            return result != null && result.Success;
        }

        private static bool TryReadAcceptTrigger(QuestAcceptResult result, out uint trigger)
        {
            trigger = result != null ? result.InitTrigger : 0;
            return result != null && result.Success;
        }

        private static bool TryReadAcceptEventItem(QuestAcceptResult result, out ushort slot, out int itemId, out int count)
        {
            slot = 0;
            itemId = 0;
            count = 0;
            if (result == null || !result.Success || result.EventItems.Count < 1)
                return false;

            slot = result.EventItems[0].SlotIndex;
            itemId = result.EventItems[0].ItemId;
            count = result.EventItems[0].Count;
            return true;
        }

        private static bool TryReadFinishInsertedItem(QuestFinishResult result, out ushort slot, out int itemId, out int count)
        {
            slot = 0;
            itemId = 0;
            count = 0;
            if (result == null || !result.Success || result.ChainType != 0 || result.InsertedEntries.Count < 1)
                return false;

            slot = result.InsertedEntries[0].SlotIndex;
            itemId = result.InsertedEntries[0].ItemId;
            count = (int)result.InsertedEntries[0].CountOrSeed;
            return true;
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
        }

        private static int CountItem(IAssetService assetService, int itemId)
        {
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                return assetService.CountItem(scope, itemId);
            }
        }

        private static void AddItem(IAssetService assetService, int itemId, int count)
        {
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                short assignedSlot;
                if (!assetService.TryAddItem(scope, itemId, count, out assignedSlot))
                    throw new InvalidOperationException($"failed to add item {itemId}");
                scope.Commit();
            }
        }

        private static void RemoveItem(IAssetService assetService, int itemId, int count)
        {
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                if (!assetService.TryRemoveItem(scope, itemId, count, out _, out _))
                    throw new InvalidOperationException($"failed to remove item {itemId}");
                scope.Commit();
            }
        }

        private static void RunQuestLevelUpStatsChecks(
            string connStr,
            string dbPath,
            string schemaPath,
            SqliteCharacterRepository characterRepository,
            IAssetService assetService,
            ref int failures)
        {
            var questId = SelectPlainExpQuest();
            Check("plain exp reward quest found", questId > 0, ref failures);
            if (questId <= 0)
                return;

            var reward = GameWorld.QuestData.GetRewardExp(questId, playerLevel: 1, playerJob: 0, playerGrowType: 0);
            var level2Threshold = (uint)ExpTableProvider.GetLevelThreshold(1);
            var startExp = reward.Exp >= level2Threshold ? 0u : level2Threshold - reward.Exp;

            characterRepository.UpdateLevelAndExp(LevelUpCharacterId, 1, startExp);
            SeedSubtype1Stats(dbPath, schemaPath, LevelUpCharacterId, job: 0, level: 1);
            var before = new SqliteSubtype1Repository(dbPath, schemaPath).Load(LevelUpCharacterId);

            QuestService.SaveActiveQuests(connStr, LevelUpCharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = questId, TriggerValue = 0 },
            });

            var player = new PlayerContext
            {
                CharacterId = LevelUpCharacterId,
                Job = 0,
                GrowType = 0,
                Level = 1,
                Exp = startExp,
            };
            var sender = new RecordingQuestSender(LevelUpCharacterId, AccountId, player);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.HandleFinishQuestAsync(0x003C, BuildQuestBody(questId)).GetAwaiter().GetResult();
            var ackExp = sender.LastAckBody != null && sender.LastAckBody.Length >= 8
                ? BitConverter.ToUInt32(sender.LastAckBody, 4)
                : 0;

            var record = characterRepository.GetById(LevelUpCharacterId);
            var after = new SqliteSubtype1Repository(dbPath, schemaPath).Load(LevelUpCharacterId);
            var expectedStats = CharacterStatComputer.BuildAdditionalInfo(0, player.Level);
            var expectedHp = BitConverter.ToUInt32(expectedStats, 0);
            var expectedPhysicalAttack = BitConverter.ToInt16(expectedStats, 8);

            Check("quest reward ack grants exp", ackExp > 0, ref failures);
            Check("quest reward levels character in memory", player.Level > 1, ref failures);
            Check("quest reward level persisted", record != null && record.Level == player.Level, ref failures);
            Check("quest reward subtype1 hp recomputed",
                before != null && after != null && after.StatHpMax == expectedHp && after.StatHpMax != before.StatHpMax,
                ref failures);
            Check("quest reward subtype1 attack recomputed",
                after != null && after.StatPhysicalAttack == expectedPhysicalAttack,
                ref failures);
            Check("quest reward sends subtype0 before exp notification",
                SendsSubtype0BeforeExp(sender, player.Level), ref failures);
            Check("quest reward sends subtype1 stats before exp notification",
                SendsSubtype1StatsBeforeExp(sender, expectedHp, expectedPhysicalAttack), ref failures);
            Check("quest reward sends exp notification", sender.NotiTypes.Contains(0x0025), ref failures);
            Check("quest reward exp notification carries account honor",
                ExpNotificationCarriesHonor(sender, expectedHonorLevel: 1, expectedHonorExp: 0), ref failures);
            Check("quest reward does not reload subtype1 after exp notification",
                !SendsSubtype1AfterExp(sender), ref failures);
        }

        private static void RunMaxLevelQuestHonorChecks(
            string connStr,
            SqliteCharacterRepository characterRepository,
            IAssetService assetService,
            ref int failures)
        {
            var questId = SelectPlainExpQuest();
            Check("max-level honor reward quest found", questId > 0, ref failures);
            if (questId <= 0)
                return;

            var reward = GameWorld.QuestData.GetRewardExp(
                questId, playerLevel: ExpTableProvider.MaxLevel, playerJob: 0, playerGrowType: 0);
            var maxLevelEntryExp = (uint)Math.Max(0,
                ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));
            var existingMaxLevelExp = maxLevelEntryExp + 123u;
            characterRepository.UpdateLevelAndExp(
                MaxLevelCharacterId, ExpTableProvider.MaxLevel, existingMaxLevelExp);
            QuestService.SaveActiveQuests(connStr, MaxLevelCharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = questId, TriggerValue = 0 },
            });

            var player = new PlayerContext
            {
                CharacterId = MaxLevelCharacterId,
                Job = 0,
                GrowType = 0,
                Level = ExpTableProvider.MaxLevel,
                Exp = existingMaxLevelExp,
            };
            var sender = new RecordingQuestSender(MaxLevelCharacterId, AccountId, player);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.HandleFinishQuestAsync(0x003C, BuildQuestBody(questId)).GetAwaiter().GetResult();

            var record = characterRepository.GetById(MaxLevelCharacterId);
            Check("max-level quest keeps normal exp fixed",
                record != null && record.Exp == existingMaxLevelExp && player.Exp == existingMaxLevelExp,
                ref failures);
            Check("max-level quest stores reward in account honor exp",
                LoadHonorExp(connStr) == reward.Exp,
                ref failures);
            Check("max-level quest stores reward in account growth capsule exp",
                LoadGrowthCapsuleExp(connStr) == GrowthCapsuleDataProvider.CalculateExpGain(reward.Exp),
                ref failures);
            Check("max-level quest sends honor through exp notification",
                ExpNotificationCarriesHonor(sender, expectedHonorLevel: 1, expectedHonorExp: reward.Exp),
                ref failures);
            Check("max-level quest sends growth capsule through exp notification",
                ExpNotificationCarriesGrowthCapsule(
                    sender,
                    GrowthCapsuleDataProvider.CalculateExpGain(reward.Exp)),
                ref failures);
            Check("max-level quest does not send repeated honor init notification",
                !sender.NotiTypes.Contains(0x0289),
                ref failures);
            Check("max-level quest does not reload subtype1",
                !sender.NotiTypes.Contains(0x0002),
                ref failures);
        }

        private static void RunMaxLevelOverflowQuestChecks(
            string connStr,
            SqliteCharacterRepository characterRepository,
            IAssetService assetService,
            ref int failures)
        {
            var questId = SelectPlainExpQuest();
            var reward = GameWorld.QuestData.GetRewardExp(
                questId, playerLevel: ExpTableProvider.MaxLevel - 1, playerJob: 0, playerGrowType: 0);
            Check("max-level overflow quest has splittable exp reward", reward.Exp >= 2, ref failures);
            if (questId <= 0 || reward.Exp < 2)
                return;

            var maxLevelEntryExp = (uint)Math.Max(0,
                ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));
            var normalExp = Math.Min(100u, reward.Exp / 2u);
            var overflowHonorExp = reward.Exp - normalExp;
            var startExp = maxLevelEntryExp - normalExp;
            var previousHonorExp = LoadHonorExp(connStr);
            var previousGrowthCapsuleExp = LoadGrowthCapsuleExp(connStr);
            characterRepository.UpdateLevelAndExp(
                MaxLevelOverflowCharacterId, ExpTableProvider.MaxLevel - 1, startExp);
            QuestService.SaveActiveQuests(connStr, MaxLevelOverflowCharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = questId, TriggerValue = 0 },
            });

            var player = new PlayerContext
            {
                CharacterId = MaxLevelOverflowCharacterId,
                Job = 0,
                GrowType = 0,
                Level = ExpTableProvider.MaxLevel - 1,
                Exp = startExp,
            };
            var sender = new RecordingQuestSender(MaxLevelOverflowCharacterId, AccountId, player);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.HandleFinishQuestAsync(0x003C, BuildQuestBody(questId)).GetAwaiter().GetResult();

            var record = characterRepository.GetById(MaxLevelOverflowCharacterId);
            Check("quest exp before max level reaches max with normal portion",
                record != null
                    && record.Level == ExpTableProvider.MaxLevel
                    && record.Exp == maxLevelEntryExp
                    && player.Level == ExpTableProvider.MaxLevel
                    && player.Exp == maxLevelEntryExp,
                ref failures);
            Check("quest exp overflow is stored as account honor exp",
                LoadHonorExp(connStr) == previousHonorExp + overflowHonorExp,
                ref failures);
            var expectedGrowthCapsuleExp = previousGrowthCapsuleExp
                + GrowthCapsuleDataProvider.CalculateExpGain(overflowHonorExp);
            Check("quest exp overflow is stored as account growth capsule exp",
                LoadGrowthCapsuleExp(connStr) == expectedGrowthCapsuleExp,
                ref failures);
            Check("mixed max-level quest sends normal exp notification",
                sender.NotiTypes.Contains(0x0025),
                ref failures);
            var expectedHonor = HonorLevelDataProvider.CalculateFromHonorExp(
                previousHonorExp + overflowHonorExp, 0);
            Check("mixed max-level quest exp notification carries honor",
                ExpNotificationCarriesHonor(
                    sender, expectedHonor.HonorLevel, expectedHonor.HonorExp),
                ref failures);
            Check("mixed max-level quest exp notification carries growth capsule",
                ExpNotificationCarriesGrowthCapsule(sender, expectedGrowthCapsuleExp),
                ref failures);
            Check("mixed max-level quest does not send repeated honor init notification",
                !sender.NotiTypes.Contains(0x0289),
                ref failures);
            Check("mixed max-level quest does not reload subtype1 after exp notification",
                !SendsSubtype1AfterExp(sender),
                ref failures);
        }

        private static bool ExpNotificationCarriesHonor(
            RecordingQuestSender sender,
            uint expectedHonorLevel,
            uint expectedHonorExp)
        {
            var exp = sender.Notis.FindLast(n => n.Item1 == 0x0025);
            return exp != null
                && exp.Item2 != null
                && exp.Item2.Length >= ExpNotificationBuilder.HonorExpOffset + sizeof(uint)
                && BitConverter.ToUInt32(exp.Item2, ExpNotificationBuilder.HonorLevelOffset) == expectedHonorLevel
                && BitConverter.ToUInt32(exp.Item2, ExpNotificationBuilder.HonorExpOffset) == expectedHonorExp;
        }

        private static bool ExpNotificationCarriesGrowthCapsule(
            RecordingQuestSender sender,
            uint totalGrowthCapsuleExp)
        {
            var exp = sender.Notis.FindLast(n => n.Item1 == 0x0025);
            var summary = GrowthCapsuleDataProvider.Calculate(totalGrowthCapsuleExp);
            var expected = summary.TotalExp;
            return exp != null
                && exp.Item2 != null
                && exp.Item2.Length >= ExpNotificationBuilder.GrowthCapsuleExpOffset + sizeof(uint)
                && BitConverter.ToUInt32(
                    exp.Item2, ExpNotificationBuilder.GrowthCapsuleExpOffset) == expected;
        }

        private static bool SendsSubtype1AfterExp(RecordingQuestSender sender)
        {
            var expIndex = sender.Notis.FindLastIndex(n => n.Item1 == 0x0025);
            return expIndex >= 0 && sender.Notis.FindIndex(
                expIndex + 1,
                n => n.Item1 == 0x0002 && n.Item2 != null && n.Item2.Length > 0 && n.Item2[0] == 1) >= 0;
        }

        private static bool SendsSubtype0BeforeExp(RecordingQuestSender sender, byte expectedLevel)
        {
            var subtype0Index = sender.Notis.FindIndex(n =>
                n.Item1 == 0x0002 && IsSubtype0LevelRefresh(n.Item2, expectedLevel));
            var expIndex = sender.Notis.FindIndex(n => n.Item1 == 0x0025);
            return subtype0Index >= 0 && expIndex >= 0 && subtype0Index < expIndex;
        }

        private static bool SendsSubtype1StatsBeforeExp(
            RecordingQuestSender sender,
            uint expectedHp,
            short expectedPhysicalAttack)
        {
            var subtype1Index = sender.Notis.FindIndex(n =>
                n.Item1 == 0x0002 && IsSubtype1StatRefresh(n.Item2, expectedHp, expectedPhysicalAttack));
            var expIndex = sender.Notis.FindIndex(n => n.Item1 == 0x0025);
            return subtype1Index >= 0 && expIndex >= 0 && subtype1Index < expIndex;
        }

        private static bool IsSubtype0LevelRefresh(byte[] body, byte expectedLevel)
        {
            if (body == null || body.Length < 12 || body[0] != 0)
                return false;

            int nameLength = BitConverter.ToInt32(body, 5);
            if (nameLength < 0)
                return false;

            int levelOffset = 9 + nameLength + 2;
            return levelOffset < body.Length && body[levelOffset] == expectedLevel;
        }

        private static bool IsSubtype1StatRefresh(
            byte[] body,
            uint expectedHp,
            short expectedPhysicalAttack)
        {
            if (body == null || body.Length < 23 || body[0] != 1)
                return false;

            var count = BitConverter.ToUInt16(body, 1);
            if (count == 0)
                return false;

            const int subtype1Offset = 5;
            var statCount = BitConverter.ToInt32(body, subtype1Offset + 4);
            var hp = BitConverter.ToUInt32(body, subtype1Offset + 8);
            var physicalAttack = BitConverter.ToInt16(body, subtype1Offset + 16);
            return statCount == 83
                && hp == expectedHp
                && physicalAttack == expectedPhysicalAttack;
        }

        private static ushort SelectPlainExpQuest()
        {
            ushort[] candidates =
            {
                GiveLetterQuestId,
                UseLetterQuestId,
                1776,
                1016,
                101,
            };

            foreach (var questId in candidates)
            {
                var reward = GameWorld.QuestData.GetRewardExp(questId, playerLevel: 1, playerJob: 0, playerGrowType: 0);
                if (reward.Exp > 0 && reward.ChainType == 0)
                    return questId;
            }

            return 0;
        }

        private static void SeedSubtype1Stats(string dbPath, string schemaPath, int characterId, byte job, byte level)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO character_subtype1_fields(character_id) VALUES(@cid);";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();
                }
            }

            var stats = CharacterStatComputer.BuildAdditionalInfo(job, level);
            new SqliteSubtype1Repository(dbPath, schemaPath).UpdateCombatStats(characterId, stats);
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "quest-item-flow-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static ulong LoadHonorExp(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT honor_exp FROM accounts WHERE account_id=@aid;";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    return (ulong)Math.Max(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                }
            }
        }

        private static uint LoadGrowthCapsuleExp(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT growth_capsule_exp FROM accounts WHERE account_id=@aid;";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    return (uint)Math.Max(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                }
            }
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO character_invisible_falgs (character_id, slot_index, flag_value)
VALUES (@cid, @qid, 1);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void ClearIssue135State(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_active_quests WHERE character_id=@cid AND quest_id IN (2042, 2043);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_items WHERE character_id=@cid AND item_template_id=@item;";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.Parameters.AddWithValue("@item", AganzoLetterItemId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_invisible_falgs WHERE character_id=@cid AND slot_index IN (2042, 2043);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingQuestSender : ISessionPacketSender
        {
            public RecordingQuestSender(int characterId, int accountId, PlayerContext player = null)
            {
                CharacterId = characterId;
                AccountId = accountId;
                Player = player;
            }

            public int CharacterId { get; }
            public int AccountId { get; }
            public PlayerContext Player { get; }
            public int NotiCount { get; private set; }
            public ushort LastNotiType { get; private set; }
            public List<ushort> NotiTypes { get; } = new List<ushort>();
            public List<Tuple<ushort, byte[]>> Notis { get; } = new List<Tuple<ushort, byte[]>>();
            public byte[] LastAckBody { get; private set; }

            public Task SendPacketAsync(byte[] rawPacket)
            {
                return Task.CompletedTask;
            }

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                NotiCount++;
                LastNotiType = notiType;
                NotiTypes.Add(notiType);
                Notis.Add(Tuple.Create(notiType, body));
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                LastAckBody = body;
                return Task.CompletedTask;
            }
        }
    }
}
