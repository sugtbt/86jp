using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Dungeon
{
    internal enum SpecialDungeonKind
    {
        None,
        SeaChase,
        TimeCrack,
        SeizeMoney,
        SealForest,
        GentInfiltrate,
    }

    internal sealed class SpecialDungeonRuntime
    {
        private readonly HashSet<int> _sealForestBuffMonsterCodes = new HashSet<int>();
        private readonly List<int> _sealForestBuffIds = new List<int>();
        private readonly List<int> _seaChaseAppliedBuffIds = new List<int>();
        private readonly List<int> _timeCrackBuffIds = new List<int>();
        private readonly Dictionary<int, int> _gentInfiltrateTowerRequired =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> _gentInfiltrateTowerDestroyed =
            new Dictionary<int, int>();
        private bool _seizeMoneyClearRewardGenerated;

        internal SpecialDungeonRuntime(int dungeonId, SpecialDungeonKind kind, SpecialDungeonModuleConfig config)
        {
            DungeonId = dungeonId;
            Kind = kind;
            Config = config ?? SpecialDungeonModuleConfig.Empty;

            if (kind == SpecialDungeonKind.SeizeMoney)
                SeizeMoneyGauge = Config.SeizeMoney.GaugeMax;
            if (kind == SpecialDungeonKind.GentInfiltrate
                && Config.TimerSecondsByDungeonId.TryGetValue(dungeonId, out var seconds))
                GentInfiltrateTimerSeconds = seconds;
        }

        internal int DungeonId { get; }
        internal SpecialDungeonKind Kind { get; }
        internal SpecialDungeonModuleConfig Config { get; }
        internal int SeizeMoneyGauge { get; private set; }
        internal ushort SeizeMoneyBossSeq { get; private set; }
        internal bool? SeaChaseMiniGameSucceeded { get; private set; }
        internal IReadOnlyList<int> SeaChaseAppliedBuffIds => _seaChaseAppliedBuffIds;
        internal int TimeCrackGauge { get; private set; }
        internal IReadOnlyList<int> TimeCrackBuffIds => _timeCrackBuffIds;
        internal IReadOnlyList<int> SealForestBuffIds => _sealForestBuffIds;
        internal IReadOnlyDictionary<int, int> GentInfiltrateTowerRequired => _gentInfiltrateTowerRequired;
        internal int GentInfiltrateTimerSeconds { get; private set; }
        internal bool GentInfiltrateConditionComplete { get; private set; }
        internal bool GentInfiltrateStrongWarlord { get; private set; }
        internal bool GentInfiltrateTimedOut { get; private set; }
        internal string GentInfiltrateCompletionSource { get; private set; } = string.Empty;

        internal SpecialDungeonRuntime CloneFresh()
        {
            var clone = new SpecialDungeonRuntime(DungeonId, Kind, Config);
            clone.GentInfiltrateTimerSeconds = GentInfiltrateTimerSeconds;
            foreach (var pair in _gentInfiltrateTowerRequired)
            {
                clone._gentInfiltrateTowerRequired[pair.Key] = pair.Value;
                clone._gentInfiltrateTowerDestroyed[pair.Key] = 0;
            }

            return clone;
        }

        internal void NoteSeizeMoneyBossSeq(ushort bossSeq)
        {
            if (Kind == SpecialDungeonKind.SeizeMoney && bossSeq != 0)
                SeizeMoneyBossSeq = bossSeq;
        }

        internal bool NoteSeaChaseMiniGameResult(bool succeeded)
        {
            if (Kind != SpecialDungeonKind.SeaChase)
                return false;

            SeaChaseMiniGameSucceeded = succeeded;
            return true;
        }

        internal bool NoteSeaChaseBuffsApplied(IReadOnlyList<int> buffIds)
        {
            if (Kind != SpecialDungeonKind.SeaChase || buffIds == null || buffIds.Count == 0)
                return false;

            for (var i = 0; i < buffIds.Count; i++)
            {
                var buffId = buffIds[i];
                if (buffId > 0 && !_seaChaseAppliedBuffIds.Contains(buffId))
                    _seaChaseAppliedBuffIds.Add(buffId);
            }

            return _seaChaseAppliedBuffIds.Count > 0;
        }

        internal bool TryConsumeSeaChaseAppliedBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.SeaChase || _seaChaseAppliedBuffIds.Count == 0)
                return false;

            buffIds.AddRange(_seaChaseAppliedBuffIds);
            _seaChaseAppliedBuffIds.Clear();
            return true;
        }

        internal bool IsTimeCrackInvincibleMonster(int monsterCode)
            => Kind == SpecialDungeonKind.TimeCrack
                && Config.TimeCrack.InvincibleMonsterCodes.Contains(monsterCode);

        internal bool TryAddTimeCrackGauge(
            int monsterCode,
            bool isChampion,
            out int previous,
            out int current,
            out int delta,
            out bool filled)
        {
            previous = TimeCrackGauge;
            current = TimeCrackGauge;
            delta = 0;
            filled = false;

            if (Kind != SpecialDungeonKind.TimeCrack
                || monsterCode <= 0
                || IsTimeCrackInvincibleMonster(monsterCode))
            {
                return false;
            }

            var config = Config.TimeCrack;
            var max = Math.Max(1, config.SandGaugeMax);
            delta = Math.Max(
                1,
                isChampion
                    ? config.SandGaugeGainOnChampion
                    : config.SandGaugeGainOnKill);
            current = Math.Min(max, previous + delta);
            TimeCrackGauge = current;
            filled = current >= max;
            return true;
        }

        internal void ResetTimeCrackGauge()
        {
            if (Kind == SpecialDungeonKind.TimeCrack)
                TimeCrackGauge = 0;
        }

        internal bool NoteTimeCrackBuffApplied(int buffId)
        {
            if (Kind != SpecialDungeonKind.TimeCrack || buffId <= 0)
                return false;

            if (!_timeCrackBuffIds.Contains(buffId))
                _timeCrackBuffIds.Add(buffId);
            return true;
        }

        internal bool TryConsumeTimeCrackBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.TimeCrack || _timeCrackBuffIds.Count == 0)
                return false;

            buffIds.AddRange(_timeCrackBuffIds);
            _timeCrackBuffIds.Clear();
            return true;
        }

        internal bool TryReserveSeizeMoneyClearReward(
            int remainingGoldUnits,
            int maxDropCount,
            out int count,
            out int gauge)
        {
            count = 0;
            gauge = SeizeMoneyGauge;
            if (Kind != SpecialDungeonKind.SeizeMoney || _seizeMoneyClearRewardGenerated)
                return false;

            _seizeMoneyClearRewardGenerated = true;
            var cfg = Config.SeizeMoney;
            var unitValue = Math.Max(1, cfg.GaugeSubOnDamage);
            var maxUnits = Math.Max(1, cfg.GaugeMax / unitValue);
            if (remainingGoldUnits < 0)
                remainingGoldUnits = 0;
            if (remainingGoldUnits > maxUnits)
                remainingGoldUnits = maxUnits;

            gauge = Math.Min(cfg.GaugeMax, remainingGoldUnits * unitValue);
            SeizeMoneyGauge = gauge;

            maxDropCount = Math.Max(0, maxDropCount);
            count = (int)Math.Floor((remainingGoldUnits * maxDropCount / (double)maxUnits) + 0.5d);
            if (count > maxDropCount)
                count = maxDropCount;

            return count > 0;
        }

        internal bool TryMarkSealForestBuffMonster(int monsterCode, out SealForestBuffEntry entry)
        {
            entry = null;
            if (Kind != SpecialDungeonKind.SealForest)
                return false;

            if (!Config.SealForest.BuffsByMonsterCode.TryGetValue(monsterCode, out entry))
                return false;

            if (!_sealForestBuffMonsterCodes.Add(monsterCode))
                return false;

            if (!_sealForestBuffIds.Contains(entry.BuffId))
                _sealForestBuffIds.Add(entry.BuffId);

            return true;
        }

        internal bool TryConsumeSealForestBuffIds(out List<int> buffIds)
        {
            buffIds = new List<int>();
            if (Kind != SpecialDungeonKind.SealForest
                || _sealForestBuffIds.Count == 0)
            {
                return false;
            }

            buffIds.AddRange(_sealForestBuffIds);
            _sealForestBuffIds.Clear();
            _sealForestBuffMonsterCodes.Clear();
            return true;
        }

        internal void ConfigureGentInfiltrateBossEntrance(string condition, int timerSeconds)
        {
            if (Kind != SpecialDungeonKind.GentInfiltrate)
                return;

            _gentInfiltrateTowerRequired.Clear();
            _gentInfiltrateTowerDestroyed.Clear();
            GentInfiltrateConditionComplete = false;
            GentInfiltrateStrongWarlord = false;
            GentInfiltrateTimedOut = false;
            GentInfiltrateCompletionSource = string.Empty;
            GentInfiltrateTimerSeconds = timerSeconds > 0
                ? timerSeconds
                : GentInfiltrateTimerSeconds;

            var tokens = Tokenize(condition);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!string.Equals(tokens[i], "[hunt monster]", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (i + 1 >= tokens.Count || !int.TryParse(tokens[i + 1], out var count) || count <= 0)
                    continue;

                var pos = i + 2;
                for (var n = 0; n < count && pos + 2 < tokens.Count; n++, pos += 3)
                {
                    if (!int.TryParse(tokens[pos], out var monsterCode) || monsterCode <= 0)
                        continue;

                    var required = 1;
                    if (int.TryParse(tokens[pos + 2], out var parsedRequired) && parsedRequired > 0)
                        required = parsedRequired;

                    _gentInfiltrateTowerRequired[monsterCode] = required;
                    _gentInfiltrateTowerDestroyed[monsterCode] = 0;
                }
                break;
            }
        }

        internal bool TryMarkGentInfiltrateTowerDestroyed(
            int monsterCode,
            out int destroyed,
            out int required,
            out int totalDestroyed,
            out int totalRequired,
            out bool completed)
        {
            destroyed = 0;
            required = 0;
            totalDestroyed = 0;
            totalRequired = 0;
            completed = false;

            if (Kind != SpecialDungeonKind.GentInfiltrate
                || !_gentInfiltrateTowerRequired.TryGetValue(monsterCode, out required))
            {
                return false;
            }

            if (!_gentInfiltrateTowerDestroyed.TryGetValue(monsterCode, out destroyed))
                destroyed = 0;

            if (destroyed < required)
            {
                destroyed++;
                _gentInfiltrateTowerDestroyed[monsterCode] = destroyed;
            }

            ComputeGentInfiltrateProgress(out totalDestroyed, out totalRequired);
            completed = TryCompleteGentInfiltrate("tower", strongWarlord: !GentInfiltrateTimedOut);
            return true;
        }

        internal bool TryCompleteGentInfiltrateByTimer(
            out int totalDestroyed,
            out int totalRequired)
        {
            ComputeGentInfiltrateProgress(out totalDestroyed, out totalRequired);
            if (Kind == SpecialDungeonKind.GentInfiltrate
                && !GentInfiltrateConditionComplete)
            {
                GentInfiltrateTimedOut = true;
            }

            return false;
        }

        private bool TryCompleteGentInfiltrate(string source, bool strongWarlord)
        {
            if (Kind != SpecialDungeonKind.GentInfiltrate
                || GentInfiltrateConditionComplete)
            {
                return false;
            }

            ComputeGentInfiltrateProgress(out var totalDestroyed, out var totalRequired);
            if (totalRequired <= 0 || totalDestroyed < totalRequired)
                return false;

            GentInfiltrateConditionComplete = true;
            GentInfiltrateStrongWarlord = strongWarlord;
            GentInfiltrateCompletionSource = source ?? string.Empty;
            return true;
        }

        private void ComputeGentInfiltrateProgress(out int totalDestroyed, out int totalRequired)
        {
            totalDestroyed = 0;
            totalRequired = 0;
            foreach (var pair in _gentInfiltrateTowerRequired)
            {
                totalRequired += pair.Value;
                _gentInfiltrateTowerDestroyed.TryGetValue(pair.Key, out var value);
                totalDestroyed += Math.Min(value, pair.Value);
            }
        }

        private static List<string> Tokenize(string value)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return tokens;

            foreach (Match match in Regex.Matches(value, "`([^`]*)`|\\S+"))
                tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Value);

            return tokens;
        }
    }

    internal sealed class SpecialDungeonModuleConfig
    {
        internal static readonly SpecialDungeonModuleConfig Empty = new SpecialDungeonModuleConfig();

        private static readonly Lazy<SpecialDungeonModuleConfig> Cached =
            new Lazy<SpecialDungeonModuleConfig>(Load);

        internal SeizeMoneyConfig SeizeMoney { get; } = new SeizeMoneyConfig();
        internal SeaChaseConfig SeaChase { get; } = new SeaChaseConfig();
        internal TimeCrackConfig TimeCrack { get; } = new TimeCrackConfig();
        internal SealForestConfig SealForest { get; } = new SealForestConfig();
        internal Dictionary<int, int> TimerSecondsByDungeonId { get; } = new Dictionary<int, int>();
        internal Dictionary<int, SpecialDungeonKind> KindByDungeonId { get; } = new Dictionary<int, SpecialDungeonKind>();

        internal static SpecialDungeonRuntime CreateRuntime(int dungeonId)
        {
            var config = Cached.Value;
            if (!config.KindByDungeonId.TryGetValue(dungeonId, out var kind))
                return null;

            return new SpecialDungeonRuntime(dungeonId, kind, config);
        }

        private static SpecialDungeonModuleConfig Load()
        {
            var config = new SpecialDungeonModuleConfig();

            try
            {
                var text = PvfArchiveAccessor.ReadText("Etc/GameMode/SpecialDungeonModule.etc");
                var root = new ScriptParser().Parse(text);

                ParseSeaChase(root.GetChild("sea chase"), text, config.SeaChase);
                ParseTimeCrack(root.GetChild("time crack"), text, config.TimeCrack);
                ParseSeizeMoney(root.GetChild("seize money"), text, config.SeizeMoney);
                ParseSealForest(root.GetChild("seal forest"), text, config.SealForest);
                ParseTimerInfo(root.GetChild("etc"), text, config.TimerSecondsByDungeonId);
                ParseTimerInfo(root, text, config.TimerSecondsByDungeonId);
                ParseTimerInfoFromText(text, config.TimerSecondsByDungeonId);
                ParseDungeonKinds(config.KindByDungeonId);

                FileLogger.Log($"[SpecialDungeonModule] Loaded SpecialDungeonModule.etc: seaChaseSuccessBuffs={config.SeaChase.SuccessBuffIds.Count} seaChaseFailBuffs={config.SeaChase.FailBuffIds.Count} timeCrackBuffs={config.TimeCrack.BuffWeights.Count} timeCrackSandGauge={config.TimeCrack.SandGaugeMax}/{config.TimeCrack.SandGaugeGainOnKill}/{config.TimeCrack.SandGaugeGainOnChampion} seizeGauge={config.SeizeMoney.GaugeMax} sealBuffs={config.SealForest.BuffsByMonsterCode.Count} specialDungeons={config.KindByDungeonId.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SpecialDungeonModule] ERROR loading SpecialDungeonModule.etc: {ex.Message}");
            }

            return config;
        }

        private static void ParseSeaChase(ScriptNode node, string text, SeaChaseConfig config)
        {
            if (node == null || config == null)
                return;

            config.PassEndPos = ReadInt(node, text, "pass end pos", 0);
            ReadIntList(node, text, "success buff", config.SuccessBuffIds);
            ReadIntList(node, text, "fail buff", config.FailBuffIds);

            var notice = node.GetChild("buff notice string");
            if (notice == null)
                return;

            var tokens = Tokenize(notice.GetFirstDataContent(text));
            for (var i = 0; i + 3 < tokens.Count; i += 4)
            {
                if (!int.TryParse(tokens[i], out var buffId))
                    continue;

                config.BuffNotices[buffId] = new SeaChaseBuffNotice(
                    buffId,
                    tokens[i + 1],
                    tokens[i + 2],
                    tokens[i + 3]);
            }
        }

        private static void ParseTimeCrack(ScriptNode node, string text, TimeCrackConfig config)
        {
            if (node == null || config == null)
                return;

            ReadIntList(node, text, "InvincibleMonster", config.InvincibleMonsterCodes);

            config.BuffWeights.Clear();
            var buffWeight = node.GetChild("buff weight");
            var buffTokens = Tokenize(buffWeight?.GetFirstDataContent(text));
            for (var i = 0; i + 1 < buffTokens.Count; i += 2)
            {
                if (int.TryParse(buffTokens[i], out var buffId)
                    && int.TryParse(buffTokens[i + 1], out var weight)
                    && buffId > 0
                    && weight > 0)
                {
                    config.BuffWeights.Add(new TimeCrackBuffWeight(buffId, weight));
                }
            }

            var gauge = node.GetChild("sand gauge");
            var gaugeTokens = Tokenize(gauge?.GetFirstDataContent(text));
            if (gaugeTokens.Count > 0 && int.TryParse(gaugeTokens[0], out var max) && max > 0)
                config.SandGaugeMax = max;
            if (gaugeTokens.Count > 1 && int.TryParse(gaugeTokens[1], out var gain) && gain > 0)
                config.SandGaugeGainOnKill = gain;
            if (gaugeTokens.Count > 2 && int.TryParse(gaugeTokens[2], out var value3) && value3 > 0)
                config.SandGaugeGainOnChampion = value3;
        }

        private static void ParseSeizeMoney(ScriptNode node, string text, SeizeMoneyConfig config)
        {
            if (node == null)
                return;

            config.GaugeMax = ReadInt(node, text, "gauge max", 1000);
            config.GaugeSubOnDamage = ReadInt(node, text, "gauge sub on damage", 100);
            config.GaugeValueToMoveHiddenMap = ReadInt(node, text, "gauge value to move hidden map", 1);
            config.NoticeTextOnHit = ReadBacktickText(node, text, "notice text on hit");
            config.NoticeTextOnHitTermMs = ReadInt(node, text, "notice text on hit time term", 10000);
            config.CreateGoldBallNumOnHitStatue = ReadInt(node, text, "create gold ball num on hit statue", 3);
        }

        private static void ParseSealForest(ScriptNode node, string text, SealForestConfig config)
        {
            var addBuff = node?.GetChild("add buff");
            if (addBuff == null)
                return;

            var tokens = Tokenize(addBuff.GetFirstDataContent(text));
            for (var i = 0; i + 4 < tokens.Count;)
            {
                if (!int.TryParse(tokens[i], out var monsterCode)
                    || !int.TryParse(tokens[i + 1], out var buffId))
                {
                    i++;
                    continue;
                }

                config.BuffsByMonsterCode[monsterCode] = new SealForestBuffEntry(
                    monsterCode,
                    buffId,
                    tokens[i + 2],
                    tokens[i + 3],
                    tokens[i + 4]);
                i += 5;
            }
        }

        private static void ParseTimerInfo(ScriptNode etcNode, string text, Dictionary<int, int> timers)
        {
            var timerInfo = etcNode?.GetChild("timer info");
            if (timerInfo == null)
                return;

            var tokens = Tokenize(timerInfo.GetFirstDataContent(text));
            ParseTimerTokens(tokens, timers);
        }

        private static void ParseTimerInfoFromText(string text, Dictionary<int, int> timers)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var match = Regex.Match(text, @"(?im)^\s*\[timer info\]\s*(?<inline>[^\r\n]*)$");
            if (!match.Success)
                return;

            var lines = new List<string>();
            var inline = match.Groups["inline"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inline))
                lines.Add(inline);

            var lineStart = text.IndexOf('\n', match.Index + match.Length);
            if (lineStart >= 0)
            {
                lineStart++;
                var pos = lineStart;
                while (pos < text.Length)
                {
                    var next = text.IndexOf('\n', pos);
                    if (next < 0)
                        next = text.Length;

                    var line = text.Substring(pos, next - pos).Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                        break;
                    if (line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        lines.Add(line);

                    pos = next + 1;
                }
            }

            ParseTimerTokens(Tokenize(string.Join(" ", lines)), timers);
        }

        private static void ParseTimerTokens(IReadOnlyList<string> tokens, Dictionary<int, int> timers)
        {
            for (var i = 0; i + 1 < tokens.Count; i += 2)
            {
                if (int.TryParse(tokens[i], out var dungeonId)
                    && int.TryParse(tokens[i + 1], out var seconds))
                    timers[dungeonId] = seconds;
            }
        }

        private static void ParseDungeonKinds(Dictionary<int, SpecialDungeonKind> kinds)
        {
            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("dungeon/dungeon.lst"));
            foreach (var entry in lst.Entries)
            {
                var path = (entry.FilePath ?? string.Empty).Replace('\\', '/');
                if (!path.StartsWith("Special/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var kind = ResolveKindFromPath(path);
                if (kind != SpecialDungeonKind.None)
                    kinds[entry.Id] = kind;
            }
        }

        private static SpecialDungeonKind ResolveKindFromPath(string path)
        {
            if (path.IndexOf("SeaChase", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SeaChase;
            if (path.IndexOf("TimeBreak", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.TimeCrack;
            if (path.IndexOf("Seizemoney", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SeizeMoney;
            if (path.IndexOf("SealForest", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.SealForest;
            if (path.IndexOf("GentInfiltrate", StringComparison.OrdinalIgnoreCase) >= 0)
                return SpecialDungeonKind.GentInfiltrate;
            return SpecialDungeonKind.None;
        }

        private static int ReadInt(ScriptNode parent, string text, string tag, int fallback)
        {
            var node = parent?.GetChild(tag);
            if (node == null)
                return fallback;

            return int.TryParse(node.GetFirstDataContent(text), out var value) ? value : fallback;
        }

        private static void ReadIntList(ScriptNode parent, string text, string tag, List<int> values)
        {
            values.Clear();
            var node = parent?.GetChild(tag);
            if (node == null)
                return;

            var tokens = Tokenize(node.GetFirstDataContent(text));
            for (var i = 0; i < tokens.Count; i++)
                if (int.TryParse(tokens[i], out var value))
                    values.Add(value);
        }

        private static string ReadBacktickText(ScriptNode parent, string text, string tag)
        {
            var node = parent?.GetChild(tag);
            if (node == null)
                return string.Empty;

            var tokens = Tokenize(node.GetFirstDataContent(text));
            return tokens.Count > 0 ? tokens[0] : string.Empty;
        }

        private static List<string> Tokenize(string value)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return tokens;

            foreach (Match match in Regex.Matches(value, "`([^`]*)`|\\S+"))
            {
                tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            }

            return tokens;
        }
    }

    internal sealed class SeizeMoneyConfig
    {
        internal int GaugeMax { get; set; }
        internal int GaugeSubOnDamage { get; set; }
        internal int GaugeValueToMoveHiddenMap { get; set; }
        internal string NoticeTextOnHit { get; set; }
        internal int NoticeTextOnHitTermMs { get; set; }
        internal int CreateGoldBallNumOnHitStatue { get; set; }
    }

    internal sealed class SeaChaseConfig
    {
        internal int PassEndPos { get; set; }
        internal List<int> SuccessBuffIds { get; } = new List<int>();
        internal List<int> FailBuffIds { get; } = new List<int>();
        internal Dictionary<int, SeaChaseBuffNotice> BuffNotices { get; } =
            new Dictionary<int, SeaChaseBuffNotice>();
    }

    internal sealed class SeaChaseBuffNotice
    {
        internal SeaChaseBuffNotice(int buffId, string messageA, string messageB, string color)
        {
            BuffId = buffId;
            MessageA = messageA ?? string.Empty;
            MessageB = messageB ?? string.Empty;
            Color = color ?? string.Empty;
        }

        internal int BuffId { get; }
        internal string MessageA { get; }
        internal string MessageB { get; }
        internal string Color { get; }
    }

    internal sealed class TimeCrackConfig
    {
        internal List<int> InvincibleMonsterCodes { get; } = new List<int>();
        internal List<TimeCrackBuffWeight> BuffWeights { get; } = new List<TimeCrackBuffWeight>();
        internal int SandGaugeMax { get; set; } = 100;
        internal int SandGaugeGainOnKill { get; set; } = 10;
        internal int SandGaugeGainOnChampion { get; set; } = 30;
    }

    internal sealed class TimeCrackBuffWeight
    {
        internal TimeCrackBuffWeight(int buffId, int weight)
        {
            BuffId = buffId;
            Weight = weight;
        }

        internal int BuffId { get; }
        internal int Weight { get; }
    }

    internal sealed class SealForestConfig
    {
        internal Dictionary<int, SealForestBuffEntry> BuffsByMonsterCode { get; } =
            new Dictionary<int, SealForestBuffEntry>();
    }

    internal sealed class SealForestBuffEntry
    {
        internal SealForestBuffEntry(int monsterCode, int buffId, string messageA, string messageB, string color)
        {
            MonsterCode = monsterCode;
            BuffId = buffId;
            MessageA = messageA ?? string.Empty;
            MessageB = messageB ?? string.Empty;
            Color = color ?? string.Empty;
        }

        internal int MonsterCode { get; }
        internal int BuffId { get; }
        internal string MessageA { get; }
        internal string MessageB { get; }
        internal string Color { get; }
    }
}
