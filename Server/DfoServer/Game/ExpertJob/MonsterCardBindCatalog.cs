using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class MonsterCardBindResultEntry
    {
        public int ItemId { get; set; }
        public int Rarity { get; set; }
        public int Weight { get; set; }
    }

    internal sealed class MonsterCardBindCatalog
    {
        private const int ProbabilityDenominator = 10000;
        private readonly Dictionary<int, int> _upgradeChances = new Dictionary<int, int>();
        private readonly List<MonsterCardBindResultEntry> _results = new List<MonsterCardBindResultEntry>();

        internal static MonsterCardBindCatalog Load()
            => Parse(PvfArchiveAccessor.ReadText("character/expertjob/enchanter.exj"));

        internal static MonsterCardBindCatalog Parse(string text)
        {
            var catalog = new MonsterCardBindCatalog();
            var infoValues = ReadInts(ExtractSection(text, "monstercard bind info"));
            for (var i = 0; i + 2 < infoValues.Count; i += 3)
            {
                var bindType = infoValues[i];
                var chance = infoValues[i + 1];
                if (bindType >= 0 && chance >= 0 && chance <= ProbabilityDenominator)
                    catalog._upgradeChances[bindType] = chance;
            }

            var listValues = ReadInts(ExtractSection(text, "monstercard bind list"));
            for (var i = 0; i + 2 < listValues.Count; i += 3)
            {
                var itemId = listValues[i];
                var rarity = listValues[i + 1];
                var weight = listValues[i + 2];
                if (itemId > 0 && rarity >= 0 && rarity <= 3 && weight >= 0)
                {
                    catalog._results.Add(new MonsterCardBindResultEntry
                    {
                        ItemId = itemId,
                        Rarity = rarity,
                        Weight = weight,
                    });
                }
            }

            if (catalog._upgradeChances.Count == 0 || catalog._results.Count == 0)
                throw new FormatException("enchanter.exj is missing monstercard bind info/list.");
            return catalog;
        }

        internal bool TryRollResult(int bindType, int inputRarity, out MonsterCardBindResultEntry result)
            => TryRollResult(bindType, inputRarity, max => ServerRandom.Next(checked((int)max)), out result);

        internal bool TryRollResult(
            int bindType,
            int inputRarity,
            Func<long, long> next,
            out MonsterCardBindResultEntry result)
        {
            result = null;
            if (!_upgradeChances.TryGetValue(bindType, out var upgradeChance)
                || inputRarity < 0 || inputRarity > 3 || next == null)
                return false;

            var targetRarity = inputRarity;
            if (inputRarity < 3 && next(ProbabilityDenominator) < upgradeChance)
                targetRarity++;

            long totalWeight = 0;
            foreach (var entry in _results)
            {
                if (entry.Rarity == targetRarity && entry.Weight > 0)
                    totalWeight += entry.Weight;
            }
            if (totalWeight <= 0 || totalWeight > int.MaxValue)
                return false;

            var roll = next(totalWeight);
            foreach (var entry in _results)
            {
                if (entry.Rarity != targetRarity || entry.Weight <= 0)
                    continue;
                if (roll < entry.Weight)
                {
                    result = entry;
                    return true;
                }
                roll -= entry.Weight;
            }
            return false;
        }

        private static string ExtractSection(string text, string name)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var startTag = "[" + name + "]";
            var endTag = "[/" + name + "]";
            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;
            var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        }

        private static List<int> ReadInts(string text)
        {
            var values = new List<int>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+"))
            {
                if (int.TryParse(match.Value, out var value))
                    values.Add(value);
            }
            return values;
        }
    }
}
