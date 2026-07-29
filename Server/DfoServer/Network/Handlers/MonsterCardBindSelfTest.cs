using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.SelfTests
{
    public static class MonsterCardBindSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MONSTERCARD_BIND selftest ===");
            var failures = 0;
            var catalog = MonsterCardBindCatalog.Parse(@"
[monstercard bind info]
0 100 0 1 200 0 2 300 0
[/monstercard bind info]
[monstercard bind list]
1000 0 500
1001 0 500
2000 1 100
2001 1 0
[/monstercard bind list]");

            var calls = 0;
            Check("same-rarity roll uses positive result weight",
                catalog.TryRollResult(2, 0, max => calls++ == 0 ? 9999 : max - 1, out var same)
                && same.Rarity == 0 && (same.ItemId == 1000 || same.ItemId == 1001), ref failures);
            calls = 0;
            Check("silver upgrade boundary selects next rarity pool",
                catalog.TryRollResult(2, 0, max => calls++ == 0 ? 299 : 0, out var upgraded)
                && upgraded.ItemId == 2000 && upgraded.Rarity == 1, ref failures);
            calls = 0;
            Check("silver upgrade boundary rejects roll 300",
                catalog.TryRollResult(2, 0, max => calls++ == 0 ? 300 : 0, out var boundary)
                && boundary.Rarity == 0, ref failures);
            calls = 0;
            Check("zero-weight result is excluded",
                catalog.TryRollResult(2, 1, max => calls++ == 0 ? 9999 : max - 1, out var weighted)
                && weighted.ItemId == 2000, ref failures);
            var liveCatalog = MonsterCardBindCatalog.Load();
            Check("live enchanter.exj bind type 2 resolves a same-rarity result",
                liveCatalog.TryRollResult(2, 0, max => max - 1, out var liveResult)
                && liveResult.ItemId > 0 && liveResult.Rarity == 0, ref failures);
            Check("success ACK echoes input slots and one result row", CheckSuccessAck(), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool CheckSuccessAck()
        {
            var result = new MonsterCardBindResult
            {
                ResultItemId = 3752,
                Grant = new InventoryRewardGrantResult { SlotIndex = 246 },
            };
            var body = MonsterCardBindAckBuilder.BuildSuccess(105, 241, 245, result);
            return body.Length == MonsterCardBindAckBuilder.SuccessLength
                && BitConverter.ToString(body) == "01-69-00-F1-00-F5-00-01-F6-00-A8-0E-00-00-01-00-00-00-00";
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine(condition ? $"  [PASS] {name}" : $"  [FAIL] {name}");
            if (!condition)
                failures++;
        }
    }
}
