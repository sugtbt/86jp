using System;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class QuestMonsterDropSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== QUEST_MONSTER_DROP selftest ===");

            var failures = 0;
            Check(
                "enemy reward item type 1 is routed through ordinary monster drops",
                ParsesAndMatchesEnemyTypeMonster(),
                ref failures);
            Check(
                "quest 2071 all three rats drop item 10099749 in dungeon 171",
                AnotherDimensionNorthMyreRatTailsMatch(),
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool ParsesAndMatchesEnemyTypeMonster()
        {
            const string content =
                "[enemy reward item]\n" +
                "65636 1 171 -1 10099749 1 60 10\n" +
                "[/enemy reward item]\n";
            var quest = QuestFile.Parse(content);
            var entry = quest.EnemyRewardItems.SingleOrDefault();

            return entry != null
                && entry.EnemyCode == 65636
                && entry.EnemyType == QuestDropProvider.EnemyTypeMonster
                && entry.DungeonId == 171
                && entry.Difficulty == -1
                && entry.ItemId == 10099749
                && entry.Count == 1
                && entry.DropRate == 60
                && entry.MaxStack == 10;
        }

        private static bool AnotherDimensionNorthMyreRatTailsMatch()
        {
            const int questId = 2071;
            const int dungeonId = 171;
            const int ratTailItemId = 10099749;

            var quest = QuestData.GetQuestFile(questId);
            var expectedRats = new[]
            {
                (MonsterCode: 65636, DropRate: 60),
                (MonsterCode: 65635, DropRate: 60),
                (MonsterCode: 58524, DropRate: 70),
            };

            return quest?.Name == "另一个次元的诺斯玛尔"
                && expectedRats.All(expected =>
                {
                    var candidates = QuestDropProvider.CheckMonsterDrop(
                        new[] { questId },
                        dungeonId,
                        difficulty: 0,
                        expected.MonsterCode);
                    return candidates != null
                        && candidates.Any(candidate =>
                            candidate.QuestId == questId
                            && candidate.ItemId == ratTailItemId
                            && candidate.Count == 1
                            && candidate.DropRate == expected.DropRate
                            && candidate.MaxStack == 10
                            && candidate.PreferQuestInventory);
                });
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
