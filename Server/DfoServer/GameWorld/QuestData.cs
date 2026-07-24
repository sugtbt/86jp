using PvfLib;
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

    internal struct QuestRewardItem
    {
        public int ItemId;
        public int Count;
    }

    internal sealed class HuntMonsterQuestTarget
    {
        public int QuestId;
        public int DungeonId;
        public int MapId;
        public int MonsterCode;
        public int RequiredCount;
        public int ChannelIndex;
    }

    internal static class QuestData
    {
        // PVF [slot expansion] rewards use reward int data as an equipment slot id, not an item id.
        internal const int ChainTypeSlotExpansion = 21;

        private static readonly Lazy<QuestIndex> Index = new Lazy<QuestIndex>(BuildQuestIndex);
        private static readonly Lazy<QuestParameterTable> Parameters = new Lazy<QuestParameterTable>(LoadParameters);
        private static readonly Lazy<Dictionary<int, HashSet<int>>> TrainingQuestNpcs = new Lazy<Dictionary<int, HashSet<int>>>(LoadTrainingQuestNpcs);
        private static readonly Lazy<Dictionary<int, int>> QuestionAnswerCounts = new Lazy<Dictionary<int, int>>(BuildQuestionAnswerCounts);
        private static readonly Dictionary<int, QuestFile> _qstCache = new Dictionary<int, QuestFile>();
        private static readonly object _cacheLock = new object();

        private sealed class QuestIndex
        {
            public Dictionary<int, string> Paths;
            public List<int> OrderedIds;
        }

        internal static QuestFile GetQuestFile(int questId)
        {
            lock (_cacheLock)
            {
                QuestFile cached;
                if (_qstCache.TryGetValue(questId, out cached)) return cached;
            }
            string path;
            if (!Index.Value.Paths.TryGetValue(questId, out path)) return null;
            try
            {
                var qst = QuestFile.Parse(PvfArchiveAccessor.ReadText(path));
                lock (_cacheLock) { _qstCache[questId] = qst; }
                return qst;
            }
            catch (Exception ex)
            {
                // 解析失败的任务会从可接列表和奖励结算里凭空消失, 是最难排查的一类问题 -- 必须大声。
                FileLogger.Log($"[QuestData] ERROR: quest {questId} parse failed, quest will be MISSING: path={path}: {ex.Message}");
                return null;
            }
        }

        private static QuestParameterTable LoadParameters()
        {
            try
            {
                var content = PvfArchiveAccessor.ReadText("n_Quest/questParameter.etc");
                return QuestParameterTable.Parse(content);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestData] Failed to load questParameter.etc: {ex.Message}");
                return new QuestParameterTable();
            }
        }

        private static Dictionary<int, HashSet<int>> LoadTrainingQuestNpcs()
        {
            var result = new Dictionary<int, HashSet<int>>();
            try
            {
                var text = PvfArchiveAccessor.ReadText("n_Quest/TrainingQuest.lst");
                if (string.IsNullOrEmpty(text)) return result;

                int currentLevel = -1;
                foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    if (line == "[level]") { currentLevel = -1; continue; }
                    if (line == "[/level]") { currentLevel = -1; continue; }
                    if (line.StartsWith("[")) continue;
                    if (currentLevel < 0)
                    {
                        int lv;
                        if (int.TryParse(line, out lv) && lv >= 1 && lv <= 70)
                            currentLevel = lv;
                        continue;
                    }
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var t in tokens)
                    {
                        int npc;
                        if (int.TryParse(t, out npc) && npc > 0)
                        {
                            HashSet<int> set;
                            if (!result.TryGetValue(currentLevel, out set))
                            {
                                set = new HashSet<int>();
                                result[currentLevel] = set;
                            }
                            set.Add(npc);
                        }
                    }
                }
                FileLogger.Log($"[QuestData] TrainingQuest: {result.Count} levels with NPC entries");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestData] Failed to load TrainingQuest.lst: {ex.Message}");
            }
            return result;
        }

        private static Dictionary<int, int> BuildQuestionAnswerCounts()
        {
            var result = new Dictionary<int, int>();
            foreach (var questId in Index.Value.OrderedIds)
            {
                var qst = GetQuestFile(questId);
                if (qst == null)
                    continue;

                var preReqAns = ParseIntList(qst.PreRequiredQuestAnswer);
                for (int i = 0; i + 1 < preReqAns.Count; i += 2)
                {
                    int questionQuestId = preReqAns[i];
                    int answerIndex = preReqAns[i + 1];
                    if (questionQuestId <= 0 || answerIndex < 0)
                        continue;

                    int nextCount = answerIndex + 1;
                    int currentCount;
                    if (!result.TryGetValue(questionQuestId, out currentCount) || nextCount > currentCount)
                        result[questionQuestId] = nextCount;
                }
            }

            FileLogger.Log($"[QuestData] QuestionAnswerCounts: {result.Count} question quests with answer-dependent successors");
            return result;
        }

        internal static bool IsThereDailyTrainingQuestList(int level, int npcIndex)
        {
            if (level <= 0 || level > 70) return false;
            HashSet<int> npcs;
            if (!TrainingQuestNpcs.Value.TryGetValue(level, out npcs)) return false;
            return npcs.Contains(npcIndex);
        }

        private static QuestIndex BuildQuestIndex()
        {
            var idx = new QuestIndex
            {
                Paths = new Dictionary<int, string>(),
                OrderedIds = new List<int>()
            };
            try
            {
                var content = PvfArchiveAccessor.ReadText("n_quest/quest.lst");
                var lst = LstFile.Parse(content);
                foreach (var entry in lst.Entries)
                {
                    if (!idx.Paths.ContainsKey(entry.Id))
                    {
                        idx.Paths[entry.Id] = "n_quest/" + entry.FilePath;
                        idx.OrderedIds.Add(entry.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestData] Failed to load quest.lst: {ex.Message}");
            }
            return idx;
        }

        public static bool IsRepeatableQuest(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return false;
            var grade = (qst.Grade ?? "").Trim().ToLowerInvariant();
            return grade == "[daily]" || grade == "[normaly repeat]" || grade == "[special daily]";
        }

        public static bool IsTitleRewardQuest(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return false;
            return NormalizeQuestTag(qst.RewardType) == "title";
        }

        // The client rebuilds these native character effects from the completed
        // quest id and the QST's [special reward status] block.
        internal static bool HasSpecialRewardStatus(int questId)
        {
            if (questId <= 0)
                return false;

            var qst = GetQuestFile(questId);
            return qst != null && qst.HasTag("special reward status");
        }

        public static bool CanGiveup(int questId)
        {
            var qst = GetQuestFile(questId);
            return qst == null || !qst.CantGiveup;
        }

        public static List<int> GetPreRequiredQuests(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return new List<int>();
            var values = ParseIntList(qst.PreRequiredQuest);
            return values;
        }

        internal static bool IsQuestionQuest(int questId)
        {
            return IsQuestionQuest(GetQuestFile(questId));
        }

        internal static int GetQuestionAnswerCount(int questId)
        {
            int count;
            return QuestionAnswerCounts.Value.TryGetValue(questId, out count) ? count : 0;
        }

        internal static int GetRequiredQuestAnswerFlagValue(int answerIndex)
        {
            return answerIndex >= 0 ? answerIndex + 1 : 0;
        }

        internal static bool DoesClearedFlagMatchRequiredQuestAnswer(
            Dictionary<int, int> clearedFlags,
            int requiredQuestId,
            int requiredAnswerIndex)
        {
            if (requiredQuestId <= 0)
                return true;

            int requiredFlag = GetRequiredQuestAnswerFlagValue(requiredAnswerIndex);
            if (requiredFlag <= 0 || clearedFlags == null)
                return false;

            int flagValue;
            return clearedFlags.TryGetValue(requiredQuestId, out flagValue)
                && flagValue == requiredFlag;
        }

        public static List<ushort> ComputeAcceptableQuests(int characterLevel, int characterJob, int growType, HashSet<int> clearedQuestIds, Dictionary<int, int> clearedFlags)
            => ComputeAcceptableQuests(characterLevel, characterJob, growType, clearedQuestIds, clearedFlags, null);

        public static List<ushort> ComputeAcceptableQuests(
            int characterLevel,
            int characterJob,
            int growType,
            HashSet<int> clearedQuestIds,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
        {
            var result = new List<ushort>();
            foreach (var questId in Index.Value.OrderedIds)
            {
                if (questId <= 0 || questId > 29999) continue;


                var qst = GetQuestFile(questId);
                if (qst == null) continue;

                var exposedVal = ParseExposedValue(qst.ExposedByNpc);
                if (exposedVal == 0) continue;

                if (qst.IsEvent) continue;

                if (qst.CreatureKind >= 0
                    && (allowedCreatureKinds == null || !allowedCreatureKinds.Contains(qst.CreatureKind)))
                    continue;

                if (qst.ExpertJobType >= 0 && qst.ExpertJobLevel >= 0)
                    continue;

                var grade = (qst.Grade ?? "").Trim().ToLowerInvariant();
                if (grade == "[training]")
                {
                    if (!IsThereDailyTrainingQuestList(characterLevel, qst.NpcIndex))
                        continue;
                }

                if (!IsSelectableGrade(grade)) continue;

                var tc = (qst.TargetCharacter ?? "").Trim().ToLowerInvariant();
                if (tc.Length > 0 && !MatchesTargetCharacter(tc, characterJob))
                    continue;

                // check_possible: level range
                int minLv = (qst.Level != null && qst.Level.Length > 0) ? qst.Level[0] : 1;
                int maxLv = (qst.Level != null && qst.Level.Length > 1) ? qst.Level[1] : 99;
                if (characterLevel < minLv || characterLevel > maxLv) continue;

                // check_possible: job
                var jobStr = (qst.Job ?? "").Trim().ToLowerInvariant();
                if (jobStr.Length > 0 && jobStr != "[all]" && !MatchesJob(jobStr, characterJob))
                    continue;

                // check_possible: growType / jobChangeQuest
                // jcq=1: 一转相关(转职任务+转职贺礼), 不过滤growType, 靠pre_required_quest控制顺序
                // jcq=2/3: 觉醒/二次觉醒任务, 需 firstGrow 匹配 [grow type]
                // jcq=10/20: 跳过 growType 检查
                int jcq = qst.JobChangeQuestValue;
                if (jcq == 2 || jcq == 3)
                {
                    int firstGrow = growType & 0xF;
                    if (qst.GrowType != -1 && qst.GrowType != firstGrow) continue;
                }
                else if (qst.GrowType != -1 && jcq != 1 && jcq != 10 && jcq != 20 && growType >= 0)
                {
                    if (!MatchesGrowType(qst.GrowType, characterJob, growType)) continue;
                }

                bool repeatable = grade == "[daily]" || grade == "[normaly repeat]" ||
                                  grade == "[special daily]";
                if (!repeatable && clearedQuestIds.Contains(questId)) continue;

                bool preOk;
                if (qst.PreRequiredQuestGroups != null && qst.PreRequiredQuestGroups.Count > 0)
                {
                    preOk = false;
                    foreach (var group in qst.PreRequiredQuestGroups)
                    {
                        var ids = ParseIntList(group);
                        bool groupOk = true;
                        foreach (var pq in ids)
                        {
                            if (pq > 0 && !clearedQuestIds.Contains(pq)) { groupOk = false; break; }
                        }
                        if (groupOk) { preOk = true; break; }
                    }
                }
                else
                {
                    preOk = true;
                }
                if (!preOk) continue;

                var preReqAns = ParseIntList(qst.PreRequiredQuestAnswer);
                bool preAnsOk = true;
                for (int pi = 0; pi + 1 < preReqAns.Count; pi += 2)
                {
                    int reqQid = preReqAns[pi];
                    int reqAnswer = preReqAns[pi + 1];
                    if (!DoesClearedFlagMatchRequiredQuestAnswer(clearedFlags, reqQid, reqAnswer))
                    { preAnsOk = false; break; }
                }
                if (!preAnsOk) continue;

                var collisions = ParseIntList(qst.CollisionQuest);
                bool colOk = true;
                foreach (var cq in collisions)
                {
                    if (cq > 0 && clearedQuestIds.Contains(cq)) { colOk = false; break; }
                }
                if (!colOk) continue;

                result.Add((ushort)questId);
            }
            FileLogger.Log($"[QuestData] ComputeAcceptableQuests: {result.Count} quests for job={characterJob} lv={characterLevel} grow={growType}");
            var sb = new System.Text.StringBuilder();
            foreach (var qid in result) sb.Append(qid).Append(',');
            FileLogger.Log($"[QuestData] IDs: {sb}");
            return result;
        }


        private static int GetGradeBucket(QuestFile qst)
        {
            if (qst == null) return 99;
            var g = (qst.Grade ?? "").Trim().ToLowerInvariant();
            switch (g)
            {
                case "[achievement]": return 0;
                case "": case "[normal]": return 1;
                case "[epic]": return 2;
                case "[side]": case "[sub]": return 3;
                case "[common unique]": return 4;
                case "[normaly repeat]": return 5;
                case "[training]": return 6;
                case "[daily]": case "[daily random]":
                case "[special daily]": return 7;
                default: return g.StartsWith("[special daily]") || g.StartsWith("[daily]") ? 7 : 99;
            }
        }

        private static bool IsSelectableGrade(string grade)
        {
            return grade == "" || grade == "[normal]" || grade == "[side]" || grade == "[sub]" ||
                   grade == "[epic]" || grade == "[training]" || grade == "[achievement]" ||
                   grade == "[daily]" || grade == "[daily random]" ||
                   grade == "[normaly repeat]" || grade == "[special daily]" ||
                   grade == "[common unique]" || grade == "[system]";
        }

        private static bool IsQuestExposed(QuestFile qst)
        {
            int exposed = ParseExposedValue(qst.ExposedByNpc);
            if (exposed >= 1) return true;
            int firstExposed = ParseExposedValue(qst.FirstExposedByNpc);
            if (firstExposed >= 1) return true;
            return false;
        }

        private static int ParseExposedValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return -1;
            int result;
            return int.TryParse(val.Trim(), out result) ? result : -1;
        }

        private static bool MatchesTargetCharacter(string tc, int characterJob)
        {
            string[] jobNames = { "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[thief]", "[knight]" };
            string[] atJobNames = { "[at swordman]", "[at fighter]", "[at gunner]", "[at mage]", "[at priest]", "[at thief]", "[at knight]" };
            int baseIdx = GetBaseJobIndex(characterJob);
            if (baseIdx < 0 || baseIdx >= jobNames.Length) return false;
            bool isAt = IsAtVariant(characterJob);
            if (isAt)
                return tc.Contains(atJobNames[baseIdx]);
            return tc.Contains(jobNames[baseIdx]);
        }

        private static bool MatchesGrowType(int questGrowType, int characterJob, int characterGrowType)
        {
            // growType in PVF: encodes job+specialization
            if (questGrowType == -1) return true;
            return questGrowType == characterGrowType;
        }

        private static int GetBaseJobIndex(int characterJob)
        {
            // PVF character.lst: 0=Swordman 1=Fighter 2=Gunner 3=Mage 4=Priest
            //   5=ATGunner 6=Thief 7=ATFighter 8=ATMage 9=DSSwordman
            //   10=CreatorMage 11=ATSwordman 12=Knight
            switch (characterJob)
            {
                case 0: case 9: case 11: return 0;  // swordman / ds swordman / at swordman
                case 1: case 7: return 1;            // fighter / at fighter
                case 2: case 5: return 2;            // gunner / at gunner
                case 3: case 8: case 10: return 3;   // mage / at mage / creator mage
                case 4: return 4;                     // priest
                case 6: return 5;                     // thief
                case 12: return 6;                    // knight
                default: return -1;
            }
        }

        private static bool IsAtVariant(int characterJob)
        {
            return characterJob == 5 || characterJob == 7 || characterJob == 8 ||
                   characterJob == 9 || characterJob == 10 || characterJob == 11;
        }

        private static bool MatchesJob(string jobStr, int characterJob)
        {
            string[] jobNames = { "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[thief]", "[knight]" };
            string[] atJobNames = { "[at swordman]", "[at fighter]", "[at gunner]", "[at mage]", "[at priest]", "[at thief]", "[at knight]" };
            int baseIdx = GetBaseJobIndex(characterJob);
            if (baseIdx < 0 || baseIdx >= jobNames.Length) return false;
            bool isAt = IsAtVariant(characterJob);
            if (isAt)
                return jobStr.Contains(atJobNames[baseIdx]);
            return jobStr.Contains(jobNames[baseIdx]);
        }

        public static List<int> GetCollisionQuests(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return new List<int>();
            return ParseIntList(qst.CollisionQuest);
        }

        internal static List<HuntMonsterQuestTarget> GetHuntMonsterTargets(
            int questId)
        {
            var result = new List<HuntMonsterQuestTarget>();
            var qst = GetQuestFile(questId);
            if (qst == null
                || NormalizeQuestTag(qst.Type) != "hunt monster")
            {
                return result;
            }

            var values = ParseIntList(qst.IntData);
            const int stride = 4;
            for (var offset = 0;
                offset + stride <= values.Count;
                offset += stride)
            {
                var dungeonId = values[offset];
                var mapId = values[offset + 1];
                var monsterCode = values[offset + 2];
                var requiredCount = values[offset + 3];
                if (dungeonId <= 0
                    || monsterCode <= 0
                    || requiredCount <= 0)
                {
                    continue;
                }

                result.Add(new HuntMonsterQuestTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = mapId,
                    MonsterCode = monsterCode,
                    RequiredCount = requiredCount,
                    ChannelIndex = offset / stride,
                });
            }

            return result;
        }

        public static uint GetInitTrigger(int questId)
        {
            var qst = GetQuestFile(questId);
            return qst != null ? ComputeInitTrigger(qst) : 1;
        }

        internal static bool IsQuestClearQuest(int questId)
        {
            return IsQuestClearQuest(GetQuestFile(questId));
        }

        internal static List<int> GetQuestClearRequiredQuestIds(int questId)
        {
            var qst = GetQuestFile(questId);
            if (!IsQuestClearQuest(qst))
                return new List<int>();

            var values = ParseIntList(qst.IntData);
            values.RemoveAll(id => id <= 0);
            return values;
        }

        internal static bool IsClearMapQuest(int questId)
        {
            return IsClearMapQuest(GetQuestFile(questId));
        }

        internal static bool MatchesClearMapTarget(int questId, int dungeonId, int mapId)
        {
            return MatchesClearMapTarget(GetQuestFile(questId), dungeonId, mapId);
        }

        internal static bool MatchesClearMapTarget(QuestFile qst, int dungeonId, int mapId)
        {
            return IsClearMapQuest(qst) && MatchesClearMapTargetData(qst.IntData, dungeonId, mapId);
        }

        internal static bool MatchesClearMapTargetData(string intData, int dungeonId, int mapId)
        {
            var values = ParseIntList(intData);
            for (int i = 0; i < values.Count; i++)
            {
                var target = values[i];
                if (target <= 0)
                    continue;
                if (dungeonId > 0 && target == dungeonId)
                    return true;
                if (mapId > 0 && target == mapId)
                    return true;
            }
            return false;
        }

        private static bool IsClearMapQuest(QuestFile qst)
        {
            return qst != null && NormalizeQuestTag(qst.Type) == "clear map";
        }

        private static bool IsQuestClearQuest(QuestFile qst)
        {
            var tag = NormalizeQuestTag(qst?.Type);
            return tag == "quest clear" || tag == "clear quest";
        }

        private static bool IsQuestionQuest(QuestFile qst)
        {
            return NormalizeQuestTag(qst?.Type) == "question";
        }

        private static string NormalizeQuestTag(string value)
        {
            var tag = (value ?? "").Trim().ToLowerInvariant();
            if (tag.Length >= 2 && tag[0] == '[' && tag[tag.Length - 1] == ']')
                tag = tag.Substring(1, tag.Length - 2).Trim();
            return tag;
        }

        public static List<QuestRewardItem> GetEventItems(int questId)
        {
            var qst = GetQuestFile(questId);
            return qst != null ? ParseItemPairs(qst.DependGiveItem) : new List<QuestRewardItem>();
        }

        public static List<QuestRewardItem> GetCarryForwardEventItems(int questId)
        {
            var eventItems = GetEventItems(questId);
            if (eventItems.Count == 0)
                return new List<QuestRewardItem>();

            var carryForward = new Dictionary<int, int>();
            var eventItemIds = new HashSet<int>();
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId > 0 && eventItem.Count > 0)
                    eventItemIds.Add(eventItem.ItemId);
            }

            if (eventItemIds.Count == 0)
                return new List<QuestRewardItem>();

            // Some [depend give item] entries seed the next quest instead of ending with this one.
            // Keep only items required by a direct follow-up quest that does not give the item again.
            foreach (var nextQuestId in Index.Value.OrderedIds)
            {
                if (nextQuestId == questId)
                    continue;

                var nextQuest = GetQuestFile(nextQuestId);
                if (nextQuest == null)
                    continue;

                var preRequired = ParseIntList(nextQuest.PreRequiredQuest);
                if (!preRequired.Contains(questId))
                    continue;

                var nextSeekItems = GetSeekingConsumeItems(nextQuestId);
                if (nextSeekItems.Count == 0)
                    continue;

                var nextEventItems = GetEventItems(nextQuestId);
                foreach (var eventItem in eventItems)
                {
                    if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                        continue;

                    bool nextNeedsItem = false;
                    foreach (var nextSeekItem in nextSeekItems)
                    {
                        if (nextSeekItem.ItemId == eventItem.ItemId && nextSeekItem.Count > 0)
                        {
                            nextNeedsItem = true;
                            break;
                        }
                    }

                    if (!nextNeedsItem)
                        continue;

                    bool nextGivesItem = false;
                    foreach (var nextEventItem in nextEventItems)
                    {
                        if (nextEventItem.ItemId == eventItem.ItemId && nextEventItem.Count > 0)
                        {
                            nextGivesItem = true;
                            break;
                        }
                    }

                    if (nextGivesItem)
                        continue;

                    int currentCount;
                    if (!carryForward.TryGetValue(eventItem.ItemId, out currentCount) || currentCount < eventItem.Count)
                        carryForward[eventItem.ItemId] = eventItem.Count;
                }
            }

            var result = new List<QuestRewardItem>();
            foreach (var pair in carryForward)
                result.Add(new QuestRewardItem { ItemId = pair.Key, Count = pair.Value });
            return result;
        }

        public static List<QuestRewardItem> GetSeekingConsumeItems(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return new List<QuestRewardItem>();
            if (IsQuestClearQuest(qst)) return new List<QuestRewardItem>();

            if (IsSeekAndMeetNpcQuest(qst))
                return ParseSeekAndMeetNpcItems(qst.IntData);

            if (NormalizeQuestTag(qst.Type) != "seeking")
                return new List<QuestRewardItem>();

            var items = ParseItemPairs(qst.IntData);
            items.RemoveAll(item => item.ItemId <= 0 || item.Count <= 0);
            return items;
        }


        public static QuestReward GetRewardExp(int questId, int rewardSelectIdx = -1, int playerLevel = 1, int playerJob = -1, int playerGrowType = -1)
        {
            var empty = new QuestReward { Exp = 0, Gold = 0, ChainType = 0, Items = new List<QuestRewardItem>(), ConsumeItems = new List<QuestRewardItem>() };
            var qst = GetQuestFile(questId);
            if (qst == null) return empty;

            try
            {
                int chainType = MapRewardType(qst.RewardType);

                var param = Parameters.Value;
                int questMinLevel = (qst.Level != null && qst.Level.Length > 0) ? qst.Level[0] : 1;
                char difficulty = (qst.Difficulty != null && qst.Difficulty.Length > 0) ? qst.Difficulty[0] : 'G';
                bool ignoreLevel = qst.IgnoreQuestLevel4Exp;
                bool isRepeatable = (qst.Grade ?? "").Trim().ToLowerInvariant() == "[normaly repeat]"
                                 || MapTypeString(qst.Type) == 4;

                uint exp = 0;
                if (!isRepeatable)
                    exp = param.ComputeExp(playerLevel, questMinLevel, difficulty, ignoreLevel);

                var items = new List<QuestRewardItem>();
                uint gold = 0;
                if (chainType == 0)
                {
                    var fixedRewards = ParseItemPairs(
                        qst.RewardIntData, playerJob, playerGrowType, preserveGoldMarker: true);
                    bool hasGoldMarker = false;
                    foreach (var fr in fixedRewards)
                    {
                        if (fr.ItemId == 0)
                        {
                            hasGoldMarker = true;
                        }
                        else
                        {
                            items.Add(fr);
                        }
                    }

                    if (hasGoldMarker || qst.GoldMultiple > 0)
                        gold = param.ComputeGoldReward(playerLevel, questMinLevel, qst.GoldMultiple, ignoreLevel);

                    if (rewardSelectIdx >= 0)
                    {
                        var selectable = ParseItemPairs(qst.RewardSelectionIntData, playerJob, playerGrowType);
                        if (rewardSelectIdx < selectable.Count)
                            items.Add(selectable[rewardSelectIdx]);
                    }
                }

                var consumeItems = new List<QuestRewardItem>();

                int growNumber = 0;
                if (chainType == 1
                    || chainType == 2
                    || chainType == 10
                    || chainType == 20
                    || chainType == 25
                    || chainType == ChainTypeSlotExpansion)
                {
                    var rewardValues = ParseIntList(qst.RewardIntData);
                    if (rewardValues.Count > 0)
                        growNumber = rewardValues[0];
                }

                return new QuestReward
                {
                    Exp = exp,
                    Gold = gold,
                    ChainType = chainType,
                    GrowNumber = growNumber,
                    CreatureKind = qst.CreatureKind,
                    CreatureLevel = qst.CreatureLevel,
                    Items = items,
                    ConsumeItems = consumeItems
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestData] ERROR: reward calc failed, returning EMPTY reward: quest={questId}: {ex.Message}");
                return empty;
            }
        }

        private static int MapRewardType(string rewardType)
        {
            if (string.IsNullOrEmpty(rewardType)) return 0;
            switch (rewardType.Trim().ToLowerInvariant())
            {
                case "[item]": return 0;
                case "[grow type]": return 1;
                case "[awakening type]": return 2;
                case "[creature evolution]": return 10;
                case "[expert job]": return 20;
                case "[slot expansion]": return ChainTypeSlotExpansion;
                case "[event creature evolution]": return 25;
                default: return 0;
            }
        }

        private static List<QuestRewardItem> ParseItemPairs(
            string data,
            int playerJob = -1,
            int playerGrowType = -1,
            bool preserveGoldMarker = false)
        {
            var result = new List<QuestRewardItem>();
            if (string.IsNullOrWhiteSpace(data)) return result;

            var tokens = data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < tokens.Length)
            {
                int itemId;
                if (!int.TryParse(tokens[i], out itemId)) { i++; continue; }
                i++;

                if (i < tokens.Length && tokens[i].IndexOf("[job]", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    i++;
                    int jobId = -1;
                    int growType = -1;
                    int count = 1;
                    if (i < tokens.Length) { int.TryParse(tokens[i], out jobId); i++; }
                    if (i < tokens.Length) { int.TryParse(tokens[i], out growType); i++; }
                    if (i < tokens.Length) { int.TryParse(tokens[i], out count); i++; }

                    bool jobMatch = playerJob < 0 || jobId == playerJob;
                    bool growMatch = playerGrowType < 0 || growType == -1 || growType == (playerGrowType & 0xF);
                    if (itemId > 0 && jobMatch && growMatch)
                        result.Add(new QuestRewardItem { ItemId = itemId, Count = count });
                }
                else
                {
                    int count = 0;
                    if (i < tokens.Length) { int.TryParse(tokens[i], out count); i++; }
                    if (itemId > 0 || (preserveGoldMarker && itemId == 0))
                        result.Add(new QuestRewardItem { ItemId = itemId, Count = count });
                }
            }
            return result;
        }

        private static uint ComputeInitTrigger(QuestFile qst)
        {
            int typeCode = MapTypeString(qst.Type);
            string typeTag = NormalizeQuestTag(qst.Type);

            if (IsSeekAndMeetNpcQuest(qst))
                return ComputeSeekAndMeetNpcInitTrigger(qst.IntData);

            if (IsQuestClearQuest(qst))
            {
                var requiredQuestIds = ParseIntList(qst.IntData);
                requiredQuestIds.RemoveAll(id => id <= 0);
                return requiredQuestIds.Count > 0 ? (uint)requiredQuestIds.Count : 1;
            }

            if (typeTag == "condition under clear" || typeTag == "clear map")
                return ComputeTriggerFromIntData(qst.IntData, 4);

            if (typeTag == "condition under clear2")
                return ComputeTriggerFromIntData(qst.IntData, 5);

            if (typeCode == 25)
                return PackTrigger(1, 1, 0);

            if (typeCode == 1)
            {
                if (typeTag == "hunt monster")
                    return ComputeTriggerFromIntData(qst.IntData, 4);

                if (typeTag == "hunt enemy")
                    return ComputeTriggerFromIntData(qst.IntData, 5);

                if (qst.SubType == 6)
                {
                    var values = ParseIntList(qst.IntData);
                    if (values.Count >= 3 && values[2] > 0)
                        return (uint)values[2];
                }
            }

            return 1;
        }

        internal static uint ReplaceTriggerChannel(uint trigger, int channelIndex, long value)
        {
            int shift = channelIndex * 9;
            if (shift < 0 || shift > 18)
                return trigger;

            uint channelValue;
            if (value <= 0)
                channelValue = 0;
            else if (value > 0x1FF)
                channelValue = 0x1FF;
            else
                channelValue = (uint)value;

            return (trigger & ~(0x1FFu << shift)) | (channelValue << shift);
        }

        internal static int GetTriggerChannel(uint trigger, int channelIndex)
        {
            var shift = channelIndex * 9;
            if (shift < 0 || shift > 18)
                return 0;

            return (int)((trigger >> shift) & 0x1FFu);
        }

        internal static bool IsSeekAndMeetNpcQuest(int questId)
        {
            return IsSeekAndMeetNpcQuest(GetQuestFile(questId));
        }

        internal static bool IsMeetNpcQuest(int questId)
        {
            var tag = NormalizeQuestTag(GetQuestFile(questId)?.Type);
            return tag == "meet npc" || tag == "seek n meet npc";
        }

        private static bool IsSeekAndMeetNpcQuest(QuestFile qst)
        {
            return NormalizeQuestTag(qst?.Type) == "seek n meet npc";
        }

        private static uint ComputeSeekAndMeetNpcInitTrigger(string intData)
        {
            var values = ParseIntList(intData);
            if (values.Count < 3)
                return 1;

            int itemCount = values[1] > 0 ? values[1] : 1;
            int meetNpcCount = values[2] > 0 ? 1 : 0;
            return PackTrigger(itemCount, meetNpcCount, 0);
        }

        private static List<QuestRewardItem> ParseSeekAndMeetNpcItems(string intData)
        {
            var values = ParseIntList(intData);
            var result = new List<QuestRewardItem>();
            if (values.Count >= 2 && values[0] > 0 && values[1] > 0)
                result.Add(new QuestRewardItem { ItemId = values[0], Count = values[1] });
            return result;
        }

        private static uint ComputeTriggerFromIntData(string intData, int stride)
        {
            var values = ParseIntList(intData);
            if (values.Count == 0 || stride <= 0)
                return 1;

            int countOffset = stride - 1;

            var channels = new List<int>();
            for (int i = 0; i + stride <= values.Count; i += stride)
                channels.Add(values[i + countOffset]);

            if (channels.Count == 0)
                return 1;

            int f0 = channels.Count > 0 ? channels[0] : 0;
            int f1 = channels.Count > 1 ? channels[1] : 0;
            int f2 = channels.Count > 2 ? channels[2] : 0;
            return PackTrigger(f0, f1, f2);
        }

        private static uint PackTrigger(int f0, int f1, int f2)
        {
            return (uint)(((f2 & 0x1FF) << 18) | ((f1 & 0x1FF) << 9) | (f0 & 0x1FF));
        }

        internal static List<int> ParseIntList(string data)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(data)) return result;
            foreach (var token in data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int val;
                if (int.TryParse(token, out val))
                    result.Add(val);
            }
            return result;
        }

        private static int MapTypeString(string typeStr)
        {
            if (string.IsNullOrEmpty(typeStr)) return 0;
            var t = typeStr.Trim().ToLowerInvariant();
            switch (t)
            {
                case "[seeking]": return 1;
                case "[condition under clear]": return 2;
                case "[accumulate play]": return 3;
                case "[seeking repeat]": return 4;
                case "[powerwar win]": return 5;
                case "[condition under clear2]": return 6;
                case "[belong to winning power]": return 7;
                case "[powerwar point]": return 8;
                case "[hunt monster]": return 1;
                case "[clear map]": return 2;
                case "[meet npc]": return 1;
                case "[hunt enemy]": return 1;
                case "[use item]": return 1;
                case "[get item]": return 1;
                case "[get score]": return 1;
                case "[clear quest]": return 1;
                case "[quest clear]": return 1;
                case "[custom quest]": return 1;
                case "[send chatting]": return 1;
                case "[check life]": return 1;
                case "[amplify item]": return 1;
                case "[disjoint item]": return 1;
                case "[equipped item]": return 1;
                case "[check time]": return 1;
                case "[use fortune coin]": return 1;
                case "[meet secret npc]": return 1;
                case "[turn gold card]": return 1;
                case "[ui click]": return 1;
                case "[seek n meet npc]": return 1;
                case "[assault count]": return 1;
                case "[mobile]": return 1;
                case "[normal clear]": return 25;
                default: return 0;
            }
        }
    }

    internal sealed class QuestParameterTable
    {
        private Dictionary<char, int> _difficultyWeight = new Dictionary<char, int>();
        private int[] _expTable = new int[0];
        private int[] _goldTable = new int[0];
        private int _greenPenalty = 80;
        private int _greyPenalty = 30;

        public uint ComputeExp(int playerLevel, int questMinLevel, char difficulty, bool ignoreLevel)
        {
            int levelDiff = playerLevel - questMinLevel;
            int penalty = ComputeLevelPenalty(levelDiff);
            if (ignoreLevel) penalty = 100;

            int lookupLevel = ignoreLevel ? playerLevel : questMinLevel;
            int baseExp = (lookupLevel >= 1 && lookupLevel <= _expTable.Length) ? _expTable[lookupLevel - 1] : 0;

            int weight;
            if (!_difficultyWeight.TryGetValue(char.ToUpperInvariant(difficulty), out weight))
                weight = 10;

            return (uint)(penalty * ((long)weight * baseExp / 100) / 100);
        }

        public uint ComputeGold(int questMinLevel)
        {
            if (questMinLevel >= 0 && questMinLevel < _goldTable.Length)
                return (uint)_goldTable[questMinLevel];
            return 0;
        }

        public uint ComputeGoldReward(int playerLevel, int questMinLevel, int goldMultiple, bool ignoreLevel)
        {
            if (goldMultiple <= 0) goldMultiple = 100;
            int levelDiff = playerLevel - questMinLevel;
            int penalty = ignoreLevel ? 100 : ComputeLevelPenalty(levelDiff);
            int lookupIndex = ignoreLevel ? playerLevel - 1 : questMinLevel;
            int baseGold = (lookupIndex >= 0 && lookupIndex < _goldTable.Length) ? _goldTable[lookupIndex] : 0;
            return (uint)(goldMultiple * ((long)penalty * baseGold / 100) / 100);
        }

        private int ComputeLevelPenalty(int levelDiff)
        {
            if (levelDiff > 6 && levelDiff <= 11) return _greenPenalty;
            if (levelDiff <= 11) return 100;
            return _greyPenalty;
        }

        public static QuestParameterTable Parse(string content)
        {
            var t = new QuestParameterTable();
            if (string.IsNullOrEmpty(content)) return t;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            string section = null;
            var expValues = new List<int>();
            var goldValues = new List<int>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[") && line.EndsWith("]") || line.StartsWith("[/"))
                {
                    if (line == "[difficulty]") { section = "diff"; continue; }
                    if (line == "[/difficulty]") { section = null; continue; }
                    if (line == "[exp reward table]") { section = "exp"; continue; }
                    if (line == "[gold reward table]") { section = "gold"; continue; }
                    if (line.StartsWith("[green level penalty]")) { section = "green"; continue; }
                    if (line.StartsWith("[grey level penalty]")) { section = "grey"; continue; }
                    if (line.StartsWith("[/") || line.StartsWith("[")) { section = null; continue; }
                }

                if (section == "green" && line.Length > 0)
                {
                    int v; if (int.TryParse(line.Split(' ')[0], out v)) t._greenPenalty = v;
                    section = null;
                }
                else if (section == "grey" && line.Length > 0)
                {
                    int v; if (int.TryParse(line.Split(' ')[0], out v)) t._greyPenalty = v;
                    section = null;
                }
                else if (section == "diff" && line.Length > 0)
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j + 1 < tokens.Length; j += 2)
                    {
                        var key = tokens[j].Trim('`');
                        int val;
                        if (key.Length == 1 && int.TryParse(tokens[j + 1], out val))
                            t._difficultyWeight[key[0]] = val;
                    }
                }
                else if (section == "exp" && line.Length > 0)
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tok in tokens)
                    {
                        int v;
                        if (int.TryParse(tok, out v) && v >= 0)
                            expValues.Add(v);
                    }
                }
                else if (section == "gold" && line.Length > 0)
                {
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tok in tokens)
                    {
                        int v;
                        if (int.TryParse(tok, out v))
                            goldValues.Add(v);
                    }
                }
            }

            t._expTable = expValues.ToArray();
            t._goldTable = goldValues.ToArray();
            return t;
        }
    }
}
