using System;
using System.Collections.Generic;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    public sealed class WorldMapDungeonEntry
    {
        public int DungeonId { get; set; }
        public int QuestId { get; set; }
        public bool HasExplicitQuestId { get; set; }
        public bool InProgressOnly { get; set; }
    }

    public sealed class HellTicketItem
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    public sealed class WorldMapArea
    {
        public int AreaId { get; set; }
        public string FilePath { get; set; }
        public List<WorldMapDungeonEntry> Dungeons { get; } = new List<WorldMapDungeonEntry>();
        public bool HellDungeon { get; set; }
        public List<int> HellQuestIds { get; } = new List<int>();
        public List<HellTicketItem> HellFreePassItems { get; } = new List<HellTicketItem>();
        public List<int> HellNormalTicketItemIds { get; } = new List<int>();
    }

    public static class WorldMap
    {
        private static readonly Lazy<WorldMapIndex> Index =
            new Lazy<WorldMapIndex>(LoadIndex);

        public static IReadOnlyList<WorldMapArea> Areas => Index.Value.Areas;

        public static WorldMapArea GetAreaByDungeonId(int dungeonId)
        {
            if (dungeonId <= 0)
                return null;

            Index.Value.AreaByDungeonId.TryGetValue(dungeonId, out var area);
            return area;
        }

        public static bool IsTaskExclusiveDungeon(int dungeonId)
        {
            return IsQuestDungeonAsset(dungeonId);
        }

        public static bool IsTaskExclusiveDungeonAvailable(
            int dungeonId,
            ISet<int> activeQuestIds)
        {
            if (!IsTaskExclusiveDungeon(dungeonId))
                return true;
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return false;

            if (Index.Value.EntriesByDungeonId.TryGetValue(
                    dungeonId,
                    out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.InProgressOnly
                        && entry.HasExplicitQuestId
                        && entry.QuestId > 0
                        && activeQuestIds.Contains(entry.QuestId))
                    {
                        return true;
                    }
                }
            }

            foreach (var questId in GetDungeonQuestConnectionIds(dungeonId))
            {
                if (activeQuestIds.Contains(questId))
                    return true;
            }

            foreach (var questId in activeQuestIds)
            {
                if (QuestData.ReferencesDungeon(questId, dungeonId))
                    return true;
            }

            return false;
        }

        public static IReadOnlyList<int> GetTaskExclusiveQuestIds(int dungeonId)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (Index.Value.EntriesByDungeonId.TryGetValue(
                    dungeonId,
                    out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.InProgressOnly
                        && entry.HasExplicitQuestId
                        && entry.QuestId > 0
                        && seen.Add(entry.QuestId))
                    {
                        result.Add(entry.QuestId);
                    }
                }
            }

            foreach (var questId in GetDungeonQuestConnectionIds(dungeonId))
            {
                if (seen.Add(questId))
                    result.Add(questId);
            }

            return result;
        }

        public static bool ShouldPersistDungeonPermission(int dungeonId) =>
            !IsTaskExclusiveDungeon(dungeonId);

        private static bool IsQuestDungeonAsset(int dungeonId)
        {
            if (dungeonId <= 0)
                return false;

            try
            {
                var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);
                var path = (loaded.FilePath ?? string.Empty)
                    .Replace('\\', '/')
                    .TrimStart('/');
                return path.StartsWith("quest/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("dungeon/quest/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<int> GetDungeonQuestConnectionIds(
            int dungeonId)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            try
            {
                var dungeon = Dungeon.GetDungeonFile(dungeonId);
                AddQuestConnectionId(dungeon?.QuestConnection, seen, result);
                if (dungeon?.Mazes != null)
                {
                    foreach (var maze in dungeon.Mazes)
                        AddQuestConnectionId(maze?.QuestConnection, seen, result);
                }
            }
            catch
            {
                // Missing or malformed PVF data cannot grant a task-only entry.
            }

            return result;
        }

        private static void AddQuestConnectionId(
            int[] connection,
            HashSet<int> seen,
            List<int> result)
        {
            if (connection == null || connection.Length < 2)
                return;

            var questId = connection[1];
            if (questId > 0 && seen.Add(questId))
                result.Add(questId);
        }

        public static bool IsHellDungeon(int dungeonId)
        {
            var area = GetAreaByDungeonId(dungeonId);
            return area != null && area.HellDungeon;
        }

        public static int GetHellNormalTicketNeedCount(int dungeonMinLevel)
        {
            if (dungeonMinLevel <= 44)
                return 0;

            return ((40 * dungeonMinLevel - 1800) / 100) + 10;
        }

        private static WorldMapIndex LoadIndex()
        {
            var areas = new List<WorldMapArea>();
            var byDungeon = new Dictionary<int, WorldMapArea>();

            try
            {
                var entries = ParseWorldMapList(PvfArchiveAccessor.ReadText("worldmap/worldmap.lst"));
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    var area = LoadArea(entry.Id, entry.FilePath);
                    if (area == null)
                        continue;

                    areas.Add(area);
                    foreach (var dungeon in area.Dungeons)
                    {
                        if (dungeon.DungeonId <= 0 || byDungeon.ContainsKey(dungeon.DungeonId))
                            continue;

                        byDungeon[dungeon.DungeonId] = area;
                    }
                }

                FileLogger.Log($"[WorldMap] loaded areas={areas.Count} dungeonRefs={byDungeon.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[WorldMap] load failed: {ex.Message}");
            }

            return new WorldMapIndex(areas, byDungeon);
        }

        private static WorldMapArea LoadArea(int areaId, string relativeFilePath)
        {
            try
            {
                var normalized = relativeFilePath.Replace('\\', '/').TrimStart('/');
                var pvfPath = normalized.StartsWith("worldmap/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : Path.Combine("worldmap", normalized);
                var content = PvfArchiveAccessor.ReadText(pvfPath);

                var area = new WorldMapArea
                {
                    AreaId = areaId,
                    FilePath = normalized,
                };

                ParseDungeons("dungeon", content, area);
                area.HellDungeon = ParseFirstInt("hell dungeon", content) == 1;
                ParseIntList("hell quest", content, area.HellQuestIds);
                ParseTicketPairs("hell freepass item", content, area.HellFreePassItems);
                ParseIntList("item condition", content, area.HellNormalTicketItemIds);
                FileLogger.Log($"[WorldMap] area={area.AreaId} file={area.FilePath} dungeons={area.Dungeons.Count} hell={area.HellDungeon} freepass={area.HellFreePassItems.Count} normalTickets={area.HellNormalTicketItemIds.Count}");
                return area;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[WorldMap] area load failed: area={areaId} file={relativeFilePath} {ex.Message}");
                return null;
            }
        }

        private static List<LstEntry> ParseWorldMapList(string content)
        {
            var lst = LstFile.Parse(content);
            if (lst.Entries.Count > 0)
                return lst.Entries;

            var result = new List<LstEntry>();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var tokens = Tokenize(rawLine);
                if (tokens.Count < 2 || !int.TryParse(tokens[0], out var id))
                    continue;

                result.Add(new LstEntry { Id = id, FilePath = tokens[1] });
            }

            return result;
        }

        private static void ParseDungeons(string sectionName, string content, WorldMapArea area)
        {
            var pendingInProgressRows = false;
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                if (tokens.Count == 0)
                    continue;

                // TimeGate.wdm 这类文件会把 [in progress] 独立成一行，下一行按“任务ID 副本ID”成对出现。
                if (IsInProgressMarker(tokens, 0, out var markerTokenCount) && markerTokenCount == tokens.Count)
                {
                    pendingInProgressRows = true;
                    continue;
                }

                if (IsInProgressMarker(tokens, 0, out markerTokenCount))
                {
                    tokens.RemoveRange(0, markerTokenCount);
                    if (tokens.Count == 0)
                        continue;

                    ParseInProgressDungeonPairs(tokens, area);
                    pendingInProgressRows = false;
                    continue;
                }

                if (pendingInProgressRows)
                {
                    ParseInProgressDungeonPairs(tokens, area);
                    pendingInProgressRows = false;
                    continue;
                }

                for (var i = 0; i < tokens.Count;)
                {
                    if (!int.TryParse(tokens[i], out var dungeonId))
                    {
                        i++;
                        continue;
                    }

                    i++;
                    var inProgressOnly = false;
                    if (IsInProgressMarker(tokens, i, out markerTokenCount))
                    {
                        inProgressOnly = true;
                        i += markerTokenCount;
                    }

                    var questId = -1;
                    var hasExplicitQuestId =
                        i < tokens.Count && int.TryParse(tokens[i], out questId);
                    if (hasExplicitQuestId)
                        i++;

                    area.Dungeons.Add(new WorldMapDungeonEntry
                    {
                        DungeonId = dungeonId,
                        QuestId = questId,
                        HasExplicitQuestId = hasExplicitQuestId,
                        InProgressOnly = inProgressOnly,
                    });
                }
            }
        }

        private static void ParseInProgressDungeonPairs(List<string> tokens, WorldMapArea area)
        {
            for (var i = 0; i + 1 < tokens.Count;)
            {
                if (!int.TryParse(tokens[i], out var questId))
                {
                    i++;
                    continue;
                }

                if (!int.TryParse(tokens[i + 1], out var dungeonId))
                {
                    i++;
                    continue;
                }

                if (dungeonId > 0)
                {
                    area.Dungeons.Add(new WorldMapDungeonEntry
                    {
                        DungeonId = dungeonId,
                        QuestId = questId,
                        HasExplicitQuestId = true,
                        InProgressOnly = true,
                    });
                }

                i += 2;
            }
        }

        private static bool IsInProgressMarker(List<string> tokens, int index, out int tokenCount)
        {
            tokenCount = 0;
            if (tokens == null || index < 0 || index >= tokens.Count)
                return false;

            if (tokens[index].Equals("[in progress]", StringComparison.OrdinalIgnoreCase))
            {
                tokenCount = 1;
                return true;
            }

            if (index + 1 < tokens.Count
                && tokens[index].Equals("[in", StringComparison.OrdinalIgnoreCase)
                && tokens[index + 1].Equals("progress]", StringComparison.OrdinalIgnoreCase))
            {
                tokenCount = 2;
                return true;
            }

            return false;
        }

        private static void ParseTicketPairs(string sectionName, string content, List<HellTicketItem> result)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                for (var i = 0; i + 1 < tokens.Count; i += 2)
                {
                    if (!int.TryParse(tokens[i], out var itemId))
                        continue;
                    if (!int.TryParse(tokens[i + 1], out var count))
                        count = 1;
                    if (itemId > 0 && count > 0)
                        result.Add(new HellTicketItem { ItemId = itemId, Count = count });
                }
            }
        }

        private static void ParseIntList(string sectionName, string content, List<int> result)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                foreach (var token in tokens)
                    if (int.TryParse(token, out var value) && value > 0)
                        result.Add(value);
            }
        }

        private static int ParseFirstInt(string sectionName, string content)
        {
            foreach (var line in EnumerateSectionLines(sectionName, content))
            {
                var tokens = Tokenize(line);
                foreach (var token in tokens)
                    if (int.TryParse(token, out var value))
                        return value;
            }

            return 0;
        }

        private static IEnumerable<string> EnumerateSectionLines(string sectionName, string content)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrEmpty(content))
                yield break;

            var startTag = "[" + sectionName + "]";
            var endTag = "[/" + sectionName + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                yield break;

            start += startTag.Length;
            var end = content.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                var next = content.IndexOf("[", start, StringComparison.Ordinal);
                end = next >= 0 ? next : content.Length;
            }

            var section = content.Substring(start, end - start);
            var lines = section.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = StripLineComment(rawLine).Trim();
                if (line.Length > 0)
                    yield return line;
            }
        }

        private static List<string> Tokenize(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(line))
                return result;

            var tokens = line.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var trimmed = StripBacktick(token.Trim());
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }

            return result;
        }

        private static string StripBacktick(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2);
            return value ?? string.Empty;
        }

        private static string StripLineComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            return commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
        }

        private sealed class WorldMapIndex
        {
            public WorldMapIndex(List<WorldMapArea> areas, Dictionary<int, WorldMapArea> areaByDungeonId)
            {
                Areas = areas;
                AreaByDungeonId = areaByDungeonId;
                EntriesByDungeonId = new Dictionary<int, List<WorldMapDungeonEntry>>();
                foreach (var area in areas)
                {
                    foreach (var entry in area.Dungeons)
                    {
                        if (entry.DungeonId <= 0)
                            continue;
                        if (!EntriesByDungeonId.TryGetValue(
                                entry.DungeonId,
                                out var entries))
                        {
                            entries = new List<WorldMapDungeonEntry>();
                            EntriesByDungeonId[entry.DungeonId] = entries;
                        }
                        entries.Add(entry);
                    }
                }
            }

            public List<WorldMapArea> Areas { get; }
            public Dictionary<int, WorldMapArea> AreaByDungeonId { get; }
            public Dictionary<int, List<WorldMapDungeonEntry>> EntriesByDungeonId { get; }
        }
    }
}
