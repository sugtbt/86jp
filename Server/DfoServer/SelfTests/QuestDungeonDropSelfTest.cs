using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class QuestDungeonDropSelfTest
    {
        private const ushort NightmareDimensionQuestId = 2071;
        private const int NightmareSourceDungeonId = 171;
        private const int NightmareRatMonsterCode = 65636;
        private const int NightmareRatTailItemId = 10099749;
        private const ushort BlackChurchIntrusionQuestId = 2415;
        private const int BlackChurchDungeonId = 73;
        private const int BlackChurchAiCharacterCode = 21604;
        private const int BlackChurchQuestItemId = 4755;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_DUNGEON_DROP selftest ===");
            var failures = 0;

            VerifyConfiguredDrops(ref failures);
            VerifyNotificationBatcher(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyConfiguredDrops(ref int failures)
        {
            var nightmareDimensionQuest =
                QuestData.GetQuestFile(NightmareDimensionQuestId);
            Check(
                "normal monster enemy reward parses with type one",
                nightmareDimensionQuest != null
                    && nightmareDimensionQuest.EnemyRewardItems.Exists(entry =>
                        entry.EnemyCode == NightmareRatMonsterCode
                        && entry.EnemyType == QuestDropProvider.EnemyTypeMonster
                        && entry.DungeonId == NightmareSourceDungeonId
                        && entry.ItemId == NightmareRatTailItemId
                        && entry.Count == 1
                        && entry.DropRate == 60
                        && entry.MaxStack == 10),
                ref failures);

            var nightmareRatCandidates = QuestDropProvider.CheckMonsterDrop(
                new[] { (int)NightmareDimensionQuestId },
                NightmareSourceDungeonId,
                0,
                NightmareRatMonsterCode);
            Check(
                "normal monster death matches enemy reward type one",
                nightmareRatCandidates != null
                    && nightmareRatCandidates.Count == 1
                    && nightmareRatCandidates[0].QuestId
                        == NightmareDimensionQuestId
                    && nightmareRatCandidates[0].ItemId
                        == NightmareRatTailItemId
                    && nightmareRatCandidates[0].PreferQuestInventory,
                ref failures);

            Check(
                "quest drop is clipped to the remaining requirement",
                QuestDropProvider.RollDrop(
                    new QuestDropCandidate
                    {
                        Count = 3,
                        DropRate = 100,
                        MaxStack = 10,
                    },
                    currentHeld: 9) == 1,
                ref failures);

            var blackChurchQuest =
                QuestData.GetQuestFile(BlackChurchIntrusionQuestId);
            Check(
                "Black Church intrusion quest parses hostile APC reward",
                blackChurchQuest != null
                    && blackChurchQuest.EnemyRewardItems.Exists(entry =>
                        entry.EnemyCode == BlackChurchAiCharacterCode
                        && entry.EnemyType
                            == QuestDropProvider.EnemyTypeAiCharacter
                        && entry.DungeonId == BlackChurchDungeonId
                        && entry.Difficulty == -1
                        && entry.ItemId == BlackChurchQuestItemId
                        && entry.Count == 1
                        && entry.DropRate == 100
                        && entry.MaxStack == 15),
                ref failures);

            var blackChurchAiCandidates = QuestDropProvider.CheckEnemyDrop(
                new[] { (int)BlackChurchIntrusionQuestId },
                BlackChurchDungeonId,
                0,
                BlackChurchAiCharacterCode,
                QuestDropProvider.EnemyTypeAiCharacter);
            Check(
                "normal dungeon hostile APC matches AI-character quest drop",
                blackChurchAiCandidates != null
                    && blackChurchAiCandidates.Count == 1
                    && blackChurchAiCandidates[0].QuestId
                        == BlackChurchIntrusionQuestId
                    && blackChurchAiCandidates[0].ItemId
                        == BlackChurchQuestItemId
                    && blackChurchAiCandidates[0].Count == 1
                    && blackChurchAiCandidates[0].DropRate == 100
                    && blackChurchAiCandidates[0].MaxStack == 15
                    && blackChurchAiCandidates[0].PreferQuestInventory,
                ref failures);
            Check(
                "hostile APC reward does not match normal monster path",
                QuestDropProvider.CheckMonsterDrop(
                    new[] { (int)BlackChurchIntrusionQuestId },
                    BlackChurchDungeonId,
                    0,
                    BlackChurchAiCharacterCode) == null,
                ref failures);
            Check(
                "actor types five through eight use AI-character quest drops",
                DungeonCombatHandler.IsAiCharacterActorType(5)
                    && DungeonCombatHandler.IsAiCharacterActorType(6)
                    && DungeonCombatHandler.IsAiCharacterActorType(7)
                    && DungeonCombatHandler.IsAiCharacterActorType(8)
                    && !DungeonCombatHandler.IsAiCharacterActorType(4)
                    && !DungeonCombatHandler.IsAiCharacterActorType(9),
                ref failures);
        }

        private static void VerifyNotificationBatcher(ref int failures)
        {
            var session = new Network.EnhancedClientSession(
                new System.Net.Sockets.TcpClient(),
                null);
            session.Player.CharacterId = 135001;

            var questRefreshCount = 0;
            var inventoryRefreshCount = 0;
            var refreshedSlots = new HashSet<short>();
            var batcher = new QuestDropNotificationBatcher(
                _ =>
                {
                    questRefreshCount++;
                    return Task.CompletedTask;
                },
                (_, slots) =>
                {
                    inventoryRefreshCount++;
                    foreach (var slot in slots)
                        refreshedSlots.Add(slot);
                    return Task.CompletedTask;
                });

            batcher.Queue(session, true, new short[] { 178 });
            batcher.Queue(session, true, new short[] { 178, 179 });
            Check(
                "quest-drop refresh is deferred during a kill burst",
                questRefreshCount == 0 && inventoryRefreshCount == 0,
                ref failures);

            var flushed = batcher.FlushPendingAsync(session)
                .GetAwaiter()
                .GetResult();
            Check(
                "quest-drop burst sends one quest and one inventory refresh",
                flushed
                    && questRefreshCount == 1
                    && inventoryRefreshCount == 1,
                ref failures);
            Check(
                "quest-drop burst deduplicates changed inventory slots",
                refreshedSlots.SetEquals(new short[] { 178, 179 }),
                ref failures);
            Check(
                "quest-drop flush consumes the pending batch once",
                !batcher.FlushPendingAsync(session).GetAwaiter().GetResult()
                    && questRefreshCount == 1
                    && inventoryRefreshCount == 1,
                ref failures);

            session.Close();
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
