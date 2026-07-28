using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal struct QuestReward
    {
        public uint Exp;
        public uint Gold;
        public int ChainType;
        public int GrowNumber;
        public int CreatureKind;
        public int CreatureLevel;
        public List<QuestRewardItem> Items;
        public List<QuestRewardItem> ConsumeItems;
    }

    internal sealed class QuestRewardResolution
    {
        private QuestRewardResolution(
            bool isValid,
            QuestReward reward,
            string error)
        {
            IsValid = isValid;
            Reward = reward;
            Error = error ?? string.Empty;
        }

        internal bool IsValid { get; }
        internal QuestReward Reward { get; }
        internal string Error { get; }

        internal static QuestRewardResolution Valid(QuestReward reward)
            => new QuestRewardResolution(true, reward, string.Empty);

        internal static QuestRewardResolution Invalid(
            QuestReward emptyReward,
            string error)
            => new QuestRewardResolution(false, emptyReward, error);
    }

    internal struct QuestRewardItem
    {
        public int ItemId;
        public int Count;
    }

    internal static class QuestRewardProjector
    {
        internal const int ChainTypeSlotExpansion = 21;

        private static readonly Lazy<QuestParameterTable> Parameters =
            new Lazy<QuestParameterTable>(LoadParameters);

        internal static QuestRewardResolution Resolve(
            int questId,
            int rewardSelectIdx,
            int playerLevel,
            int playerJob,
            int playerGrowType)
        {
            var empty = CreateEmptyReward();
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
            {
                return QuestRewardResolution.Invalid(
                    empty,
                    "quest definition not found");
            }

            try
            {
                var chainType = MapRewardType(quest.RewardType);
                var questMinLevel = quest.Level != null && quest.Level.Length > 0
                    ? quest.Level[0]
                    : 1;
                var difficulty = quest.Difficulty != null
                    && quest.Difficulty.Length > 0
                        ? quest.Difficulty[0]
                        : 'G';
                var ignoreLevel = quest.IgnoreQuestLevel4Exp;
                var isRepeatable = string.Equals(
                        (quest.Grade ?? string.Empty).Trim(),
                        "[normaly repeat]",
                        StringComparison.OrdinalIgnoreCase)
                    || QuestData.NormalizeQuestTag(quest.Type)
                        == "seeking repeat";

                var exp = isRepeatable
                    ? 0
                    : Parameters.Value.ComputeExp(
                        playerLevel,
                        questMinLevel,
                        difficulty,
                        ignoreLevel);

                var items = new List<QuestRewardItem>();
                uint gold = 0;
                if (chainType == 0)
                {
                    var fixedRewards = QuestData.ParseItemPairs(
                        quest.RewardIntData,
                        playerJob,
                        playerGrowType,
                        preserveGoldMarker: true);
                    var hasGoldMarker = false;
                    foreach (var reward in fixedRewards)
                    {
                        if (reward.ItemId == 0)
                            hasGoldMarker = true;
                        else
                            items.Add(reward);
                    }

                    if (hasGoldMarker || quest.GoldMultiple > 0)
                    {
                        gold = Parameters.Value.ComputeGoldReward(
                            playerLevel,
                            questMinLevel,
                            quest.GoldMultiple,
                            ignoreLevel);
                    }

                    if (rewardSelectIdx >= 0)
                    {
                        var selectable = QuestData.ParseItemPairs(
                            quest.RewardSelectionIntData,
                            playerJob,
                            playerGrowType);
                        if (!string.IsNullOrWhiteSpace(
                                quest.RewardSelectionIntData)
                            && rewardSelectIdx >= selectable.Count)
                        {
                            return QuestRewardResolution.Invalid(
                                empty,
                                $"reward selection index {rewardSelectIdx} " +
                                $"is outside {selectable.Count} entries");
                        }
                        if (rewardSelectIdx < selectable.Count)
                            items.Add(selectable[rewardSelectIdx]);
                    }
                }

                var growNumber = 0;
                if (RequiresIntegerParameter(chainType))
                {
                    var rewardValues = QuestData.ParseIntList(
                        quest.RewardIntData);
                    if (rewardValues.Count == 0)
                    {
                        return QuestRewardResolution.Invalid(
                            empty,
                            $"reward type {quest.RewardType} " +
                            "requires an integer parameter");
                    }
                    growNumber = rewardValues[0];
                }

                return QuestRewardResolution.Valid(
                    new QuestReward
                    {
                        Exp = exp,
                        Gold = gold,
                        ChainType = chainType,
                        GrowNumber = growNumber,
                        CreatureKind = quest.CreatureKind,
                        CreatureLevel = quest.CreatureLevel,
                        Items = items,
                        ConsumeItems = new List<QuestRewardItem>(),
                    });
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestRewardProjector] reward calc failed: " +
                    $"quest={questId}: {ex.Message}");
                return QuestRewardResolution.Invalid(empty, ex.Message);
            }
        }

        private static QuestReward CreateEmptyReward()
            => new QuestReward
            {
                Exp = 0,
                Gold = 0,
                ChainType = 0,
                Items = new List<QuestRewardItem>(),
                ConsumeItems = new List<QuestRewardItem>(),
            };

        private static bool RequiresIntegerParameter(int chainType)
            => chainType == 1
                || chainType == 2
                || chainType == 10
                || chainType == 20
                || chainType == 25
                || chainType == ChainTypeSlotExpansion;

        private static int MapRewardType(string rewardType)
        {
            switch ((rewardType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "[grow type]": return 1;
                case "[awakening type]": return 2;
                case "[creature evolution]": return 10;
                case "[expert job]": return 20;
                case "[slot expansion]": return ChainTypeSlotExpansion;
                case "[event creature evolution]": return 25;
                default: return 0;
            }
        }

        private static QuestParameterTable LoadParameters()
        {
            try
            {
                return QuestParameterTable.Parse(
                    PvfArchiveAccessor.ReadText("n_Quest/questParameter.etc"));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestRewardProjector] Failed to load " +
                    $"questParameter.etc: {ex.Message}");
                return new QuestParameterTable();
            }
        }
    }

    internal sealed class QuestParameterTable
    {
        private Dictionary<char, int> _difficultyWeight =
            new Dictionary<char, int>();
        private int[] _expTable = Array.Empty<int>();
        private int[] _goldTable = Array.Empty<int>();
        private int _greenPenalty = 80;
        private int _greyPenalty = 30;

        internal uint ComputeExp(
            int playerLevel,
            int questMinLevel,
            char difficulty,
            bool ignoreLevel)
        {
            var levelDiff = playerLevel - questMinLevel;
            var penalty = ignoreLevel ? 100 : ComputeLevelPenalty(levelDiff);
            var lookupLevel = ignoreLevel ? playerLevel : questMinLevel;
            var baseExp = lookupLevel >= 1 && lookupLevel <= _expTable.Length
                ? _expTable[lookupLevel - 1]
                : 0;
            if (!_difficultyWeight.TryGetValue(
                    char.ToUpperInvariant(difficulty),
                    out var weight))
            {
                weight = 10;
            }

            return (uint)(penalty * ((long)weight * baseExp / 100) / 100);
        }

        internal uint ComputeGoldReward(
            int playerLevel,
            int questMinLevel,
            int goldMultiple,
            bool ignoreLevel)
        {
            if (goldMultiple <= 0)
                goldMultiple = 100;
            var levelDiff = playerLevel - questMinLevel;
            var penalty = ignoreLevel ? 100 : ComputeLevelPenalty(levelDiff);
            var lookupIndex = ignoreLevel ? playerLevel - 1 : questMinLevel;
            var baseGold = lookupIndex >= 0 && lookupIndex < _goldTable.Length
                ? _goldTable[lookupIndex]
                : 0;
            return (uint)(goldMultiple * ((long)penalty * baseGold / 100) / 100);
        }

        private int ComputeLevelPenalty(int levelDiff)
        {
            if (levelDiff > 6 && levelDiff <= 11)
                return _greenPenalty;
            if (levelDiff <= 11)
                return 100;
            return _greyPenalty;
        }

        internal static QuestParameterTable Parse(string content)
        {
            var table = new QuestParameterTable();
            if (string.IsNullOrEmpty(content))
                return table;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            string section = null;
            var expValues = new List<int>();
            var goldValues = new List<int>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (line == "[difficulty]")
                    {
                        section = "diff";
                        continue;
                    }
                    if (line == "[exp reward table]")
                    {
                        section = "exp";
                        continue;
                    }
                    if (line == "[gold reward table]")
                    {
                        section = "gold";
                        continue;
                    }
                    if (line.StartsWith("[green level penalty]"))
                    {
                        section = "green";
                        continue;
                    }
                    if (line.StartsWith("[grey level penalty]"))
                    {
                        section = "grey";
                        continue;
                    }

                    section = null;
                    continue;
                }

                if (section == "green" && line.Length > 0)
                {
                    if (int.TryParse(line.Split(' ')[0], out var value))
                        table._greenPenalty = value;
                    section = null;
                }
                else if (section == "grey" && line.Length > 0)
                {
                    if (int.TryParse(line.Split(' ')[0], out var value))
                        table._greyPenalty = value;
                    section = null;
                }
                else if (section == "diff" && line.Length > 0)
                {
                    var tokens = line.Split(
                        new[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    for (var index = 0; index + 1 < tokens.Length; index += 2)
                    {
                        var key = tokens[index].Trim('`');
                        if (key.Length == 1
                            && int.TryParse(tokens[index + 1], out var value))
                        {
                            table._difficultyWeight[key[0]] = value;
                        }
                    }
                }
                else if (section == "exp" && line.Length > 0)
                {
                    AppendIntegers(line, expValues, requireNonNegative: true);
                }
                else if (section == "gold" && line.Length > 0)
                {
                    AppendIntegers(line, goldValues, requireNonNegative: false);
                }
            }

            table._expTable = expValues.ToArray();
            table._goldTable = goldValues.ToArray();
            return table;
        }

        private static void AppendIntegers(
            string line,
            ICollection<int> output,
            bool requireNonNegative)
        {
            foreach (var token in line.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var value)
                    && (!requireNonNegative || value >= 0))
                {
                    output.Add(value);
                }
            }
        }
    }
}
