using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class SpecialDungeonSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_DUNGEON selftest ===");
            var failures = 0;

            TestPacketBodies(ref failures);
            TestBossDieCheckParser(ref failures);
            TestTimeCrack(ref failures);
            TestGentInfiltrate(ref failures);
            TestSeizeMoney(ref failures);
            TestClearMapGroups(ref failures);
            TestPvfBackedData(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestPacketBodies(ref int failures)
        {
            var passGate =
                SpecialDungeonNotificationBuilder
                    .BuildCompleteConditionPassGateTrigger();
            Check(
                "0x0138 body is five zero bytes",
                BytesEqual(passGate, 0x00, 0x00, 0x00, 0x00, 0x00),
                ref failures);

            var summon =
                SpecialDungeonNotificationBuilder
                    .BuildSummonMonsterCommandCreateResponse(
                        result: 0x01,
                        state: 0x01020304,
                        count: 0x01,
                        key: SpecialDungeonNotifier.BossSummonRuntimeKey,
                        monsterCode: 0x11223344,
                        mode: 0x03,
                        paramA: 0x5566);
            Check(
                "0x0211 command response layout and runtime key",
                BytesEqual(
                    summon,
                    0x01,
                    0x04, 0x03, 0x02, 0x01,
                    0x01,
                    0xDD, 0x42,
                    0x44, 0x33, 0x22, 0x11,
                    0x03,
                    0x66, 0x55),
                ref failures);
        }

        private static void TestBossDieCheckParser(ref int failures)
        {
            var parsed = BossDieCheckRequest.TryParse(
                new byte[] { 0x34, 0x12, 0x78, 0x56 },
                out var request);
            Check(
                "BOSS_DIE_CHECK parses little-endian user and sequence",
                parsed
                    && request.UserId == 0x1234
                    && request.BossSequence == 0x5678,
                ref failures);
            Check(
                "BOSS_DIE_CHECK rejects truncated body",
                !BossDieCheckRequest.TryParse(
                    new byte[] { 0x34, 0x12, 0x78 },
                    out _),
                ref failures);

            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                var run = new DungeonRun(900, 0)
                {
                    BossEntranceConditionComplete = true,
                    ConditionalBossSpawned = true,
                    ConditionalBossCode = 222,
                };
                run.BossEntranceConditionTargets.Add(
                    new BossEntranceConditionTargetState
                    {
                        MonsterCode = 100,
                        X = 1,
                        Y = 1,
                        Completed = true,
                    });
                run.BossEntranceConditionalSummonCodes.Add(111);
                run.BossEntranceConditionalSummonCodes.Add(222);
                session.Player.CurrentRun = run;

                var clearRequest = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    new BossDieCheckRequest(
                        userId: 1,
                        bossSequence: SpecialDungeonNotifier.BossSummonRuntimeKey));
                Check(
                    "BOSS_DIE_CHECK uses the Boss actually spawned in this run",
                    clearRequest.ShouldClearDungeon
                        && clearRequest.BossCode == 222,
                    ref failures);
            }
        }

        private static void TestTimeCrack(ref int failures)
        {
            var config = new SpecialDungeonModuleConfig();
            config.TimeCrack.SandGaugeMax = 100;
            config.TimeCrack.SandGaugeGainOnKill = 10;
            config.TimeCrack.SandGaugeGainOnChampion = 30;
            config.TimeCrack.InvincibleMonsterCodes.Add(9000);
            config.TimeCrack.BuffWeights.Add(
                new TimeCrackBuffWeight(101, 1));
            config.TimeCrack.BuffWeights.Add(
                new TimeCrackBuffWeight(202, 100));

            var special = new SpecialDungeonRuntime(
                1000,
                SpecialDungeonKind.TimeCrack,
                config);

            var advanced = special.TryAddTimeCrackGauge(
                monsterCode: 100,
                isChampion: true,
                out var previous,
                out var current,
                out var delta,
                out var filled);
            Check(
                "TimeCrack champion grants configured 30 energy",
                advanced
                    && previous == 0
                    && current == 30
                    && delta == 30
                    && !filled,
                ref failures);

            Check(
                "TimeCrack invincible monster grants no energy",
                !special.TryAddTimeCrackGauge(
                    monsterCode: 9000,
                    isChampion: false,
                    out _,
                    out _,
                    out _,
                    out _)
                    && special.TimeCrackGauge == 30,
                ref failures);

            special.NoteTimeCrackBuffApplied(101);
            var picked = SpecialDungeonNotifier.TryPickTimeCrackBuff(
                special,
                new DnfLcg(1),
                out var buffId,
                out _,
                out var totalWeight,
                out var pickMode);
            Check(
                "TimeCrack weighted selection prefers missing buffs",
                picked
                    && buffId == 202
                    && totalWeight == 100
                    && pickMode == "missing_first",
                ref failures);

            special.NoteTimeCrackBuffApplied(202);
            picked = SpecialDungeonNotifier.TryPickTimeCrackBuff(
                special,
                new DnfLcg(1),
                out buffId,
                out _,
                out totalWeight,
                out pickMode);
            Check(
                "TimeCrack refreshes full weighted table after all buffs",
                picked
                    && (buffId == 101 || buffId == 202)
                    && totalWeight == 101
                    && pickMode == "refresh_all",
                ref failures);
        }

        private static void TestGentInfiltrate(ref int failures)
        {
            const string condition =
                "`[hunt monster]` 4 " +
                "1001 0 1 " +
                "1002 0 1 " +
                "1003 0 1 " +
                "1004 0 1";

            var withinTime = new SpecialDungeonRuntime(
                2000,
                SpecialDungeonKind.GentInfiltrate,
                new SpecialDungeonModuleConfig());
            withinTime.ConfigureGentInfiltrateBossEntrance(
                condition,
                timerSeconds: 180);

            var completed = false;
            for (var code = 1001; code <= 1004; code++)
            {
                withinTime.TryMarkGentInfiltrateTowerDestroyed(
                    code,
                    out _,
                    out _,
                    out _,
                    out _,
                    out completed);
            }

            Check(
                "Gent four towers within time enables strong Warlord path",
                completed
                    && withinTime.GentInfiltrateConditionComplete
                    && withinTime.GentInfiltrateStrongWarlord
                    && !withinTime.GentInfiltrateTimedOut,
                ref failures);

            var timedOut = new SpecialDungeonRuntime(
                2000,
                SpecialDungeonKind.GentInfiltrate,
                new SpecialDungeonModuleConfig());
            timedOut.ConfigureGentInfiltrateBossEntrance(
                condition,
                timerSeconds: 180);
            timedOut.TryCompleteGentInfiltrateByTimer(out _, out _);

            completed = false;
            for (var code = 1001; code <= 1004; code++)
            {
                timedOut.TryMarkGentInfiltrateTowerDestroyed(
                    code,
                    out _,
                    out _,
                    out _,
                    out _,
                    out completed);
            }

            Check(
                "Gent timeout still waits for four towers without strong Warlord",
                completed
                    && timedOut.GentInfiltrateConditionComplete
                    && !timedOut.GentInfiltrateStrongWarlord
                    && timedOut.GentInfiltrateTimedOut,
                ref failures);
        }

        private static void TestSeizeMoney(ref int failures)
        {
            var config = new SpecialDungeonModuleConfig();
            config.SeizeMoney.GaugeMax = 1000;
            config.SeizeMoney.GaugeSubOnDamage = 100;

            var special = new SpecialDungeonRuntime(
                3000,
                SpecialDungeonKind.SeizeMoney,
                config);
            var reserved = special.TryReserveSeizeMoneyClearReward(
                remainingGoldUnits: 5,
                maxDropCount: 4,
                out var count,
                out var gauge);
            Check(
                "SeizeMoney scales clear reward from remaining gauge",
                reserved && count == 2 && gauge == 500,
                ref failures);

            var reservedAgain = special.TryReserveSeizeMoneyClearReward(
                remainingGoldUnits: 10,
                maxDropCount: 4,
                out count,
                out gauge);
            Check(
                "SeizeMoney clear reward reservation is one-shot",
                !reservedAgain && count == 0 && gauge == 500,
                ref failures);
        }

        private static void TestPvfBackedData(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed special dungeon checks: Script.pvf not found");
                return;
            }

            var gent =
                SpecialDungeonModuleConfig.CreateRuntime(2005);
            var sea =
                SpecialDungeonModuleConfig.CreateRuntime(2006);
            var timeCrack =
                SpecialDungeonModuleConfig.CreateRuntime(2007);
            var sealForest =
                SpecialDungeonModuleConfig.CreateRuntime(2009);
            var seizeMoney =
                SpecialDungeonModuleConfig.CreateRuntime(2011);

            Check(
                "PVF identifies all stateful part-one special dungeon kinds",
                gent?.Kind == SpecialDungeonKind.GentInfiltrate
                    && sea?.Kind == SpecialDungeonKind.SeaChase
                    && timeCrack?.Kind == SpecialDungeonKind.TimeCrack
                    && sealForest?.Kind == SpecialDungeonKind.SealForest
                    && seizeMoney?.Kind == SpecialDungeonKind.SeizeMoney,
                ref failures);

            Check(
                "PVF loads core ETC settings for part-one dungeons",
                gent?.GentInfiltrateTimerSeconds == 300
                    && sea?.Config.SeaChase.SuccessBuffIds.Count > 0
                    && timeCrack?.Config.TimeCrack.BuffWeights.Count > 0
                    && timeCrack.Config.TimeCrack.SandGaugeGainOnChampion == 30
                    && sealForest?.Config.SealForest.BuffsByMonsterCode.Count > 0
                    && seizeMoney?.Config.SeizeMoney.GaugeMax > 0,
                ref failures);

            var meltdownMaze = GameWorld.Dungeon.GetDungeonMaze(2010, 0);
            var meltdownRun = new DungeonRun(2010, 0) { MazeIndex = 0 };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                meltdownRun,
                meltdownMaze,
                meltdownMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "PVF hunt+summon condition enables generic Boss entrance mechanism",
                meltdownRun.HasBossEntranceConditionalSummon
                    && meltdownRun.BossEntranceConditionTargets.Count == 3
                    && meltdownRun.BossEntranceConditionalSummonCodes
                        .SequenceEqual(new[] { 69264 }),
                ref failures);

            var stationMaze = GameWorld.Dungeon.GetDungeonMaze(2016, 0);
            var stationRun = new DungeonRun(2016, 0) { MazeIndex = 0 };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                stationRun,
                stationMaze,
                stationMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "same PVF condition enables StationEscape without a dungeon-kind branch",
                stationRun.HasBossEntranceConditionalSummon
                    && stationRun.BossEntranceConditionTargets.Count == 4
                    && stationRun.BossEntranceConditionTargets.TrueForAll(
                        target => target.MonsterCode == 61574)
                    && stationRun.BossEntranceConditionalSummonCodes
                        .SequenceEqual(new[] { 56616 }),
                ref failures);

            var gentMaze = GameWorld.Dungeon.GetDungeonMaze(2005, 0);
            var gentRun = new DungeonRun(2005, 0)
            {
                MazeIndex = 0,
                SpecialDungeon = gent,
            };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                gentRun,
                gentMaze,
                gentMaze.BossMap,
                Array.Empty<ActiveQuest>());
            Check(
                "hunt-only Boss entrance condition does not enable conditional summon",
                !gentRun.HasBossEntranceConditionalSummon,
                ref failures);

            Check(
                "SeizeMoney reward resolves from the Boss independent drop",
                IndependentDropSystem.TryResolveSingleGuaranteedFixedDrop(
                    monsterCode: 56631,
                    difficulty: 0,
                    dungeonLevel: 75,
                    out var seizeMoneyItemId,
                    out var seizeMoneyMaxCount)
                    && seizeMoneyItemId == 10089565
                    && seizeMoneyMaxCount == 4,
                ref failures);

            var gentClearConditions =
                GameWorld.Dungeon.GetDungeonFile(2005).Mazes[0].ClearConditions;
            var seaClearConditions =
                GameWorld.Dungeon.GetDungeonFile(2006).Mazes[0].ClearConditions;
            Check(
                "PVF Gent alternate final rooms share one-of-two clear group",
                IsClearMapGroup(
                    gentClearConditions,
                    required: 1,
                    17068,
                    17069),
                ref failures);
            Check(
                "PVF SeaChase alternate final rooms share one-of-two clear group",
                IsClearMapGroup(
                    seaClearConditions,
                    required: 1,
                    17080,
                    17083),
                ref failures);

            var gentClearState = new ClearConditionState(gentClearConditions);
            var seaClearState = new ClearConditionState(seaClearConditions);
            Check(
                "Gent non-endpoint final map satisfies explicit clear condition",
                gentClearState.TotalRequired == 1
                    && gentClearState.Check(1, 17069),
                ref failures);
            Check(
                "SeaChase double-Boss map satisfies explicit clear condition",
                seaClearState.TotalRequired == 1
                    && seaClearState.Check(1, 17083),
                ref failures);

            var seaHiddenRoom =
                GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 2006,
                    x: 0,
                    y: 1,
                    mazeIndex: 0,
                    overrideMapId: 17083);
            var seaBossCodes = new HashSet<int>();
            var seaBlockingBossCount = 0;
            foreach (var monster in seaHiddenRoom.Monsters)
            {
                if (!monster.IsBlocking)
                    continue;

                seaBlockingBossCount++;
                seaBossCodes.Add(monster.Code);
            }
            Check(
                "PVF SeaChase hidden room registers both blocking Bosses",
                seaBlockingBossCount == 2
                    && seaBossCodes.SetEquals(new[] { 69225, 69261 }),
                ref failures);

            var seaRoomRun = new DungeonRun(2006, 0)
            {
                RoomMonsters = seaHiddenRoom.Monsters,
                RoomStartSequence = 300,
            };
            seaRoomRun.RoomKilledSeqIds.Add(300);
            var oneBossCleared = DungeonRoomTopology.ComputeRoomClearedLocked(
                seaRoomRun,
                out var seaBlockingCount,
                out var oneBossKilledCount);
            seaRoomRun.RoomKilledSeqIds.Add(301);
            var bothBossesCleared = DungeonRoomTopology.ComputeRoomClearedLocked(
                seaRoomRun,
                out _,
                out var bothBossesKilledCount);
            Check(
                "SeaChase hidden room clears only after both Bosses die",
                seaBlockingCount == 2
                    && oneBossKilledCount == 1
                    && !oneBossCleared
                    && bothBossesKilledCount == 2
                    && bothBossesCleared,
                ref failures);

            var conditionActors =
                GameWorld.Dungeon.GetMapMonsterConditionSummaryInformation(
                    mapId: 17120,
                    dungeonId: 2010,
                    x: 3,
                    y: 3,
                    monsterCodes: new[] { 56611 });
            Check(
                "PVF condition target is visible and blocks room progress",
                conditionActors.Count > 0
                    && conditionActors[0].Code == 56611
                    && conditionActors[0].IsBlocking
                    && conditionActors[0].Flag0 == 0
                    && conditionActors[0].PacketIndex.HasValue,
                ref failures);

            var bossTemplates =
                GameWorld.Dungeon.GetMapConditionalSummonSummaryInformation(
                    mapId: 17120,
                    dungeonId: 2010,
                    x: 3,
                    y: 3,
                    monsterCodes: new[] { 69264 });
            Check(
                "PVF hidden Boss template preserves line, position and level",
                bossTemplates.Count > 0
                    && bossTemplates[0].Code == 69264
                    && bossTemplates[0].Type == 3
                    && !bossTemplates[0].IsBlocking
                    && bossTemplates[0].Flag0 == 1
                    && bossTemplates[0].TemplateOrder == 6
                    && bossTemplates[0].X > 100
                    && bossTemplates[0].Y > 100,
                ref failures);

            var stationBossTemplates =
                GameWorld.Dungeon.GetMapConditionalSummonSummaryInformation(
                    mapId: 11057,
                    dungeonId: 2016,
                    x: 1,
                    y: 0,
                    monsterCodes: new[] { 56616 });
            Check(
                "PVF StationEscape Boss template inherits dungeon level",
                stationBossTemplates.Count > 0
                    && stationBossTemplates[0].Code == 56616
                    && stationBossTemplates[0].Type == 3
                    && stationBossTemplates[0].Level == 86,
                ref failures);

            using (var client = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader());
                var run = new DungeonRun(2010, 0);
                run.BossEntranceConditionTargets.Add(
                    new BossEntranceConditionTargetState
                    {
                        MonsterCode = 56611,
                        X = 3,
                        Y = 3,
                    });
                run.BossEntranceConditionalSummonCodes.Add(69264);
                session.Player.CurrentRun = run;

                var startMap =
                    GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 2010,
                        x: 3,
                        y: 3,
                        mazeIndex: 0,
                        overrideMapId: 17120);
                SpecialDungeonRunCoordinator.AppendStartMapActors(
                    session,
                    startMap);

                var bossIndex =
                    startMap.Monsters.FindIndex(monster => monster.Code == 69264);
                var targetIndex =
                    startMap.Monsters.FindIndex(monster => monster.Code == 56611);
                Check(
                    "START_MAP places hidden Boss template before condition target",
                    bossIndex >= 0
                        && targetIndex > bossIndex
                        && startMap.Monsters[bossIndex].Flag0 == 1
                        && startMap.Monsters[targetIndex].Flag0 == 0,
                    ref failures);
            }

            var hasWarp = GameWorld.Dungeon.TryGetWarpMapOverride(
                dungeonId: 2005,
                mazeIndex: 0,
                targetX: 2,
                targetY: 1,
                out var sourceX,
                out var sourceY,
                out var destX,
                out var destY,
                out var overrideMapId);
            Check(
                "PVF Gent warp condition resolves source and destination map",
                hasWarp
                    && sourceX == 2
                    && sourceY == 1
                    && destX == 0
                    && destY == 0
                    && overrideMapId == 17069,
                ref failures);

            var hasSeaChaseRules = GameWorld.Dungeon.TryGetWarpMapConditionRules(
                dungeonId: 2006,
                mazeIndex: 0,
                out var seaChaseRules);
            Check(
                "PVF SeaChase keeps all warp condition rules",
                hasSeaChaseRules
                    && seaChaseRules.Count == 3
                    && seaChaseRules[0].SourceX == 0
                    && seaChaseRules[0].SourceY == 0
                    && seaChaseRules[0].DestinationX == 1
                    && seaChaseRules[0].DestinationY == 0
                    && seaChaseRules[1].SourceX == 0
                    && seaChaseRules[1].SourceY == 0
                    && seaChaseRules[1].DestinationX == 6
                    && seaChaseRules[1].DestinationY == 0
                    && seaChaseRules[2].SourceX == 7
                    && seaChaseRules[2].SourceY == 0
                    && seaChaseRules[2].DestinationX == 0
                    && seaChaseRules[2].DestinationY == 1,
                ref failures);

            var ambiguousSeaChaseWarp = GameWorld.Dungeon.TryGetWarpMapOverride(
                dungeonId: 2006,
                mazeIndex: 0,
                targetX: 0,
                targetY: 0,
                out _,
                out _,
                out _,
                out _,
                out _);
            Check(
                "PVF SeaChase does not guess between same-source warp destinations",
                !ambiguousSeaChaseWarp,
                ref failures);

            TestTimeCrackBossSelectionPvf(ref failures);
            TestQuestBoundBossSelectionPvf(ref failures);
        }

        private static void TestClearMapGroups(ref int failures)
        {
            const string ordinaryContent =
                "[maze info]\n" +
                "[size]\n1 1\n" +
                "[clear condition]\n" +
                "[clear map]\n16030 1\n" +
                "[/clear condition]\n";
            var ordinaryParsed = PvfLib.DungeonFile.Parse(ordinaryContent);
            var ordinaryConditions = ordinaryParsed.Mazes.Count == 1
                ? ordinaryParsed.Mazes[0].ClearConditions
                : new List<PvfLib.ClearConditionEntry>();
            var ordinaryState = new ClearConditionState(ordinaryConditions);
            Check(
                "ordinary clear-map condition keeps map and count semantics",
                ordinaryConditions.Count == 1
                    && ordinaryConditions[0].Type == 1
                    && ordinaryConditions[0].TargetId == 16030
                    && ordinaryConditions[0].Count == 1
                    && ordinaryConditions[0].GroupId == 0
                    && ordinaryState.Check(1, 16030),
                ref failures);

            const string content =
                "[maze info]\n" +
                "[size]\n1 1\n" +
                "[clear condition]\n" +
                "[clear map]\n`list` 3 100 101 102 2\n" +
                "[/clear condition]\n";
            var parsed = PvfLib.DungeonFile.Parse(content);
            var conditions = parsed.Mazes.Count == 1
                ? parsed.Mazes[0].ClearConditions
                : new List<PvfLib.ClearConditionEntry>();

            Check(
                "clear-map list parser preserves members and required count",
                IsClearMapGroup(conditions, required: 2, 100, 101, 102),
                ref failures);

            var state = new ClearConditionState(conditions);
            var first = state.Check(1, 100);
            var duplicate = state.Check(1, 100);
            var secondDistinct = state.Check(1, 101);
            Check(
                "clear-map group counts distinct maps only",
                state.TotalRequired == 2
                    && !first
                    && !duplicate
                    && secondDistinct
                    && state.CurrentProgress == 2,
                ref failures);
        }

        private static bool IsClearMapGroup(
            IReadOnlyList<PvfLib.ClearConditionEntry> conditions,
            int required,
            params int[] mapIds)
        {
            if (conditions == null || conditions.Count != mapIds.Length)
                return false;

            var groupId = 0;
            var actualMapIds = new HashSet<int>();
            foreach (var condition in conditions)
            {
                if (condition == null
                    || condition.Type != 1
                    || condition.Count != 1
                    || condition.GroupId <= 0
                    || condition.GroupRequired != required)
                {
                    return false;
                }

                if (groupId == 0)
                    groupId = condition.GroupId;
                else if (groupId != condition.GroupId)
                    return false;

                actualMapIds.Add(condition.TargetId);
            }

            return actualMapIds.SetEquals(mapIds);
        }

        private static void TestTimeCrackBossSelectionPvf(ref int failures)
        {
            var expectedBossMapIds = new HashSet<int>
            {
                17139,
                17144,
                17145,
                17148,
            };
            var huntTargets =
                GameWorld.QuestData.GetHuntMonsterTargets(13509);
            Check(
                "TimeCrack quest 13509 parses three Boss targets",
                huntTargets.Count == 3
                    && huntTargets[0].DungeonId == 2007
                    && huntTargets[0].MinimumDifficulty == -1
                    && huntTargets[0].MonsterCode == 69267
                    && huntTargets[1].MonsterCode == 69268
                    && huntTargets[2].MonsterCode == 69269,
                ref failures);
            Check(
                "TimeCrack quest trigger preserves three unfinished channels",
                GameWorld.QuestData.GetTriggerChannel(0x00040201, 0) == 1
                    && GameWorld.QuestData.GetTriggerChannel(0x00040201, 1) == 1
                    && GameWorld.QuestData.GetTriggerChannel(0x00040201, 2) == 1,
                ref failures);

            var maze = GameWorld.Dungeon.GetDungeonMaze(2007, 0);
            var bossPos = new[] { 9, 0 };
            var questBossMapId =
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            Slot = 3,
                            QuestId = 13509,
                            TriggerValue = 0x00040201,
                        },
                    });
            Check(
                "Unfinished TimeCrack quest fixes Boss room to target map",
                questBossMapId == 17148,
                ref failures);

            var everyUnfinishedChannelUsesTargetMap = true;
            foreach (var trigger in new[]
                {
                    0x00000001u,
                    0x00000200u,
                    0x00040000u,
                })
            {
                var selected =
                    SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                        dungeonId: 2007,
                        maze: maze,
                        bossPos: bossPos,
                        activeQuests: new List<ActiveQuest>
                        {
                            new ActiveQuest
                            {
                                QuestId = 13509,
                                TriggerValue = trigger,
                            },
                        });
                if (selected != 17148)
                {
                    everyUnfinishedChannelUsesTargetMap = false;
                    break;
                }
            }
            Check(
                "Any unfinished TimeCrack target channel keeps target Boss map",
                everyUnfinishedChannelUsesTargetMap,
                ref failures);

            Check(
                "Completed TimeCrack quest does not override Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 13509,
                            TriggerValue = 0,
                        },
                    }) == -1,
                ref failures);
            Check(
                "Unrelated quest list does not override TimeCrack Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>()) == -1,
                ref failures);
            Check(
                "TimeCrack material quest sources in ordinary rooms do not override Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 2007,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 13510,
                            TriggerValue = 1,
                        },
                    }) == -1,
                ref failures);

            var normalSelectionsValid = true;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var selected =
                    SpecialDungeonRunCoordinator.ResolveSelectedBossMapId(
                        dungeonId: 2007,
                        mazeIndex: 0,
                        maze: maze,
                        bossPos: bossPos,
                        activeQuests: new List<ActiveQuest>());
                if (!expectedBossMapIds.Contains(selected))
                {
                    normalSelectionsValid = false;
                    break;
                }
            }
            Check(
                "TimeCrack without related quest uses explicit Boss candidate pool",
                normalSelectionsValid,
                ref failures);
        }

        private static void TestQuestBoundBossSelectionPvf(ref int failures)
        {
            var maze = GameWorld.Dungeon.GetDungeonMaze(154, 0);
            var bossPos = new[] { 4, 1 };
            var cases = new[]
            {
                (QuestId: 1900, MonsterCode: 63517, MapId: 8213),
                (QuestId: 1919, MonsterCode: 64054, MapId: 8212),
                (QuestId: 1923, MonsterCode: 64055, MapId: 8211),
            };

            var allRewardSourcesResolve = true;
            foreach (var testCase in cases)
            {
                var targets = GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                    testCase.QuestId,
                    trigger: 1,
                    dungeonId: 154,
                    difficulty: 0);
                var selected = SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 154,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = (ushort)testCase.QuestId,
                            TriggerValue = 1,
                        },
                    },
                    difficulty: 0);
                if (!targets.Exists(target =>
                        target.ActorCode == testCase.MonsterCode
                        && target.Source == "monster reward item")
                    || selected != testCase.MapId)
                {
                    allRewardSourcesResolve = false;
                    break;
                }
            }
            Check(
                "Quest reward sources bind Ancient Tomb to the matching Boss map",
                allRewardSourcesResolve,
                ref failures);

            Check(
                "Non-Boss quest reward source does not force an unrelated Boss map",
                SpecialDungeonRunCoordinator.ResolveQuestBoundBossMapId(
                    dungeonId: 154,
                    maze: maze,
                    bossPos: bossPos,
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 1920,
                            TriggerValue = 1,
                        },
                    },
                    difficulty: 0) == -1,
                ref failures);

            var run = new DungeonRun(154, 0)
            {
                MazeIndex = 0,
            };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                run,
                maze,
                bossPos,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        QuestId = 1919,
                        TriggerValue = 1,
                    },
                });
            Check(
                "Generic selection fixes quest-bound Boss maps outside TimeCrack",
                run.SpecialDungeon == null
                && run.SelectedBossMapId == 8212,
                ref failures);
        }

        private static bool BytesEqual(byte[] actual, params byte[] expected)
        {
            if (actual == null || actual.Length != expected.Length)
                return false;

            for (var i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                    return false;
            }

            return true;
        }

        private static void Check(
            string name,
            bool ok,
            ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
