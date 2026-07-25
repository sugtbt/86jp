using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    public static class IndependentDropSystem
    {
        private struct DropEntry
        {
            public int ItemId;
            public int[] Probs;
            public int[] Counts;
            public int LevelMin;
            public int LevelMax;
            public int Difficulty;
            public List<(int ItemId, int CumulativeWeight)> List;
            public int TotalWeight;
        }

        private static Dictionary<int, List<DropEntry>> _monsterDrops;
        private static readonly object _lock = new object();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try { Load(); }
                catch (Exception ex) { FileLogger.Log($"[IndependentDrop] INIT FAILED: {ex}"); }
                _loaded = true;
            }
        }

        public static List<DropInfo> GenerateDrops(
            int monsterCode, int difficulty, int dungeonLevel,
            DnfLcg lcg, ref ushort slotCounter)
        {
            EnsureLoaded();
            var result = new List<DropInfo>();
            if (_monsterDrops == null) return result;
            if (!_monsterDrops.TryGetValue(monsterCode, out var entries)) return result;

            int diffIdx = Math.Max(0, Math.Min(difficulty, 4));

            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];

                if (entry.LevelMin > 0 && entry.LevelMax > 0)
                {
                    if (dungeonLevel < entry.LevelMin || dungeonLevel > entry.LevelMax)
                        continue;
                }

                if (entry.Difficulty >= 0 && entry.Difficulty != difficulty)
                    continue;

                int prob = entry.Probs[diffIdx];
                int count = entry.Counts[diffIdx];
                if (prob <= 0 || count <= 0) continue;

                int dropCount = 0;
                if (entry.ItemId != 0 || entry.TotalWeight > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (prob > lcg.Next(1000000))
                            dropCount++;
                    }
                }
                else
                {
                    dropCount = count;
                }

                if (dropCount <= 0) continue;

                if (entry.TotalWeight > 0 && entry.List != null && entry.List.Count > 0)
                {
                    for (int d = 0; d < dropCount; d++)
                    {
                        int roll = lcg.Next(entry.TotalWeight);
                        int itemId = entry.List[entry.List.Count - 1].ItemId;
                        for (int li = 0; li < entry.List.Count; li++)
                        {
                            if (roll < entry.List[li].CumulativeWeight)
                            {
                                itemId = entry.List[li].ItemId;
                                break;
                            }
                        }
                        AddDrop(result, itemId, ref slotCounter);
                    }
                }
                else if (entry.ItemId > 0)
                {
                    for (int d = 0; d < dropCount; d++)
                        AddDrop(result, entry.ItemId, ref slotCounter);
                }
            }

            return result;
        }

        private static void AddDrop(List<DropInfo> drops, int itemId, ref ushort slotCounter)
        {
            slotCounter++;
            drops.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
        }

        private static void Load()
        {
            _monsterDrops = new Dictionary<int, List<DropEntry>>();

            var scriptPools = LoadScriptPools();
            LoadIndependentDrop(scriptPools);

            int totalEntries = 0;
            foreach (var kv in _monsterDrops)
                totalEntries += kv.Value.Count;
            FileLogger.Log($"[IndependentDrop] Loaded: {totalEntries} entries for {_monsterDrops.Count} monsters, {scriptPools.Count} script pools");
        }

        private static Dictionary<int, List<(int, int)>> LoadScriptPools()
        {
            var pools = new Dictionary<int, List<(int, int)>>();

            string lstText;
            try { lstText = GameWorld.PvfArchiveAccessor.ReadText("Etc/IndependentDrop.lst"); }
            catch { return pools; }
            if (string.IsNullOrWhiteSpace(lstText)) return pools;

            var lst = LstFile.Parse(lstText);
            foreach (var entry in lst.Entries)
            {
                string scriptText;
                try { scriptText = GameWorld.PvfArchiveAccessor.ReadText("Etc/" + entry.FilePath); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(scriptText)) continue;

                var listItems = ParseListSection(scriptText);
                if (listItems.Count > 0)
                    pools[entry.Id] = listItems;
            }

            return pools;
        }

        private static List<(int ItemId, int Weight)> ParseListSection(string text)
        {
            var result = new List<(int, int)>();
            int start = text.IndexOf("[list]", StringComparison.Ordinal);
            if (start < 0) return result;
            start += "[list]".Length;
            int end = text.IndexOf("[/list]", start, StringComparison.Ordinal);
            if (end < 0) end = text.Length;

            var tokens = text.Substring(start, end - start)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                if (int.TryParse(tokens[i], out int id) && int.TryParse(tokens[i + 1], out int w))
                    result.Add((id, w));
            }
            return result;
        }

        private static void LoadIndependentDrop(Dictionary<int, List<(int, int)>> scriptPools)
        {
            var text = GameWorld.PvfArchiveAccessor.ReadText("Etc/Independent_Drop.etc");
            if (string.IsNullOrWhiteSpace(text)) return;

            int secStart = text.IndexOf("[independent drop]", StringComparison.Ordinal);
            if (secStart < 0) return;
            secStart += "[independent drop]".Length;

            var tokens = new Queue<string>(
                text.Substring(secStart)
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            while (tokens.Count >= 17)
            {
                string peek = PeekToken(tokens);
                if (peek.StartsWith("[") && !peek.StartsWith("[list]")) break;

                int type = NextInt(tokens);
                int monsterCode = NextInt(tokens);
                int itemId = NextInt(tokens);

                var probs = new int[5];
                for (int i = 0; i < 5; i++) probs[i] = NextInt(tokens);

                var counts = new int[5];
                for (int i = 0; i < 5; i++) counts[i] = NextInt(tokens);

                int levelMin = NextInt(tokens);
                int levelMax = NextInt(tokens);
                int diff = NextInt(tokens);
                int listFlag = NextInt(tokens);

                List<(int ItemId, int CumulativeWeight)> itemList = null;
                int totalWeight = 0;

                if (listFlag == 1)
                {
                    SkipToListTag(tokens);
                    var rawList = ParseListFromTokens(tokens);
                    itemList = BuildCumulativeList(rawList);
                    totalWeight = itemList.Count > 0
                        ? itemList[itemList.Count - 1].CumulativeWeight : 0;
                }
                else if (listFlag == 2)
                {
                    SkipToListTag(tokens);
                    var indices = ParseIndicesFromTokens(tokens);
                    var merged = new List<(int, int)>();
                    foreach (int idx in indices)
                    {
                        if (scriptPools.TryGetValue(idx, out var pool))
                            merged.AddRange(pool);
                    }
                    itemList = BuildCumulativeList(merged);
                    totalWeight = itemList.Count > 0
                        ? itemList[itemList.Count - 1].CumulativeWeight : 0;
                }

                if (type != 0) continue;

                var entry = new DropEntry
                {
                    ItemId = itemId,
                    Probs = probs,
                    Counts = counts,
                    LevelMin = levelMin,
                    LevelMax = levelMax,
                    Difficulty = diff,
                    List = itemList,
                    TotalWeight = totalWeight
                };

                if (!_monsterDrops.TryGetValue(monsterCode, out var list))
                {
                    list = new List<DropEntry>();
                    _monsterDrops[monsterCode] = list;
                }
                list.Add(entry);
            }
        }

        private static List<(int ItemId, int CumulativeWeight)> BuildCumulativeList(List<(int ItemId, int Weight)> raw)
        {
            var result = new List<(int, int)>();
            int cum = 0;
            for (int i = 0; i < raw.Count; i++)
            {
                cum += raw[i].Weight;
                result.Add((raw[i].ItemId, cum));
            }
            return result;
        }

        private static string PeekToken(Queue<string> q)
        {
            return q.Count > 0 ? q.Peek() : "";
        }

        private static int NextInt(Queue<string> q)
        {
            if (q.Count == 0) return 0;
            var t = q.Dequeue();
            return int.TryParse(t, out int v) ? v : 0;
        }

        private static void SkipToListTag(Queue<string> q)
        {
            int limit = 8;
            while (q.Count > 0 && q.Peek() != "[list]" && --limit >= 0)
                q.Dequeue();
            if (q.Count > 0 && q.Peek() == "[list]") q.Dequeue();
        }

        private static List<(int, int)> ParseListFromTokens(Queue<string> q)
        {
            var result = new List<(int, int)>();
            while (q.Count >= 2)
            {
                if (q.Peek() == "[/list]") { q.Dequeue(); break; }
                int id = NextInt(q);
                if (q.Count == 0) break;
                if (q.Peek() == "[/list]") { q.Dequeue(); break; }
                int w = NextInt(q);
                result.Add((id, w));
            }
            return result;
        }

        private static List<int> ParseIndicesFromTokens(Queue<string> q)
        {
            var result = new List<int>();
            while (q.Count > 0)
            {
                if (q.Peek() == "[/list]") { q.Dequeue(); break; }
                result.Add(NextInt(q));
            }
            return result;
        }
    }
}
