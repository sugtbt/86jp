using DfoServer.Game.Premium;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.SelfTests
{
    internal static class QuestGrowthContractExpSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== Quest growth contract exp self-test ===");
            var failures = 0;

            var noContractTotal = QuestService.CalculateQuestExpReward(
                1001,
                2,
                new PremiumEffects(),
                out var noContractBase,
                out var noContractBonus);
            Check(
                "quest exp is unchanged without growth contract",
                noContractBase == 2002 && noContractBonus == 0 && noContractTotal == 2002,
                ref failures);

            var contractTotal = QuestService.CalculateQuestExpReward(
                1001,
                2,
                new PremiumEffects { BonusExpPercent = 20 },
                out var contractBase,
                out var contractBonus);
            Check(
                "growth contract bonus applies to multiplied quest exp",
                contractBase == 2002 && contractBonus == 400 && contractTotal == 2402,
                ref failures);

            var saturatedTotal = QuestService.CalculateQuestExpReward(
                uint.MaxValue,
                ushort.MaxValue,
                new PremiumEffects { BonusExpPercent = 20 },
                out var saturatedBase,
                out var saturatedBonus);
            Check(
                "quest exp multiplication and bonus addition saturate",
                saturatedBase == uint.MaxValue
                && saturatedBonus > 0
                && saturatedTotal == uint.MaxValue,
                ref failures);

            var notification = ExpNotificationBuilder.Build(
                level: 53,
                totalExp: contractTotal,
                skillPoints: default(SkillPointProtocolState),
                honorLevel: null,
                growthContractBonusExp: contractBonus);
            Check(
                "quest notification exposes growth contract bonus to client text",
                BitConverter.ToUInt32(
                    notification,
                    ExpNotificationBuilder.GrowthContractExpOffset) == contractBonus,
                ref failures);

            Console.WriteLine($"Quest growth contract exp self-test: {4 - failures} passed, {failures} failed");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}: {name}");
            if (!passed)
                failures++;
        }
    }
}
