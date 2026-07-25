using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.IO;
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
            config.SeizeMoney.ClearGoldIngotMaxCount = 4;

            var special = new SpecialDungeonRuntime(
                3000,
                SpecialDungeonKind.SeizeMoney,
                config);
            var reserved = special.TryReserveSeizeMoneyClearReward(
                remainingGoldUnits: 5,
                out var count,
                out var gauge);
            Check(
                "SeizeMoney scales clear reward from remaining gauge",
                reserved && count == 2 && gauge == 500,
                ref failures);

            var reservedAgain = special.TryReserveSeizeMoneyClearReward(
                remainingGoldUnits: 10,
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
            var stationEscape =
                SpecialDungeonModuleConfig.CreateRuntime(2016);
            var sealForest =
                SpecialDungeonModuleConfig.CreateRuntime(2009);
            var meltdown =
                SpecialDungeonModuleConfig.CreateRuntime(2010);
            var seizeMoney =
                SpecialDungeonModuleConfig.CreateRuntime(2011);

            Check(
                "PVF identifies all part-one special dungeon kinds",
                gent?.Kind == SpecialDungeonKind.GentInfiltrate
                    && sea?.Kind == SpecialDungeonKind.SeaChase
                    && timeCrack?.Kind == SpecialDungeonKind.TimeCrack
                    && stationEscape?.Kind == SpecialDungeonKind.StationEscape
                    && sealForest?.Kind == SpecialDungeonKind.SealForest
                    && meltdown?.Kind == SpecialDungeonKind.MeltdownHelpus
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
                var run = new DungeonRun(2010, 0)
                {
                    SpecialDungeon = meltdown,
                };
                run.MeltdownHelpusHostages.Add(
                    new MeltdownHelpusHostageAssignment
                    {
                        MonsterCode = 56611,
                        X = 3,
                        Y = 3,
                    });
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

            TestTimeCrackBossSelectionPvf(ref failures);
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
                    && huntTargets[0].MapId == -1
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

            var bartholosisTargets =
                GameWorld.QuestData.GetSeekingMonsterRewardTargets(1900);
            Check(
                "Bartholosis quest parses its seeking-item source monster",
                bartholosisTargets.Count == 1
                    && bartholosisTargets[0].DungeonId == 154
                    && bartholosisTargets[0].MonsterCode == 63517
                    && bartholosisTargets[0].ItemId == 10089081,
                ref failures);

            var ancientHeartMaze =
                GameWorld.Dungeon.GetDungeonMaze(154, 0);
            var bartholosisBossMap =
                SpecialDungeonRunCoordinator.ResolveSelectedBossMapId(
                    dungeonId: 154,
                    mazeIndex: 0,
                    maze: ancientHeartMaze,
                    bossPos: new[] { 4, 1 },
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 1900,
                            TriggerValue = 1,
                        },
                    });
            Check(
                "Active Bartholosis quest fixes Ancient Heart Boss room",
                bartholosisBossMap == 8213
                    && GameWorld.DungeonMapResolver.MapContainsMonsterCode(
                        bartholosisBossMap,
                        63517),
                ref failures);

            var bartholosisWithGlobalSeekingQuest =
                SpecialDungeonRunCoordinator.ResolveSelectedBossMapId(
                    dungeonId: 154,
                    mazeIndex: 0,
                    maze: ancientHeartMaze,
                    bossPos: new[] { 4, 1 },
                    activeQuests: new List<ActiveQuest>
                    {
                        new ActiveQuest
                        {
                            QuestId = 446,
                            TriggerValue = 44,
                        },
                        new ActiveQuest
                        {
                            QuestId = 1900,
                            TriggerValue = 1,
                        },
                    });
            Check(
                "Bartholosis story target outranks achievement drop sources",
                GameWorld.QuestData.IsAchievementQuest(446)
                    && !GameWorld.QuestData.IsAchievementQuest(1900)
                    && bartholosisWithGlobalSeekingQuest == 8213,
                ref failures);

            var ordinaryRun = new DungeonRun(154, 0)
            {
                MazeIndex = 0,
            };
            SpecialDungeonRunCoordinator.ConfigureSelection(
                ordinaryRun,
                ancientHeartMaze,
                new[] { 4, 1 },
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        QuestId = 1900,
                        TriggerValue = 1,
                    },
                });
            Check(
                "Ordinary dungeon selection applies quest-bound Boss override",
                ordinaryRun.SpecialDungeon == null
                    && ordinaryRun.SelectedBossMapId == 8213,
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
