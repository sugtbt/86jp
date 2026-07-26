using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class SpecialDungeonNotifier
    {
        private const byte SummonMonsterResult = 0x01;
        private const byte SummonMonsterMode = 0x03;
        private const byte StrongWarlordResult = 0x01;

        internal const ushort BossSummonRuntimeKey = 0x42DD;

        private readonly struct BossSummonRequest
        {
            internal BossSummonRequest(
                ushort conditionalType,
                int monsterCode,
                int stateId,
                int mapId,
                ushort conditionalParam0,
                ushort conditionalParam1,
                byte matchCount)
            {
                ConditionalType = conditionalType;
                MonsterCode = monsterCode;
                StateId = stateId;
                MapId = mapId;
                ConditionalParam0 = conditionalParam0;
                ConditionalParam1 = conditionalParam1;
                MatchCount = matchCount;
            }

            internal ushort ConditionalType { get; }
            internal int MonsterCode { get; }
            internal int StateId { get; }
            internal int MapId { get; }
            internal ushort ConditionalParam0 { get; }
            internal ushort ConditionalParam1 { get; }
            internal byte MatchCount { get; }
        }

        private readonly struct BossTemplate
        {
            internal BossTemplate(
                int mapId,
                int monsterCode,
                byte level,
                int localIndex)
            {
                MapId = mapId;
                MonsterCode = monsterCode;
                Level = level;
                LocalIndex = localIndex;
            }

            internal int MapId { get; }
            internal int MonsterCode { get; }
            internal byte Level { get; }
            internal int LocalIndex { get; }
        }

        internal static async Task ClearRunBuffsAsync(
            EnhancedClientSession session,
            string reason)
        {
            var special = session?.Player?.CurrentRun?.SpecialDungeon;
            if (special == null)
                return;

            List<int> buffIds;
            switch (special.Kind)
            {
                case SpecialDungeonKind.SealForest:
                    if (!special.TryConsumeSealForestBuffIds(out buffIds))
                        return;
                    break;

                case SpecialDungeonKind.SeaChase:
                    if (!special.TryConsumeSeaChaseAppliedBuffIds(out buffIds))
                        return;
                    break;

                case SpecialDungeonKind.TimeCrack:
                    if (!special.TryConsumeTimeCrackBuffIds(out buffIds))
                        return;
                    break;

                default:
                    return;
            }

            await DungeonBuffNotificationSender.ClearAsync(session, buffIds);

            FileLogger.Log(
                $"[SpecialDungeonModule] buffs cleared reason={reason} " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"kind={special.Kind} buffs=[{string.Join(",", buffIds)}]");
        }

        internal static async Task SendStartMapStateAsync(
            EnhancedClientSession session)
        {
            var special = session?.Player?.CurrentRun?.SpecialDungeon;
            if (special == null)
                return;

            if (special.Kind == SpecialDungeonKind.SeizeMoney)
                await SendGaugeAsync(session, special.SeizeMoneyGauge, "seize_money");
            else if (special.Kind == SpecialDungeonKind.TimeCrack)
                await SendGaugeAsync(session, special.TimeCrackGauge, "time_crack");
        }

        internal static async Task SendBossEntranceMinimapIconInfoAsync(
            EnhancedClientSession session,
            string reason)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !run.HasBossEntranceConditionalSummon)
            {
                return;
            }

            var entries = new List<(byte X, byte Y, int MonsterCode)>();
            foreach (var target in run.BossEntranceConditionTargets)
            {
                if (target != null && target.MonsterCode > 0)
                {
                    entries.Add((
                        target.X,
                        target.Y,
                        target.MonsterCode));
                }
            }

            if (entries.Count == 0)
                return;

            var body =
                SpecialDungeonNotificationBuilder.BuildMinimapIconInfo(entries);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.MINIMAP_ICON_INFO,
                body));
            FileLogger.Log(
                $"[SpecialDungeonModule] condition minimap sent reason={reason} " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"mechanism=boss-entrance-condition entries={entries.Count}");
        }

        internal static async Task ObserveMonsterKilledAsync(
            EnhancedClientSession session,
            int monsterCode,
            byte monsterType)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || monsterCode <= 0)
                return;

            if (run.SpecialDungeon != null)
            {
                await TryApplySealForestBuffAsync(session, monsterCode);
                await TryAdvanceTimeCrackAsync(session, monsterCode, monsterType);
            }

            await TryAdvanceBossEntranceConditionAsync(session, monsterCode);
            if (run.SpecialDungeon != null)
                await TryAdvanceGentInfiltrateAsync(session, monsterCode);
        }

        internal static async Task HandleBossSummonRequestAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !run.HasBossEntranceConditionalSummon
                || !run.BossEntranceConditionComplete)
            {
                return;
            }

            if (!TryParseBossSummonRequest(body, out var request)
                || !TryFindBossTemplate(run, out var template)
                || request.MapId != template.MapId
                || request.MonsterCode != template.MonsterCode)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss summon request rejected: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"body={(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            lock (run.SyncRoot)
            {
                if (run.ConditionalBossSpawned)
                    return;
                run.ConditionalBossSpawned = true;
                run.ConditionalBossCode = template.MonsterCode;
            }

            var level = template.Level > 0
                ? (ushort)template.Level
                : ResolveDungeonLevel(run.DungeonId);
            var response =
                SpecialDungeonNotificationBuilder
                    .BuildSummonMonsterCommandCreateResponse(
                        SummonMonsterResult,
                        request.StateId,
                        1,
                        BossSummonRuntimeKey,
                        template.MonsterCode,
                        SummonMonsterMode,
                        level);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.SUMMON_MONSTER,
                response));

            FileLogger.Log(
                $"[SpecialDungeonModule] boss summon response sent: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"mechanism=boss-entrance-condition map={template.MapId} " +
                $"monster={template.MonsterCode} local={template.LocalIndex} " +
                $"level={level} state={request.StateId} " +
                $"key={BossSummonRuntimeKey}");
        }

        internal static Task HandleGentInfiltrateTimerModifyInfoAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            FileLogger.Log(
                $"[SpecialDungeonModule] TIMER_MODIFY_INFO received: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"dungeon={run?.DungeonId ?? 0} kind={special?.Kind.ToString() ?? "none"} " +
                $"body={(body != null ? BitConverter.ToString(body) : "null")}");
            return Task.CompletedTask;
        }

        internal static async Task HandleSeaChaseMiniGameResultAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var special = session?.Player?.CurrentRun?.SpecialDungeon;
            if (special == null || special.Kind != SpecialDungeonKind.SeaChase)
                return;

            var result = body != null && body.Length >= 4
                ? BitConverter.ToInt32(body, 0)
                : 0;
            var succeeded = result != 0;
            var firstResult = !special.SeaChaseMiniGameSucceeded.HasValue;
            special.NoteSeaChaseMiniGameResult(succeeded);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                CommonPacketBodyBuilder.BuildSuccessAck()));

            if (firstResult)
                await SendSeaChaseBuffsAsync(session, succeeded);

            FileLogger.Log(
                $"[SpecialDungeonModule] SEA_CHASE result: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"result={result} succeeded={succeeded} first={firstResult}");
        }

        internal static Task ObserveSeaChasePacketAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            FileLogger.Log(
                $"[SpecialDungeonModule] SEA_CHASE observe: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"dungeon={run?.DungeonId ?? 0} " +
                $"kind={special?.Kind.ToString() ?? "none"} " +
                $"type=0x{header.type:X4} " +
                $"body={(body != null ? BitConverter.ToString(body) : "null")}");
            return Task.CompletedTask;
        }

        internal static Task MarkGentInfiltrateTimeoutAsync(
            EnhancedClientSession session,
            string source)
        {
            var special = session?.Player?.CurrentRun?.SpecialDungeon;
            if (special == null
                || special.Kind != SpecialDungeonKind.GentInfiltrate)
            {
                return Task.CompletedTask;
            }

            special.TryCompleteGentInfiltrateByTimer(
                out var destroyed,
                out var required);
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE timeout: " +
                $"source={source} cid={session.Player.CharacterId} " +
                $"dungeon={special.DungeonId} progress={destroyed}/{required} " +
                $"action=mark_timeout_wait_four_towers");
            return Task.CompletedTask;
        }

        internal static bool TryPickTimeCrackBuff(
            SpecialDungeonRuntime special,
            DnfLcg lcg,
            out int buffId,
            out int roll,
            out int totalWeight,
            out string pickMode)
        {
            buffId = 0;
            roll = 0;
            totalWeight = 0;
            pickMode = "none";

            var weights = special?.Config?.TimeCrack?.BuffWeights;
            if (weights == null || weights.Count == 0)
                return false;

            var candidates = new List<TimeCrackBuffWeight>();
            foreach (var entry in weights)
            {
                if (entry.BuffId > 0
                    && entry.Weight > 0
                    && !Contains(special.TimeCrackBuffIds, entry.BuffId))
                {
                    candidates.Add(entry);
                }
            }

            if (candidates.Count > 0)
            {
                pickMode = "missing_first";
            }
            else
            {
                pickMode = "refresh_all";
                foreach (var entry in weights)
                {
                    if (entry.BuffId > 0 && entry.Weight > 0)
                        candidates.Add(entry);
                }
            }

            foreach (var entry in candidates)
                totalWeight += entry.Weight;
            if (totalWeight <= 0)
                return false;

            roll = lcg != null
                ? lcg.Next(totalWeight)
                : ServerRandom.Next(totalWeight);
            var cursor = roll;
            foreach (var entry in candidates)
            {
                if (cursor < entry.Weight)
                {
                    buffId = entry.BuffId;
                    return true;
                }
                cursor -= entry.Weight;
            }

            buffId = candidates[candidates.Count - 1].BuffId;
            return buffId > 0;
        }

        private static async Task TryApplySealForestBuffAsync(
            EnhancedClientSession session,
            int monsterCode)
        {
            var special = session.Player.CurrentRun.SpecialDungeon;
            if (!special.TryMarkSealForestBuffMonster(monsterCode, out var entry))
                return;

            await DungeonBuffNotificationSender.SendAddedAndActivateAsync(
                session,
                entry.BuffId,
                special.SealForestBuffIds);
            FileLogger.Log(
                $"[SpecialDungeonModule] SEAL_FOREST buff: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"monster={monsterCode} buff={entry.BuffId}");
        }

        private static async Task TryAdvanceTimeCrackAsync(
            EnhancedClientSession session,
            int monsterCode,
            byte monsterType)
        {
            var run = session.Player.CurrentRun;
            var special = run.SpecialDungeon;
            if (special.Kind != SpecialDungeonKind.TimeCrack
                || !special.TryAddTimeCrackGauge(
                    monsterCode,
                    monsterType == 1,
                    out var previous,
                    out var current,
                    out var delta,
                    out var filled))
            {
                return;
            }

            await SendGaugeAsync(session, current, "time_crack_kill");
            FileLogger.Log(
                $"[SpecialDungeonModule] TIME_CRACK gauge: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"monster={monsterCode} type={monsterType} " +
                $"value={previous}+{delta}->{current} filled={filled}");

            if (!filled
                || !TryPickTimeCrackBuff(
                    special,
                    run.RoomLcg,
                    out var buffId,
                    out var roll,
                    out var totalWeight,
                    out var pickMode))
            {
                return;
            }

            special.NoteTimeCrackBuffApplied(buffId);
            await DungeonBuffNotificationSender.SendAddedAndActivateAsync(
                session,
                buffId,
                special.TimeCrackBuffIds);
            special.ResetTimeCrackGauge();
            await SendGaugeAsync(session, 0, "time_crack_reset");

            FileLogger.Log(
                $"[SpecialDungeonModule] TIME_CRACK buff: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"buff={buffId} roll={roll}/{totalWeight} mode={pickMode} " +
                $"active=[{string.Join(",", special.TimeCrackBuffIds)}]");
        }

        private static async Task TryAdvanceBossEntranceConditionAsync(
            EnhancedClientSession session,
            int monsterCode)
        {
            var run = session.Player.CurrentRun;
            if (!run.HasBossEntranceConditionalSummon)
            {
                return;
            }

            var matched = false;
            var completed = 0;
            var total = 0;
            lock (run.SyncRoot)
            {
                foreach (var target in run.BossEntranceConditionTargets)
                {
                    if (target == null)
                        continue;

                    total++;
                    if (!matched
                        && !target.Completed
                        && target.MonsterCode == monsterCode
                        && target.X == run.RoomKey.X
                        && target.Y == run.RoomKey.Y)
                    {
                        target.Completed = true;
                        matched = true;
                    }

                    if (target.Completed)
                        completed++;
                }

                if (matched && total > 0 && completed >= total)
                    run.BossEntranceConditionComplete = true;
            }

            if (!matched)
                return;

            FileLogger.Log(
                $"[SpecialDungeonModule] condition target killed: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"mechanism=boss-entrance-condition monster={monsterCode} " +
                $"progress={completed}/{total}");

            if (total > 0 && completed >= total)
                await SendPassGateAsync(session, "boss_entrance_complete");
        }

        private static async Task TryAdvanceGentInfiltrateAsync(
            EnhancedClientSession session,
            int monsterCode)
        {
            var special = session.Player.CurrentRun.SpecialDungeon;
            if (special.Kind != SpecialDungeonKind.GentInfiltrate
                || !special.TryMarkGentInfiltrateTowerDestroyed(
                    monsterCode,
                    out var destroyed,
                    out var required,
                    out var totalDestroyed,
                    out var totalRequired,
                    out var completed))
            {
                return;
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE tower: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"monster={monsterCode} progress={destroyed}/{required} " +
                $"total={totalDestroyed}/{totalRequired} completed={completed} " +
                $"timedOut={special.GentInfiltrateTimedOut}");
            if (!completed)
                return;

            DungeonMechanismTimerCoordinator.Cancel(session);
            await SendPassGateAsync(session, "gent_four_towers");
            if (!special.GentInfiltrateStrongWarlord)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.TIMER_MODIFY_INFO,
                new[] { StrongWarlordResult }));
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE strong warlord: " +
                $"cid={session.Player.CharacterId} dungeon={special.DungeonId} " +
                $"cmd=1 type=0x{(ushort)CmdPacketType.TIMER_MODIFY_INFO:X4} body=01");
        }

        private static async Task SendSeaChaseBuffsAsync(
            EnhancedClientSession session,
            bool succeeded)
        {
            var special = session.Player.CurrentRun.SpecialDungeon;
            var buffIds = succeeded
                ? special.Config.SeaChase.SuccessBuffIds
                : special.Config.SeaChase.FailBuffIds;

            await DungeonBuffNotificationSender.SendAddedAndActivateAsync(
                session,
                buffIds,
                buffIds);
            special.NoteSeaChaseBuffsApplied(buffIds);
        }

        private static async Task SendGaugeAsync(
            EnhancedClientSession session,
            int value,
            string reason)
        {
            var body =
                SpecialDungeonNotificationBuilder.BuildGaugeObjectBarData(value);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.GAUGE_OBJECT_BAR_DATA,
                body));
            FileLogger.Log(
                $"[SpecialDungeonModule] gauge sent reason={reason} " +
                $"cid={session.Player.CharacterId} value={value}");
        }

        private static async Task SendPassGateAsync(
            EnhancedClientSession session,
            string reason)
        {
            await DungeonMechanismNotificationSender
                .SendCompleteConditionPassGateAsync(
                    session,
                    "ordinary-special-dungeon",
                    reason);
        }

        private static bool TryFindBossTemplate(
            DungeonRun run,
            out BossTemplate template)
        {
            template = default;
            var codes = run.BossEntranceConditionalSummonCodes;
            if (codes.Count == 0)
                return false;

            lock (run.SyncRoot)
            {
                if (run.RoomStates == null
                    || !run.RoomStates.TryGetValue(
                        run.RoomKey,
                        out var roomState)
                    || roomState == null
                    || roomState.Maze.Monsters == null)
                {
                    return false;
                }

                for (var i = 0; i < roomState.Maze.Monsters.Count; i++)
                {
                    var monster = roomState.Maze.Monsters[i];
                    if (monster.Flag0 == 0 || !codes.Contains(monster.Code))
                        continue;

                    template = new BossTemplate(
                        roomState.Maze.Index,
                        monster.Code,
                        monster.Level,
                        i);
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseBossSummonRequest(
            byte[] body,
            out BossSummonRequest request)
        {
            request = default;
            if (body == null || body.Length < 19)
                return false;

            request = new BossSummonRequest(
                BitConverter.ToUInt16(body, 0),
                BitConverter.ToInt32(body, 2),
                BitConverter.ToInt32(body, 6),
                BitConverter.ToInt32(body, 10),
                BitConverter.ToUInt16(body, 14),
                BitConverter.ToUInt16(body, 16),
                body[18]);
            return request.MonsterCode > 0
                && request.StateId > 0
                && request.MapId > 0
                && request.MatchCount > 0;
        }

        private static ushort ResolveDungeonLevel(int dungeonId)
        {
            try
            {
                return (ushort)Math.Max(
                    1,
                    Math.Min(
                        ushort.MaxValue,
                        (int)GameWorld.Dungeon.GetDungeonBasicLv(dungeonId)));
            }
            catch
            {
                return 1;
            }
        }

        private static bool Contains(
            IReadOnlyList<int> values,
            int value)
        {
            if (values == null)
                return false;

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                    return true;
            }
            return false;
        }
    }
}
