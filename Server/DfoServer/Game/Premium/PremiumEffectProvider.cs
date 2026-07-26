using System;

namespace DfoServer.Game.Premium
{
    // premiumlist_new.etc 中契约的效果字段。
    public sealed class PremiumEffects
    {
        public int OverSkillLevel;
        public int BonusExpPercent;
        public int QuestItemDropRatePercent;
        public int[] IndependentDropRatePercentByContractMemberCount = Array.Empty<int>();

        // 经验加成 = baseExp * BonusExpPercent / 100, 向下取整; 无加成返回 0。
        public uint ComputeBonusExp(uint baseExp)
        {
            if (baseExp == 0 || BonusExpPercent <= 0)
                return 0;
            var value = baseExp * BonusExpPercent / 100f;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        public int GetIndependentDropRatePercent(int contractMemberCount)
        {
            if (contractMemberCount <= 0
                || IndependentDropRatePercentByContractMemberCount == null
                || IndependentDropRatePercentByContractMemberCount.Length == 0)
                return 0;

            var index = Math.Min(
                contractMemberCount,
                IndependentDropRatePercentByContractMemberCount.Length) - 1;
            return Math.Max(0, IndependentDropRatePercentByContractMemberCount[index]);
        }
    }

    public static class PremiumEffectProvider
    {
        // 扫描该账号全部未过期契约，各标量效果取最大值、队伍人数表逐项取最大值合并。
        // PVF 读取/解析失败由 PremiumCatalog.Load 直接抛出, 不做静默兜底。
        public static PremiumEffects GetCombinedEffects(string connStr, int accountId)
        {
            var combined = new PremiumEffects();
            if (accountId <= 0)
                return combined;

            var catalog = PremiumCatalog.Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type FROM account_premiums WHERE account_id = @aid AND end_time > @now";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var effects = catalog.GetEffects(reader.GetInt32(0));
                            if (effects == null)
                                continue;

                            if (effects.OverSkillLevel > combined.OverSkillLevel)
                                combined.OverSkillLevel = effects.OverSkillLevel;
                            if (effects.BonusExpPercent > combined.BonusExpPercent)
                                combined.BonusExpPercent = effects.BonusExpPercent;
                            if (effects.QuestItemDropRatePercent > combined.QuestItemDropRatePercent)
                                combined.QuestItemDropRatePercent = effects.QuestItemDropRatePercent;
                            MergeIndependentDropRates(
                                combined,
                                effects.IndependentDropRatePercentByContractMemberCount);
                        }
                    }
                }
            }

            return combined;
        }

        private static void MergeIndependentDropRates(
            PremiumEffects combined,
            int[] candidateRates)
        {
            if (candidateRates == null || candidateRates.Length == 0)
                return;

            if (combined.IndependentDropRatePercentByContractMemberCount.Length < candidateRates.Length)
            {
                Array.Resize(
                    ref combined.IndependentDropRatePercentByContractMemberCount,
                    candidateRates.Length);
            }

            for (var i = 0; i < candidateRates.Length; i++)
            {
                if (candidateRates[i] > combined.IndependentDropRatePercentByContractMemberCount[i])
                    combined.IndependentDropRatePercentByContractMemberCount[i] = candidateRates[i];
            }
        }
    }
}
