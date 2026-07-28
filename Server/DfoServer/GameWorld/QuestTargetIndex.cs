using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal sealed class HuntMonsterQuestTarget
    {
        public int QuestId;
        public int DungeonId;
        public int MinimumDifficulty;
        public int MonsterCode;
        public int RequiredCount;
        public int ChannelIndex;
    }

    internal sealed class DungeonQuestActorTarget
    {
        public int QuestId;
        public int DungeonId;
        public int MapId;
        public int ActorCode;
        public string Source;
    }

    internal sealed class DungeonNpcItemDropQuestTarget
    {
        public int QuestId;
        public int DungeonId;
        public int Difficulty;
        public List<int> ItemIds = new List<int>();
    }

    internal static class QuestTargetIndex
    {
        internal static List<HuntMonsterQuestTarget> GetHuntMonsterTargets(
            int questId)
        {
            var result = new List<HuntMonsterQuestTarget>();
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type) != "hunt monster")
            {
                return result;
            }

            var values = QuestData.ParseIntList(quest.IntData);
            const int stride = 4;
            for (var offset = 0;
                offset + stride <= values.Count;
                offset += stride)
            {
                var dungeonId = values[offset];
                var minimumDifficulty = values[offset + 1];
                var monsterCode = values[offset + 2];
                var requiredCount = values[offset + 3];
                if ((dungeonId <= 0 && dungeonId != -1)
                    || monsterCode <= 0
                    || requiredCount <= 0)
                {
                    continue;
                }

                result.Add(new HuntMonsterQuestTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MinimumDifficulty = minimumDifficulty,
                    MonsterCode = monsterCode,
                    RequiredCount = requiredCount,
                    ChannelIndex = offset / stride,
                });
            }

            return result;
        }

        internal static List<DungeonQuestActorTarget>
            GetUnfinishedDungeonActorTargets(
                int questId,
                uint trigger,
                int dungeonId,
                int difficulty)
        {
            var result = new List<DungeonQuestActorTarget>();
            if (questId <= 0 || trigger == 0 || dungeonId <= 0)
                return result;

            var seen = new HashSet<(int MapId, int ActorCode)>();
            foreach (var target in GetHuntMonsterTargets(questId))
            {
                if (!MatchesHuntMonsterTarget(
                        target,
                        dungeonId,
                        difficulty,
                        target.MonsterCode)
                    || QuestData.GetTriggerChannel(trigger, target.ChannelIndex) <= 0
                    || target.MonsterCode <= 0
                    || !seen.Add((-1, target.MonsterCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = target.MonsterCode,
                    Source = "hunt monster",
                });
            }

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
                return result;

            foreach (var entry in quest.MonsterRewardItems)
            {
                if (entry.MonsterCode <= 0
                    || !MatchesDungeonScope(
                        entry.DungeonId,
                        entry.Difficulty,
                        dungeonId,
                        difficulty)
                    || !seen.Add((-1, entry.MonsterCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = entry.MonsterCode,
                    Source = "monster reward item",
                });
            }

            foreach (var entry in quest.EnemyRewardItems)
            {
                if (entry.EnemyCode <= 0
                    || !MatchesDungeonScope(
                        entry.DungeonId,
                        entry.Difficulty,
                        dungeonId,
                        difficulty)
                    || !seen.Add((-1, entry.EnemyCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = entry.EnemyCode,
                    Source = "enemy reward item",
                });
            }

            return result;
        }

        internal static bool TryGetNpcItemDropQuestTarget(
            int questId,
            int dungeonId,
            int difficulty,
            out DungeonNpcItemDropQuestTarget target)
        {
            target = null;
            if (questId <= 0 || dungeonId <= 0)
                return false;

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type)
                    != "get item check index")
            {
                return false;
            }

            var dungeonValues = QuestData.ParseIntList(quest.DungeonInfo);
            var matched = false;
            var matchedDungeon = -1;
            var matchedDifficulty = -1;
            for (var offset = 0; offset + 1 < dungeonValues.Count; offset += 2)
            {
                var configuredDungeon = dungeonValues[offset];
                var configuredDifficulty = dungeonValues[offset + 1];
                if (configuredDungeon != -1 && configuredDungeon != dungeonId)
                    continue;
                if (configuredDifficulty != -1
                    && configuredDifficulty != difficulty)
                {
                    continue;
                }

                matched = true;
                matchedDungeon = configuredDungeon;
                matchedDifficulty = configuredDifficulty;
                break;
            }

            if (!matched)
                return false;

            target = new DungeonNpcItemDropQuestTarget
            {
                QuestId = questId,
                DungeonId = matchedDungeon,
                Difficulty = matchedDifficulty,
            };

            var uniqueItemIds = new HashSet<int>();
            foreach (var itemId in QuestData.ParseIntList(quest.IntData))
            {
                if (itemId > 0 && uniqueItemIds.Add(itemId))
                    target.ItemIds.Add(itemId);
            }

            return target.ItemIds.Count > 0;
        }

        internal static bool MatchesHuntMonsterTarget(
            HuntMonsterQuestTarget target,
            int dungeonId,
            int difficulty,
            int monsterCode)
        {
            if (target == null
                || monsterCode <= 0
                || target.MonsterCode != monsterCode)
            {
                return false;
            }

            if (target.DungeonId != -1
                && target.DungeonId != dungeonId)
            {
                return false;
            }

            return target.MinimumDifficulty < 0
                || difficulty < 0
                || difficulty >= target.MinimumDifficulty;
        }

        internal static bool ReferencesDungeon(int questId, int dungeonId)
        {
            if (questId <= 0 || dungeonId <= 0)
                return false;

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
                return false;

            var values = QuestData.ParseIntList(quest.DungeonInfo);
            for (var offset = 0; offset + 1 < values.Count; offset += 2)
            {
                if (values[offset] == dungeonId)
                    return true;
            }

            return false;
        }

        internal static bool IsClearMapQuest(int questId)
            => IsClearMapQuest(QuestData.GetQuestFile(questId));

        internal static bool MatchesClearMapTarget(
            int questId,
            int dungeonId,
            int mapId)
            => MatchesClearMapTarget(
                QuestData.GetQuestFile(questId),
                dungeonId,
                mapId);

        internal static bool MatchesClearMapTarget(
            QuestFile quest,
            int dungeonId,
            int mapId)
            => IsClearMapQuest(quest)
                && MatchesClearMapTargetData(quest.IntData, dungeonId, mapId);

        internal static bool MatchesClearMapTargetData(
            string intData,
            int dungeonId,
            int mapId)
        {
            foreach (var target in QuestData.ParseIntList(intData))
            {
                if (target <= 0)
                    continue;
                if (dungeonId > 0 && target == dungeonId)
                    return true;
                if (mapId > 0 && target == mapId)
                    return true;
            }

            return false;
        }

        internal static List<QuestRewardItem> GetSeekingConsumeItems(int questId)
        {
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null || QuestData.IsQuestClearQuest(questId))
                return new List<QuestRewardItem>();

            if (QuestData.IsSeekAndMeetNpcQuest(questId))
                return QuestData.ParseSeekAndMeetNpcItems(quest.IntData);

            if (QuestData.NormalizeQuestTag(quest.Type) != "seeking")
                return new List<QuestRewardItem>();

            var items = QuestData.ParseItemPairs(quest.IntData);
            items.RemoveAll(item => item.ItemId <= 0 || item.Count <= 0);
            return items;
        }

        private static bool MatchesDungeonScope(
            int configuredDungeonId,
            int configuredDifficulty,
            int dungeonId,
            int difficulty)
        {
            if (configuredDungeonId != -1
                && configuredDungeonId != dungeonId)
            {
                return false;
            }

            return configuredDifficulty < 0
                || difficulty < 0
                || configuredDifficulty == difficulty;
        }

        private static bool IsClearMapQuest(QuestFile quest)
            => quest != null
                && QuestData.NormalizeQuestTag(quest.Type) == "clear map";
    }
}
