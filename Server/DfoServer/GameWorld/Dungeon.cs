using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    public class Dungeon
    {
        private const byte StartMapSpecialPassiveObjectType = 9;

        internal static LstFile LoadLstFile(string relativePath)
        {
            var content = PvfArchiveAccessor.ReadText(relativePath);
            return LstFile.Parse(content);
        }

        private static readonly object _dungeonLstLock = new object();
        private static LstFile _dungeonLstCache;

        // dungeon.lst 与各 .dgn 解析结果按需缓存(PVF 只读, 解析结果视为不可变共享)。
        // 击杀热路径每杀一只怪要读 3-4 个副本标量, 此前每次都重新解码+解析整个 .dgn 文本。
        public static LstFile LoadDungeonLstFile()
        {
            var cached = _dungeonLstCache;
            if (cached != null) return cached;
            lock (_dungeonLstLock)
            {
                if (_dungeonLstCache == null)
                    _dungeonLstCache = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
                return _dungeonLstCache;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DungeonFile File, string FilePath)>
            _dungeonFileCache = new System.Collections.Concurrent.ConcurrentDictionary<int, (DungeonFile, string)>();

        // 缓存版 .dgn 读取。返回的解析对象是共享实例, 调用方只读不改
        // (与房间拓扑的迷宫缓存共享同一约定)。
        public static DungeonFile GetDungeonFile(int dungeonId)
            => LoadDungeonFileWithPath(dungeonId).File;

        internal static string ResolveFilePath(LstFile lstFile, int id, string description)
        {
            var entry = lstFile.GetById(id);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new Exception($"未找到{description}编号{id}");

            return entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
        }

        public struct MonsterSumInfo
        {
            public int Code { get; set; }

            public byte Level { get; set; }

            // START_MAP 对象类型。0..3 为怪物，5..8 为 APC/AICharacter，9 为特殊被动对象路径。
            public byte Type { get; set; }

            public bool IsBlocking { get; set; }

            // START_MAP 模板/波次字段。深渊隐藏行使用 map [hellparty] 的 order。
            public ushort TemplateOrder { get; set; }

            // START_MAP 运行序号。为空时按普通 monster/APC 计数自动生成。
            public int? PacketIndex { get; set; }

            // START_MAP 隐藏标记。0 为可见房间对象，1 为深渊隐藏模板行。
            public byte Flag0 { get; set; }

            // 深渊柱子挂接选择器。86 官方柱子路径消费 Flag1 == 0xFF 的 hidden row。
            public byte Flag1 { get; set; }

            // START_MAP 附加状态。当前深渊隐藏行保持 0。
            public int ExtraState { get; set; }

            // 是否为深渊柱子流程挂接的隐藏小队成员。为 true 时死亡走深渊专用掉落分支。
            public bool IsHellPartyActor { get; set; }

            // 深渊小队编号，对应 etc/hellparty.etc 的 [group index]。
            public int HellPartyGroupId { get; set; }

            // 深渊难度：1=A/非常困难，2=B/困难。
            public byte HellPartyDifficulty { get; set; }

            // [difficulty] 第 1 项，最终深渊装备奖励计算次数。
            public int HellRewardRollCount { get; set; }

            // monster/APC 脚本中的 [hell monster] 标记。为 true 时不触发最终装备奖励。
            public bool IsHellMonsterScript { get; set; }

            // Source coordinates retained for conditional runtime spawns.
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public struct MazeSumInfo
        {
            public int Index { get; set; }

            public int X { get; set; }

            public int Y { get; set; }

            public List<MonsterSumInfo> Monsters { get; set; }
        }

        public struct DungeonRoomCoordinate
        {
            public int X { get; set; }

            public int Y { get; set; }

            public int MapId { get; set; }

            public string FilePath { get; set; }
        }

        public sealed class LinkedDungeonEntry
        {
            public int DungeonId { get; set; }
            public int Rate { get; set; }
            public int Condition { get; set; }
        }

        public sealed class HellPartyWaveInfo
        {
            public int GroupId { get; set; }
            public int Order { get; set; }
            public List<MonsterSumInfo> Monsters { get; set; } = new List<MonsterSumInfo>();
        }

        public sealed class HellPartyRoomInfo
        {
            public int MapId { get; set; } = -1;
            public int NormalMapId { get; set; } = -1;
            public int X { get; set; }
            public int Y { get; set; }
            public int PillarObjectCode { get; set; }
            public int SpawnX { get; set; }
            public int SpawnY { get; set; }
            public HellPartyDifficultyRule DifficultyRule { get; set; }
            public List<HellPartyWaveInfo> Waves { get; set; } = new List<HellPartyWaveInfo>();

            public bool Found => MapId > 0;
        }

        public static byte GetDungeonBasicLv(int dungeonId)
        {
            var dngFile = GetDungeonFile(dungeonId);
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            return (byte)dngFile.BasisLevel;
        }

        public static int GetDungeonMinimumRequiredLevel(int dungeonId)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (loaded.File.MinimumRequiredLevel > 0)
                    return loaded.File.MinimumRequiredLevel;

                return loaded.File.BasisLevel;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsSuitableLevelDungeon(int dungeonId, int characterLevel)
        {
            return characterLevel > 0
                && TryGetSuitableLevelRange(dungeonId, out var minLevel, out var maxLevel)
                && characterLevel >= minLevel
                && characterLevel <= maxLevel;
        }

        public static bool TryGetSuitableLevelRange(int dungeonId, out int minLevel, out int maxLevel)
        {
            minLevel = 0;
            maxLevel = 0;

            try
            {
                var file = GetDungeonFile(dungeonId);
                // 适合等级使用 PVF 最小进入等级到基础等级的闭区间。
                minLevel = file.MinimumRequiredLevel;
                maxLevel = file.BasisLevel;

                if (minLevel <= 0 && maxLevel <= 0)
                    return false;
                if (minLevel <= 0)
                    minLevel = maxLevel;
                if (maxLevel <= 0)
                    maxLevel = minLevel;
                if (minLevel > maxLevel)
                {
                    var tmp = minLevel;
                    minLevel = maxLevel;
                    maxLevel = tmp;
                }

                return minLevel > 0 && maxLevel > 0;
            }
            catch
            {
                minLevel = 0;
                maxLevel = 0;
                return false;
            }
        }

        public static int GetMaxDifficultyCount(int dungeonId)
        {
            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                if (dngFile.DifficultyLevel != null && dngFile.DifficultyLevel.Length > 0)
                {
                    int count = 0;
                    foreach (var v in dngFile.DifficultyLevel)
                        if (v != 0) count++;
                    return count;
                }
                if (dngFile.DesignateDungeonDifficulty != null && dngFile.DesignateDungeonDifficulty.Length > 0)
                    return 5;
                if (dngFile.Difficulty >= 0)
                    return 5;
                return 0;
            }
            catch { return 0; }
        }

        public static int GetChampionCount(int dungeonId, int difficulty, int mazeIndex, out int[] namedMonsterCodes)
        {
            namedMonsterCodes = null;
            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                namedMonsterCodes = dngFile.NamedMonster;

                if (dngFile.Champion == null || dngFile.Champion.Length == 0)
                    return 0;

                int diffIdx = difficulty;
                if (diffIdx < 0) diffIdx = 0;
                if (diffIdx >= dngFile.Champion.Length) diffIdx = dngFile.Champion.Length - 1;
                int probBase = dngFile.Champion[diffIdx];

                int adjusted = probBase;
                switch (difficulty)
                {
                    case 1: adjusted = probBase * 150 / 100; break;
                    case 2: adjusted = probBase * 250 / 100; break;
                    case 3: adjusted = probBase * 500 / 100; break;
                }

                int mazeW = 4, mazeH = 5;
                if (dngFile.Mazes != null && mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count)
                {
                    var m = dngFile.Mazes[mazeIndex];
                    if (m.Width > 0) mazeW = m.Width;
                    if (m.Height > 0) mazeH = m.Height;
                }

                int area = mazeW * mazeH;
                return 100 * adjusted / area > Infrastructure.ServerRandom.Next(100) ? 1 : 0;
            }
            catch { return 0; }
        }

        public static void PromoteChampions(List<MonsterSumInfo> monsters, int count, int[] namedMonsterCodes = null)
        {
            if (count <= 0) return;

            var namedSet = namedMonsterCodes != null && namedMonsterCodes.Length > 0
                ? new HashSet<int>(namedMonsterCodes) : null;

            var normalIndices = new List<int>();
            for (int i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (monster.Type == 0
                    && monster.IsBlocking
                    && monster.Flag0 == 0
                    && (namedSet == null || !namedSet.Contains(monster.Code)))
                    normalIndices.Add(i);
            }

            for (int i = 0; i < count && normalIndices.Count > 0; i++)
            {
                int pick = Infrastructure.ServerRandom.Next(normalIndices.Count);
                int idx = normalIndices[pick];
                normalIndices.RemoveAt(pick);

                var m = monsters[idx];
                m.Type = 1;
                monsters[idx] = m;
            }
        }

        public static float GetExperienceWeight(int dungeonId)
        {
            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                return dngFile.ExperienceIncreasingPoint >= 0 ? dngFile.ExperienceIncreasingPoint : 1.0f;
            }
            catch
            {
                return 1.0f;
            }
        }

        public static MazeInfo GetDungeonDefaultMaze(int dungeonId)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");

            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            MazeInfo defaultMaze = null;
            foreach (var maze in dngFile.Mazes)
            {
                if (maze.QuestConnection == null)
                {
                    defaultMaze = maze;
                    break;
                }
            }

            if (defaultMaze == null)
            {
                defaultMaze = dngFile.Mazes[0];
            }

            return defaultMaze;
        }

        public static List<LinkedDungeonEntry> GetLinkedDungeonNextEntries(int dungeonId)
        {
            try
            {
                return ParseLinkedDungeonNextEntries(
                    GetDungeonFile(dungeonId)?.LinkedDungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Dungeon] linked dungeon parse failed: " +
                    $"dungeon={dungeonId} error={ex.Message}");
                return new List<LinkedDungeonEntry>();
            }
        }

        public static bool IsSpecialLinkedDungeon(int dungeonId)
        {
            try
            {
                var dungeonFile = GetDungeonFile(dungeonId);
                return dungeonFile?.SpecialDungeon == true
                    && ParseLinkedDungeonNextEntries(
                        dungeonFile.LinkedDungeon).Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public static LinkedDungeonEntry PickLinkedDungeonNext(int dungeonId)
        {
            var entries = GetLinkedDungeonNextEntries(dungeonId);
            if (entries.Count == 0)
                return null;

            var totalRate = 0;
            foreach (var entry in entries)
            {
                if (entry.Rate > 0
                    && totalRate <= int.MaxValue - entry.Rate)
                {
                    totalRate += entry.Rate;
                }
            }

            if (totalRate <= 0)
                return entries[0];

            var roll = Infrastructure.ServerRandom.Next(totalRate);
            foreach (var entry in entries)
            {
                if (entry.Rate <= 0)
                    continue;
                if (roll < entry.Rate)
                    return entry;
                roll -= entry.Rate;
            }

            return entries[0];
        }

        internal static List<LinkedDungeonEntry> ParseLinkedDungeonNextEntries(
            string linkedDungeon)
        {
            var result = new List<LinkedDungeonEntry>();
            if (string.IsNullOrWhiteSpace(linkedDungeon))
                return result;

            var blocks = new List<string>();
            var matches = Regex.Matches(
                linkedDungeon,
                @"\[next\](?<body>.*?)(?:\[/next\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
                blocks.Add(match.Groups["body"].Value);
            if (blocks.Count == 0)
                blocks.Add(linkedDungeon);

            foreach (var block in blocks)
            {
                var numbers = Regex.Matches(
                    block ?? string.Empty,
                    @"[+-]?\d+");
                for (var i = 0; i + 2 < numbers.Count; i += 3)
                {
                    if (!int.TryParse(
                            numbers[i].Value,
                            out var nextDungeonId)
                        || !int.TryParse(
                            numbers[i + 1].Value,
                            out var rate)
                        || !int.TryParse(
                            numbers[i + 2].Value,
                            out var condition)
                        || nextDungeonId <= 0)
                    {
                        continue;
                    }

                    result.Add(new LinkedDungeonEntry
                    {
                        DungeonId = nextDungeonId,
                        Rate = rate,
                        Condition = condition,
                    });
                }
            }

            return result;
        }

        private static readonly Lazy<Dictionary<int, bool>> _monsterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("monster/monster.lst", "monster"));
        private static readonly Lazy<Dictionary<int, bool>> _aiCharacterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("AICharacter/AICharacter.lst", "AICharacter"));
        private static readonly object _namedMonsterCacheLock = new object();
        private static readonly Dictionary<int, HashSet<int>> _namedMonsterCache = new Dictionary<int, HashSet<int>>();

        public static bool IsNamedMonster(int dungeonId, int monsterCode)
        {
            if (dungeonId <= 0 || monsterCode <= 0)
                return false;

            HashSet<int> namedSet;
            lock (_namedMonsterCacheLock)
            {
                if (!_namedMonsterCache.TryGetValue(dungeonId, out namedSet))
                {
                    namedSet = new HashSet<int>();
                    try
                    {
                        var loaded = LoadDungeonFileWithPath(dungeonId);
                        if (loaded.File.NamedMonster != null)
                        {
                            foreach (var code in loaded.File.NamedMonster)
                                if (code > 0) namedSet.Add(code);
                        }
                    }
                    catch { }

                    _namedMonsterCache[dungeonId] = namedSet;
                }
            }

            return namedSet.Contains(monsterCode);
        }

        public static int[] RandomizeBossPosition(int[] bossMap)
        {
            if (bossMap == null || bossMap.Length < 2) return null;
            int pairCount = bossMap.Length / 2;
            if (pairCount <= 1) return new[] { bossMap[0], bossMap[1] };
            int pick = Infrastructure.ServerRandom.Next(pairCount);
            return new[] { bossMap[pick * 2], bossMap[pick * 2 + 1] };
        }

        // df_game_r CBattle_Field::GetAppropriateMaze — two-pass quest connection matching.
        // Pass 1 (questType=0): match mazes where the quest is currently active (IsDoingQuest).
        // Pass 2 (questType=1): match mazes where the quest is already cleared (isClearQuest).
        // qc[0]=questType, qc[1]=questId, qc[2]=minDifficulty (-1 = no restriction).
        public static (MazeInfo Maze, int Index) SelectDungeonMaze(
            int dungeonId,
            int difficulty = 0,
            ICollection<int> activeQuestIds = null,
            ICollection<int> clearedQuestIds = null)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            var doingMatch = FindQuestConnectedMazeIndex(dngFile.Mazes, activeQuestIds, 0, difficulty);
            if (doingMatch >= 0)
            {
                var qc = dngFile.Mazes[doingMatch].QuestConnection;
                FileLogger.Log($"[Dungeon] SelectMaze: dungeon={dungeonId} matched quest maze #{doingMatch} (questId={qc[1]} type=doing)");
                return (dngFile.Mazes[doingMatch], doingMatch);
            }

            var clearedMatch = FindQuestConnectedMazeIndex(dngFile.Mazes, clearedQuestIds, 1, difficulty);
            if (clearedMatch >= 0)
            {
                var qc = dngFile.Mazes[clearedMatch].QuestConnection;
                FileLogger.Log($"[Dungeon] SelectMaze: dungeon={dungeonId} matched quest maze #{clearedMatch} (questId={qc[1]} type=cleared)");
                return (dngFile.Mazes[clearedMatch], clearedMatch);
            }

            var candidates = new List<(MazeInfo maze, int index)>();
            for (int i = 0; i < dngFile.Mazes.Count; i++)
            {
                if (dngFile.Mazes[i].QuestConnection == null)
                    candidates.Add((dngFile.Mazes[i], i));
            }

            if (candidates.Count == 0)
                return (dngFile.Mazes[0], 0);

            var pick = candidates[Infrastructure.ServerRandom.Next(candidates.Count)];
            return (pick.maze, pick.index);
        }

        public static MazeInfo GetDungeonMaze(int dungeonId, int mazeIndex)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                var dungeonFile = loaded.File;
                if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                    return null;

                return mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count
                    ? dungeonFile.Mazes[mazeIndex]
                    : dungeonFile.Mazes[0];
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] GetDungeonMaze ERROR: dungeon={dungeonId} maze={mazeIndex} {ex.Message}");
                return null;
            }
        }

        // df_game_r CParty::CheckQuestConnection — match by questType and difficulty.
        private static int FindQuestConnectedMazeIndex(
            IReadOnlyList<MazeInfo> mazes,
            ICollection<int> questIds,
            int requiredQuestType,
            int difficulty)
        {
            if (mazes == null || questIds == null || questIds.Count == 0)
                return -1;

            var candidates = new List<int>();
            for (int i = 0; i < mazes.Count; i++)
            {
                var qc = mazes[i].QuestConnection;
                if (qc == null || qc.Length < 2)
                    continue;
                if (qc[0] != requiredQuestType)
                    continue;
                if (!questIds.Contains(qc[1]))
                    continue;
                if (requiredQuestType == 0 && qc.Length >= 3 && qc[2] >= 0 && difficulty < qc[2])
                    continue;
                candidates.Add(i);
            }

            if (candidates.Count == 0)
                return -1;
            if (candidates.Count == 1)
                return candidates[0];
            return candidates[Infrastructure.ServerRandom.Next(candidates.Count)];
        }

        public static int[] GetLayeredMapIds(int dungeonId, int x, int y, int mazeIndex)
        {
            var dngFile = GetDungeonFile(dungeonId);
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                return null;
            var maze = (mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count) ? dngFile.Mazes[mazeIndex] : dngFile.Mazes[0];
            if (maze.MapSpecifications == null) return null;
            foreach (var spec in maze.MapSpecifications)
            {
                if (spec.Type == "layered" && spec.X == x && spec.Y == y && spec.LayeredMapIds != null)
                    return spec.LayeredMapIds;
            }
            return null;
        }

        internal static bool TryGetWarpMapOverride(
            int dungeonId,
            int mazeIndex,
            int targetX,
            int targetY,
            out int sourceX,
            out int sourceY,
            out int destX,
            out int destY,
            out int overrideMapId)
        {
            sourceX = sourceY = destX = destY = overrideMapId = -1;

            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                if (dngFile.Mazes == null || dngFile.Mazes.Count == 0
                    || string.IsNullOrWhiteSpace(dngFile.WarpMapCondition))
                    return false;

                var maze = mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count
                    ? dngFile.Mazes[mazeIndex]
                    : dngFile.Mazes[0];
                if (!TryParseWarpMapCondition(
                    dngFile.WarpMapCondition,
                    out sourceX,
                    out sourceY,
                    out destX,
                    out destY))
                {
                    return false;
                }

                if (sourceX != targetX || sourceY != targetY)
                    return false;

                if (maze?.MapSpecifications == null)
                    return false;

                foreach (var spec in maze.MapSpecifications)
                {
                    if (spec.X != destX || spec.Y != destY || spec.Index <= 0)
                        continue;

                    overrideMapId = spec.Index;
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] warp map condition parse failed: dungeon={dungeonId} maze={mazeIndex} target=({targetX},{targetY}) error={ex.Message}");
            }

            return false;
        }

        private static bool TryParseWarpMapCondition(
            string raw,
            out int sourceX,
            out int sourceY,
            out int destX,
            out int destY)
        {
            sourceX = sourceY = destX = destY = -1;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (TryReadTaggedPoint(raw, "source grid pos", out sourceX, out sourceY)
                && TryReadTaggedPoint(raw, "dest grid pos", out destX, out destY))
            {
                return true;
            }

            var matches = Regex.Matches(raw, @"-?\d+");
            if (matches.Count < 4)
                return false;

            return int.TryParse(matches[0].Value, out sourceX)
                && int.TryParse(matches[1].Value, out sourceY)
                && int.TryParse(matches[2].Value, out destX)
                && int.TryParse(matches[3].Value, out destY);
        }

        private static bool TryReadTaggedPoint(string raw, string tag, out int x, out int y)
        {
            x = y = -1;
            var pattern = @"\[" + Regex.Escape(tag) + @"\]\s*(?<x>-?\d+)\s+(?<y>-?\d+)";
            var match = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
            return match.Success
                && int.TryParse(match.Groups["x"].Value, out x)
                && int.TryParse(match.Groups["y"].Value, out y);
        }

        public static List<MonsterSumInfo> GetMapMonsterConditionSummaryInformation(
            int mapId,
            int dungeonId,
            int x,
            int y,
            ICollection<int> monsterCodes)
        {
            var result = new List<MonsterSumInfo>();
            if (mapId <= 0 || monsterCodes == null || monsterCodes.Count == 0)
                return result;

            try
            {
                var dungeonBasicLv = GetDungeonBasicLv(dungeonId);
                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapFilePath = ResolveFilePath(maplst, mapId, "map");
                var mapFile = MapFile.Parse(
                    PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));

                AppendConditionalMonsterInfos(
                    result,
                    mapFile.MonsterConditionMonsters,
                    dungeonBasicLv,
                    monsterCodes,
                    conditionalSummon: false);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] monster condition load failed: dungeon={dungeonId} room=({x},{y}) map={mapId}: {ex.Message}");
            }

            return result;
        }

        public static bool IsHellDungeon(int dungeonId)
        {
            try
            {
                var area = WorldMap.GetAreaByDungeonId(dungeonId);
                if (area != null)
                    return area.HellDungeon;

                var loaded = LoadDungeonFileWithPath(dungeonId);
                return loaded.File.GetIntValue("hell dungeon", 0) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static int FindHellMapIdForRoom(int dungeonId, int x, int y, int mazeIndex)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                var dungeonFile = loaded.File;
                if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                    return -1;

                var maze = (mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count)
                    ? dungeonFile.Mazes[mazeIndex]
                    : dungeonFile.Mazes[0];

                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapDirCandidates = BuildMapDirCandidates(maplst, maze, loaded.FilePath);

                foreach (var entry in maplst.Entries)
                {
                    if (!IsInCandidateDir(entry.FilePath, mapDirCandidates))
                        continue;

                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (string.IsNullOrEmpty(fileName)
                        || !fileName.StartsWith("hell_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fileName.IndexOf($"({x},{y})", StringComparison.OrdinalIgnoreCase) >= 0
                        || fileName.IndexOf($"({x}.{y})", StringComparison.OrdinalIgnoreCase) >= 0)
                        return entry.Id;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] FindHellMapIdForRoom ERROR: dungeon={dungeonId} room=({x},{y}) {ex.Message}");
            }

            return -1;
        }

        public static IReadOnlyList<DungeonRoomCoordinate> GetDungeonRoomCoordinates(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze)
        {
            var result = new List<DungeonRoomCoordinate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (maze == null)
                {
                    var dungeonFile = loaded.File;
                    if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                        return result;

                    maze = mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count
                        ? dungeonFile.Mazes[mazeIndex]
                        : dungeonFile.Mazes[0];
                }

                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapDirCandidates = BuildMapDirCandidates(maplst, maze, loaded.FilePath);

                foreach (var entry in maplst.Entries)
                {
                    if (!IsInCandidateDir(entry.FilePath, mapDirCandidates))
                        continue;

                    var fileName = Path.GetFileName(entry.FilePath);
                    if (!DungeonMapResolver.TryParseMapFileCoordinate(fileName, out var x, out var y))
                        continue;

                    var key = x + "," + y;
                    if (!seen.Add(key))
                        continue;

                    result.Add(new DungeonRoomCoordinate
                    {
                        X = x,
                        Y = y,
                        MapId = entry.Id,
                        FilePath = entry.FilePath,
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] GetDungeonRoomCoordinates ERROR: dungeon={dungeonId} maze={mazeIndex} {ex.Message}");
            }

            return result;
        }

        public static HellPartyRoomInfo FindHellMapRoom(int dungeonId, MazeInfo maze, int mazeIndex, byte difficulty)
        {
            if (maze?.MapSpecifications == null)
                return new HellPartyRoomInfo();

            if (maze.SealDoorMapIndex > 0
                && maze.SealDoorPos != null
                && maze.SealDoorPos.Length >= 2)
            {
                var sealX = maze.SealDoorPos[0];
                var sealY = maze.SealDoorPos[1];
                var normalMapId = FindNormalMapIdForRoom(maze, sealX, sealY);
                if (normalMapId > 0)
                {
                    FileLogger.Log($"[Dungeon] HellParty seal door: dungeon={dungeonId} room=({sealX},{sealY}) hellMap={maze.SealDoorMapIndex} normalMap={normalMapId}");
                    return BuildHellPartyRoomInfo(maze.SealDoorMapIndex, normalMapId, sealX, sealY, dungeonId, difficulty);
                }

                FileLogger.Log($"[Dungeon] HellParty seal door ignored: dungeon={dungeonId} room=({sealX},{sealY}) hellMap={maze.SealDoorMapIndex} normalMap missing");
            }

            foreach (var spec in maze.MapSpecifications)
            {
                var hellMapId = FindHellMapIdForRoom(dungeonId, spec.X, spec.Y, mazeIndex);
                if (hellMapId <= 0)
                    continue;

                return BuildHellPartyRoomInfo(hellMapId, spec.Index, spec.X, spec.Y, dungeonId, difficulty);
            }

            return new HellPartyRoomInfo();
        }

        private static int FindNormalMapIdForRoom(MazeInfo maze, int x, int y)
        {
            if (maze?.MapSpecifications == null)
                return -1;

            foreach (var spec in maze.MapSpecifications)
                if (spec.X == x && spec.Y == y && spec.Index > 0)
                    return spec.Index;

            return -1;
        }

        private static HellPartyRoomInfo BuildHellPartyRoomInfo(int mapId, int normalMapId, int x, int y, int dungeonId, byte difficulty)
        {
            try
            {
                var mapFile = LoadMapFile(mapId);
                SpecialPassiveObjectInfo pillar = null;
                foreach (var obj in mapFile.SpecialPassiveObjects)
                {
                    if (pillar == null)
                        pillar = obj;
                    if (obj.HellPartyEntries.Count > 0)
                    {
                        pillar = obj;
                        break;
                    }
                }

                return new HellPartyRoomInfo
                {
                    MapId = mapId,
                    NormalMapId = normalMapId,
                    X = x,
                    Y = y,
                    PillarObjectCode = pillar?.ObjectCode ?? 0,
                    SpawnX = pillar?.X ?? 0,
                    SpawnY = pillar?.Y ?? 0,
                    DifficultyRule = HellPartyData.GetDifficultyRule(difficulty),
                    Waves = BuildHellPartyWaves(mapFile, dungeonId, difficulty),
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] BuildHellPartyRoomInfo ERROR: map={mapId} {ex.Message}");
                return new HellPartyRoomInfo();
            }
        }

        private static List<HellPartyWaveInfo> BuildHellPartyWaves(MapFile mapFile, int dungeonId, byte difficulty)
        {
            var result = new List<HellPartyWaveInfo>();
            var entriesByOrder = new SortedDictionary<int, List<HellPartyMapEntry>>();
            foreach (var obj in mapFile.SpecialPassiveObjects)
            {
                foreach (var entry in obj.HellPartyEntries)
                {
                    if (!entriesByOrder.TryGetValue(entry.Order, out var list))
                    {
                        list = new List<HellPartyMapEntry>();
                        entriesByOrder[entry.Order] = list;
                    }
                    list.Add(entry);
                }
            }

            foreach (var pair in entriesByOrder)
            {
                var candidates = new List<HellPartyMapEntry>();
                foreach (var entry in pair.Value)
                    if (HellPartyData.HasEntries(entry.GroupId, difficulty))
                        candidates.Add(entry);

                var selected = PickHellPartyEntry(candidates);
                if (selected == null)
                    continue;

                var monsters = BuildHellPartyMonsterInfos(selected.GroupId, dungeonId, difficulty);
                if (monsters.Count == 0)
                    continue;

                result.Add(new HellPartyWaveInfo
                {
                    GroupId = selected.GroupId,
                    Order = pair.Key,
                    Monsters = monsters,
                });

                FileLogger.Log($"[Dungeon] HellParty wave: order={pair.Key} group={selected.GroupId} mode={difficulty} monsters={monsters.Count}");
            }

            return result;
        }

        private static List<MonsterSumInfo> BuildHellPartyMonsterInfos(int groupId, int dungeonId, byte difficulty)
        {
            var result = new List<MonsterSumInfo>();
            var groupEntries = HellPartyData.GetEntries(groupId, difficulty);
            var difficultyRule = HellPartyData.GetDifficultyRule(difficulty);
            var rewardRollCount = Math.Max(0, difficultyRule?.RewardRollCount ?? 0);
            foreach (var groupEntry in groupEntries)
            {
                byte type;
                byte level;
                bool isHellMonsterScript;
                if (groupEntry.EntityType == 1)
                {
                    type = 5;
                    if (!TryGetAICharacterLevel(groupEntry.Code, out level))
                    {
                        FileLogger.Log($"[Dungeon] HellParty APC code={groupEntry.Code} not found in AICharacter.lst; fallback to dungeon level");
                        level = GetDungeonBasicLv(dungeonId);
                    }
                    isHellMonsterScript = IsAICharacterHellMonster(groupEntry.Code);
                }
                else
                {
                    type = 0;
                    level = GetDungeonBasicLv(dungeonId);
                    isHellMonsterScript = IsMonsterHellMonster(groupEntry.Code);
                }

                result.Add(new MonsterSumInfo
                {
                    Code = groupEntry.Code,
                    Level = level,
                    Type = type,
                    IsBlocking = true,
                    IsHellPartyActor = true,
                    HellPartyGroupId = groupId,
                    HellPartyDifficulty = difficulty,
                    HellRewardRollCount = rewardRollCount,
                    IsHellMonsterScript = isHellMonsterScript,
                });
            }

            return result;
        }

        internal static (DungeonFile File, string FilePath) LoadDungeonFileWithPath(int dungeonId)
        {
            return _dungeonFileCache.GetOrAdd(dungeonId, id =>
            {
                var dgnlst = LoadDungeonLstFile();
                var dgnFilePath = ResolveFilePath(dgnlst, id, "dungeon");
                var dungeonFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
                return (dungeonFile, dgnFilePath);
            });
        }

        internal static bool TryGetTowerOfDespairFloor(int dungeonId, out int floor)
        {
            floor = 0;
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (loaded.File.TowerOfDespair <= 0)
                    return false;

                var dungeonFileName = Path.GetFileNameWithoutExtension(loaded.FilePath) ?? string.Empty;
                var match = Regex.Match(
                    dungeonFileName,
                    @"TowerOfDespair(?<floor>\d{3})$",
                    RegexOptions.IgnoreCase);
                return match.Success
                    && int.TryParse(match.Groups["floor"].Value, out floor)
                    && floor > 0;
            }
            catch
            {
                floor = 0;
                return false;
            }
        }

        internal static bool TryGetTowerOfDespairDungeonId(int floor, out int dungeonId)
        {
            dungeonId = 0;
            if (floor < 1 || floor > 100)
                return false;

            try
            {
                var expectedFileName = $"TowerOfDespair{floor:000}";
                foreach (var entry in LoadDungeonLstFile().Entries)
                {
                    var fileName = Path.GetFileNameWithoutExtension(entry.FilePath) ?? string.Empty;
                    if (!fileName.EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    dungeonId = entry.Id;
                    return dungeonId > 0;
                }
            }
            catch
            {
                dungeonId = 0;
            }

            return false;
        }

        private static MapFile LoadMapFile(int mapId)
        {
            var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
            var mapFilePath = ResolveFilePath(maplst, mapId, "map");
            return MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));
        }

        internal static List<string> BuildMapDirCandidates(LstFile maplst, MazeInfo maze, string dungeonFilePath)
        {
            var result = new List<string>();

            void AddDirCandidate(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(dir)) return;
                foreach (var existing in result)
                    if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
                result.Add(dir);
            }

            void AddMapId(int mapId)
            {
                var entry = maplst.GetById(mapId);
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                    AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
            }

            if (maze.MapSpecifications != null && maplst != null)
            {
                foreach (var spec in maze.MapSpecifications)
                {
                    AddMapId(spec.Index);
                    if (spec.MapCandidates != null)
                        foreach (var id in spec.MapCandidates)
                            AddMapId(id);
                    if (spec.LayeredMapIds != null)
                        foreach (var id in spec.LayeredMapIds)
                            AddMapId(id);
                }
            }

            var dgnDir = System.IO.Path.GetFileNameWithoutExtension(dungeonFilePath);
            AddDirCandidate(dgnDir);
            if (dgnDir != null && dgnDir.StartsWith("tutorial_", StringComparison.OrdinalIgnoreCase))
                AddDirCandidate(dgnDir.Substring("tutorial_".Length));

            if (maplst != null && !string.IsNullOrEmpty(dgnDir))
            {
                foreach (var entry in maplst.Entries)
                {
                    if (entry.FilePath == null) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (fileName != null && fileName.StartsWith(dgnDir, StringComparison.OrdinalIgnoreCase))
                        AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
                }
            }

            return result;
        }

        private static bool IsInCandidateDir(string filePath, List<string> candidates)
        {
            if (filePath == null) return false;
            foreach (var dir in candidates)
            {
                if (filePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || filePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static HellPartyMapEntry PickHellPartyEntry(List<HellPartyMapEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return null;

            var total = 0;
            foreach (var entry in entries)
                if (entry.Rate > 0)
                    total += entry.Rate;

            if (total <= 0)
                return entries[0];

            var roll = Infrastructure.ServerRandom.Next(total);
            foreach (var entry in entries)
            {
                if (entry.Rate <= 0)
                    continue;
                if (roll < entry.Rate)
                    return entry;
                roll -= entry.Rate;
            }

            return entries[0];
        }

        private static bool IsMonsterHellMonster(int monsterCode)
        {
            return _monsterHellFlags.Value.TryGetValue(monsterCode, out var value) && value;
        }

        private static bool IsAICharacterHellMonster(int aiCharacterCode)
        {
            return _aiCharacterHellFlags.Value.TryGetValue(aiCharacterCode, out var value) && value;
        }

        private static Dictionary<int, bool> LoadHellMonsterFlags(string lstPath, string baseDir)
        {
            var result = new Dictionary<int, bool>();
            try
            {
                var lst = LstFile.Parse(PvfArchiveAccessor.ReadText(lstPath));
                foreach (var entry in lst.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    string content;
                    try { content = PvfArchiveAccessor.ReadText(Path.Combine(baseDir, entry.FilePath)); }
                    catch { continue; }

                    result[entry.Id] = ParseHellMonsterFlag(content);
                }
                FileLogger.Log($"[Dungeon] HellMonster flags loaded: {baseDir} count={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] HellMonster flags load failed: {lstPath} {ex.Message}");
            }
            return result;
        }

        private static bool ParseHellMonsterFlag(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            var match = Regex.Match(content, @"\[hell monster\]\s*([+-]?\d+)", RegexOptions.IgnoreCase);
            return match.Success
                && int.TryParse(match.Groups[1].Value, out var value)
                && value == 1;
        }

        public static List<MonsterSumInfo> GetMapConditionalSummonSummaryInformation(
            int mapId,
            int dungeonId,
            int x,
            int y,
            ICollection<int> monsterCodes)
        {
            var result = new List<MonsterSumInfo>();
            if (mapId <= 0 || monsterCodes == null || monsterCodes.Count == 0)
                return result;

            try
            {
                var dungeonBasicLv = GetDungeonBasicLv(dungeonId);
                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapFilePath = ResolveFilePath(maplst, mapId, "map");
                var mapFile = MapFile.Parse(
                    PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));

                AppendConditionalMonsterInfos(
                    result,
                    mapFile.ConditionalSummonMonsters,
                    dungeonBasicLv,
                    monsterCodes,
                    conditionalSummon: true);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] conditional summon load failed: dungeon={dungeonId} room=({x},{y}) map={mapId}: {ex.Message}");
            }

            return result;
        }

        private static void AppendConditionalMonsterInfos(
            List<MonsterSumInfo> result,
            IReadOnlyList<MonsterInfo> monsters,
            byte dungeonBasicLv,
            ICollection<int> monsterCodes,
            bool conditionalSummon)
        {
            if (result == null || monsters == null || monsterCodes == null)
                return;

            for (var index = 0; index < monsters.Count; index++)
            {
                var item = monsters[index];
                if (!item.MonsterId.HasValue
                    || item.MonsterId.Value <= 0
                    || !monsterCodes.Contains(item.MonsterId.Value))
                {
                    continue;
                }

                var monsterType = (byte)item.Type;
                if (monsterType > 3)
                    monsterType = 0;

                var rawLevel = item.Lv.GetValueOrDefault() != 0
                    ? dungeonBasicLv + item.AutoLv.GetValueOrDefault()
                    : item.AutoLv.GetValueOrDefault();
                var level = rawLevel > 0
                    ? (byte)Math.Min(rawLevel, 255)
                    : dungeonBasicLv;
                var conditionalOrder = item.ConditionalParam0.GetValueOrDefault();

                result.Add(new MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    Type = monsterType,
                    Level = level,
                    X = item.X.GetValueOrDefault(),
                    Y = item.Y.GetValueOrDefault(),
                    Z = item.Z.GetValueOrDefault(),
                    IsBlocking = !conditionalSummon,
                    TemplateOrder = conditionalSummon && conditionalOrder > 0
                        ? (ushort)Math.Min(conditionalOrder, ushort.MaxValue)
                        : (ushort)0,
                    PacketIndex = conditionalSummon && item.ConditionalParam0.HasValue
                        ? item.ConditionalParam0.Value
                        : index,
                    Flag0 = conditionalSummon ? (byte)1 : (byte)0,
                });
            }
        }

        public static MazeSumInfo GetDungeonMapMonsterSummaryInformation(int dungeonId, int x, int y, int mazeIndex = -1, int overrideMapId = -1, int[] bossPos = null)
        {
            if (dungeonId == 5000)
            {
                return new MazeSumInfo
                {
                    X = 0,
                    Y = 0,
                    Index = 36250,
                    Monsters = new List<MonsterSumInfo>(),
                };
            }

            byte dungeonBasicLv = GetDungeonBasicLv(dungeonId);

            MazeInfo defaultMaze;
            if (mazeIndex >= 0)
            {
                var dgnFile = GetDungeonFile(dungeonId);
                defaultMaze = (mazeIndex < dgnFile.Mazes.Count) ? dgnFile.Mazes[mazeIndex] : GetDungeonDefaultMaze(dungeonId);
            }
            else
            {
                defaultMaze = GetDungeonDefaultMaze(dungeonId);
            }
            if (x == 0xFF && y == 0xFF)
            {
                x = defaultMaze.StartMap[0];
                y = defaultMaze.StartMap[1];
            }

            if (overrideMapId > 0)
            {
                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapFilePath = ResolveFilePath(maplst, overrideMapId, "门");
                var mapFile = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));
                return new MazeSumInfo
                {
                    Monsters = ParseMapActors(mapFile, dungeonBasicLv, overrideMapId, dungeonId, x, y),
                    X = x,
                    Y = y,
                    Index = overrideMapId,
                };
            }

            int mapId = DungeonMapResolver.ResolveMapId(dungeonId, x, y, defaultMaze, mazeIndex, bossPos);

            if (mapId == -1)
            {
                FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation WARNING: no map resolved for dungeon={dungeonId} maze={mazeIndex} room=({x},{y})");
                return new MazeSumInfo { X = x, Y = y, Index = 0, Monsters = new List<MonsterSumInfo>() };
            }

            var maplst2 = LoadLstFile(Path.Combine("map", "map.lst"));
            var resolvedMapFilePath = ResolveFilePath(maplst2, mapId, "门");
            var resolvedMapFile = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", resolvedMapFilePath)));

            return new MazeSumInfo
            {
                Monsters = ParseMapActors(resolvedMapFile, dungeonBasicLv, mapId, dungeonId, x, y),
                X = x,
                Y = y,
                Index = mapId,
            };
        }

        private static List<MonsterSumInfo> ParseMapActors(MapFile mapFile, byte dungeonBasicLv, int mapId, int dungeonId, int x, int y)
        {
            var list = new List<MonsterSumInfo>();
            foreach (var item in mapFile.Monsters)
            {
                if (!item.MonsterId.HasValue || item.MonsterId.Value <= 0)
                {
                    FileLogger.Log($"[Dungeon] ParseMapActors: skip monster with invalid id in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    continue;
                }
                var monsterType = (byte)item.Type;
                if (monsterType > 3)
                {
                    FileLogger.Log($"[Dungeon] ParseMapActors: clamp monster type {monsterType} to 0 in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    monsterType = 0;
                }
                int rawMonsterLevel = item.Lv.GetValueOrDefault() != 0
                    ? dungeonBasicLv + item.AutoLv.GetValueOrDefault()
                    : item.AutoLv.GetValueOrDefault();
                byte monsterLevel = rawMonsterLevel > 0 ? (byte)Math.Min(rawMonsterLevel, 255) : dungeonBasicLv;
                list.Add(new MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    Type = monsterType,
                    Level = monsterLevel,
                    IsBlocking = true,
                });
            }

            AppendSpecialPassiveObjects(list, mapFile, dungeonBasicLv, mapId, dungeonId, x, y);

            foreach (var apc in mapFile.AICharacters)
            {
                if (apc.Code <= 0 || !TryGetAICharacterLevel(apc.Code, out var apcLevel))
                {
                    FileLogger.Log($"[Dungeon] ParseMapActors: skip APC code={apc.Code} not found in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    continue;
                }
                var apcType = (byte)apc.AIType;
                if (apcType < 5 || apcType > 8)
                {
                    FileLogger.Log($"[Dungeon] ParseMapActors: clamp APC type {apcType} to 5 in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    apcType = 5;
                }
                list.Add(new MonsterSumInfo
                {
                    Code = apc.Code,
                    Type = apcType,
                    Level = apcLevel,
                    IsBlocking = IsBlockingAICharacter(apc),
                });
            }

            return list;
        }

        // IDA check_grid_clear (0x830A0E8): spawnType==100 && spawnFlag==0 blocks passage.
        // APC 的 spawnType 不是 100, 不参与房间通关判定 — 无论敌我阵营。
        private static bool IsBlockingAICharacter(AICharacterInfo apc)
        {
            return false;
        }

        private static void AppendSpecialPassiveObjects(
            List<MonsterSumInfo> list,
            MapFile mapFile,
            byte dungeonBasicLv,
            int mapId,
            int dungeonId,
            int x,
            int y)
        {
            if (list == null || mapFile?.SpecialPassiveObjects == null || mapFile.SpecialPassiveObjects.Count == 0)
                return;

            var objectRows = 0;
            var templateRows = 0;
            for (var objectIndex = 0; objectIndex < mapFile.SpecialPassiveObjects.Count; objectIndex++)
            {
                var obj = mapFile.SpecialPassiveObjects[objectIndex];
                if (obj == null)
                    continue;

                if (obj.ObjectCode > 0)
                {
                    list.Add(new MonsterSumInfo
                    {
                        Code = obj.ObjectCode,
                        Type = StartMapSpecialPassiveObjectType,
                        Level = 0,
                        IsBlocking = false,
                        PacketIndex = objectIndex,
                    });
                    objectRows++;
                }
            }

            for (var objectIndex = 0; objectIndex < mapFile.SpecialPassiveObjects.Count; objectIndex++)
            {
                var obj = mapFile.SpecialPassiveObjects[objectIndex];
                if (obj?.Spawns == null || obj.Spawns.Count == 0)
                    continue;

                for (var spawnIndex = 0; spawnIndex < obj.Spawns.Count; spawnIndex++)
                {
                    var spawn = obj.Spawns[spawnIndex];
                    if (spawn.Code <= 0
                        || !string.Equals(spawn.Kind, "[monster]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var level = spawn.Level > 0
                        ? (byte)Math.Min(spawn.Level, 255)
                        : dungeonBasicLv;
                    list.Add(new MonsterSumInfo
                    {
                        Code = spawn.Code,
                        Type = 0,
                        Level = level,
                        IsBlocking = false,
                        TemplateOrder = (ushort)Math.Min(objectIndex, ushort.MaxValue),
                        PacketIndex = spawnIndex,
                        Flag0 = 1,
                        Flag1 = (byte)Math.Min(objectIndex, byte.MaxValue),
                    });
                    templateRows++;
                }
            }

            if (objectRows > 0 || templateRows > 0)
                FileLogger.Log($"[Dungeon] special passive objects: dungeon={dungeonId} room=({x},{y}) map={mapId} objects={objectRows} templates={templateRows}");
        }


        private static byte GetAICharacterLevel(int apcCode)
        {
            if (TryGetAICharacterLevel(apcCode, out var level))
                return level;

            throw new Exception($"AICharacter code={apcCode} 在 AICharacter.lst 中不存在或无法解析等级");
        }

        private static bool TryGetAICharacterLevel(int apcCode, out byte level)
        {
            level = 0;
            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("AICharacter/AICharacter.lst"));
            var entry = lst.GetById(apcCode);
            if (entry == null)
                return false;

            var content = PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", entry.FilePath));
            var match = System.Text.RegularExpressions.Regex.Match(content,
                @"\[minimum info\]\s*`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)");
            if (!match.Success)
                return false;

            int parsedLevel = int.Parse(match.Groups[1].Value);
            if (parsedLevel <= 0 || parsedLevel > 255)
                return false;

            level = (byte)parsedLevel;
            return true;
        }
    }
}
