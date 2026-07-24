using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using System;
using System.IO;
using TimeSpiralData = DfoServer.GameWorld.TimeSpiralDungeonData;

namespace DfoServer.SelfTests
{
    public static class SpecialDungeonPart2SelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_DUNGEON_PART2 selftest ===");
            var failures = 0;

            TestLinkedDungeonProtocol(ref failures);
            TestLinkedDungeonParsing(ref failures);
            TestPvfLinkedDungeonChain(ref failures);
            TestTimeSpiralPvfAndEtc(ref failures);
            TestQuestNpcDungeonClear(ref failures);
            TestIgnoreDefaultDungeonClear(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestLinkedDungeonProtocol(ref int failures)
        {
            var info = DungeonNotificationBuilder.BuildLinkedDungeonInfo(
                nextDungeonId: 2016,
                difficulty: 2);
            Check(
                "LINKED_DUNGEON_INFO is int32 dungeon + int32 difficulty",
                BytesEqual(
                    info,
                    0xE0, 0x07, 0x00, 0x00,
                    0x02, 0x00, 0x00, 0x00),
                ref failures);

            var selectBody =
                DungeonEntryHandler.BuildLinkedDungeonSelectBody(
                    dungeonId: 2016,
                    difficulty: 2);
            var request = SelectDungeonRequest.Parse(selectBody);
            Check(
                "linked challenge reuses standard SELECT_DUNGEON body",
                BytesEqual(
                    selectBody,
                    0xE0, 0x07, 0x02, 0x00, 0x00)
                    && request.DungeonId == 2016
                    && request.Difficulty == 2
                    && request.Flag1 == 0
                    && request.Flag2 == 0,
                ref failures);

            Check(
                "EPLP 01-03 selects linked challenge",
                DungeonSettlementHandler.IsLinkedChallengeCommand(
                    new byte[] { 0x01, 0x03 }),
                ref failures);
            Check(
                "other EPLP options do not select linked challenge",
                !DungeonSettlementHandler.IsLinkedChallengeCommand(
                    new byte[] { 0x01, 0x00 })
                    && !DungeonSettlementHandler.IsLinkedChallengeCommand(
                        new byte[] { 0x01 }),
                ref failures);
        }

        private static void TestLinkedDungeonParsing(ref int failures)
        {
            const string raw =
                "[next]\n" +
                "2016 100 0\n" +
                "[/next]\n";
            var entries =
                GameWorld.Dungeon.ParseLinkedDungeonNextEntries(raw);
            Check(
                "linked dungeon parser reads next/rate/condition",
                entries.Count == 1
                    && entries[0].DungeonId == 2016
                    && entries[0].Rate == 100
                    && entries[0].Condition == 0,
                ref failures);
        }

        private static void TestPvfLinkedDungeonChain(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed linked dungeon checks: " +
                    "Script.pvf not found");
                return;
            }

            var first = GameWorld.Dungeon.GetLinkedDungeonNextEntries(2014);
            var second = GameWorld.Dungeon.GetLinkedDungeonNextEntries(2016);
            var terminal = GameWorld.Dungeon.GetLinkedDungeonNextEntries(2008);
            Check(
                "PVF linked chain starts with 2014 -> 2016",
                first.Count == 1
                    && first[0].DungeonId == 2016
                    && first[0].Condition == -1,
                ref failures);
            Check(
                "PVF linked chain continues with 2016 -> 2008",
                second.Count == 1
                    && second[0].DungeonId == 2008,
                ref failures);
            Check(
                "PVF linked chain terminates at 2008",
                terminal.Count == 0,
                ref failures);
            Check(
                "runtime linked challenge is limited to special dungeons",
                GameWorld.Dungeon.IsSpecialLinkedDungeon(2014)
                    && GameWorld.Dungeon.IsSpecialLinkedDungeon(2016)
                    && !GameWorld.Dungeon.IsSpecialLinkedDungeon(70),
                ref failures);
        }

        private static void TestTimeSpiralPvfAndEtc(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed TimeSpiral checks: " +
                    "Script.pvf not found");
                return;
            }

            Check(
                "TimeSpiral marker is parsed from dungeon PVF",
                TimeSpiralData.IsDungeon(3900)
                    && !TimeSpiralData.IsDungeon(104),
                ref failures);
            Check(
                "TimeSpiral trap object is a condition gate",
                TimeSpiralData.TryGetConditionGatePassiveObject(
                    27103,
                    12577,
                    out var gate)
                    && gate.ObjectPath.IndexOf(
                        "Timespiral_Trap.obj",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                ref failures);
            Check(
                "ordinary TimeSpiral objects do not open the gate",
                !TimeSpiralData.TryGetConditionGatePassiveObject(
                    27103,
                    12905,
                    out _)
                    && !TimeSpiralData.TryGetConditionGatePassiveObject(
                        27104,
                        12942,
                        out _),
                ref failures);

            var midBossMaze =
                GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                    3900,
                    0,
                    4,
                    0,
                    27104);
            Check(
                "ETC target room exposes the mv_771 boss family",
                TimeSpiralData.TryFindHiddenBossCandidate(
                    midBossMaze,
                    1,
                    out var midBoss)
                    && midBoss.Code == 64930
                    && midBoss.Type != 3
                    && midBoss.Type != 8,
                ref failures);

            var finalBossMaze =
                GameWorld.Dungeon.GetDungeonMapMonsterSummaryInformation(
                    3900,
                    2,
                    2,
                    0,
                    27112);
            Check(
                "DGN boss room exposes final mv_771 actor",
                TimeSpiralData.TryFindHiddenBossCandidate(
                    finalBossMaze,
                    1,
                    out var finalBoss)
                    && finalBoss.Code == 64923
                    && finalBoss.Type == 3,
                ref failures);

            var flag0Run = new DungeonRun
            {
                DungeonId = 3900,
                BossMapPos = new[] { 2, 2 },
                TimeSpiralTargetActive = true,
                TimeSpiralTargetX = 0,
                TimeSpiralTargetY = 4,
                TimeSpiralTargetFlag = 0,
            };
            var midBossRoom = new RoomState { Maze = midBossMaze };
            Check(
                "only ETC flag zero target registers a random boss",
                TimeSpiralDungeonCoordinator.IsHiddenBossRegistrationRoom(
                    flag0Run,
                    midBossRoom),
                ref failures);
            flag0Run.TimeSpiralTargetFlag = 1;
            Check(
                "ETC nonzero target stays an intermediate room",
                !TimeSpiralDungeonCoordinator.IsHiddenBossRegistrationRoom(
                    flag0Run,
                    midBossRoom),
                ref failures);

            var finalRun = new DungeonRun
            {
                DungeonId = 3900,
                BossMapPos = new[] { 2, 2 },
            };
            Check(
                "DGN final room registers without an ETC target",
                TimeSpiralDungeonCoordinator.IsHiddenBossRegistrationRoom(
                    finalRun,
                    new RoomState { Maze = finalBossMaze }),
                ref failures);

            Check(
                "TimeSpiral ETC buff weights parse without sending a packet",
                TimeSpiralData.TryPickTrapBuff(
                    _ => 0,
                    out var attackBuff,
                    out var attackRoll,
                    out var totalWeight)
                    && attackBuff.Index == 0
                    && attackBuff.Weight == 30
                    && attackBuff.PhysicalAttack == 100
                    && attackBuff.MagicalAttack == 100
                    && attackBuff.BuffTimeMs == 60000
                    && attackRoll == 0
                    && totalWeight == 100,
                ref failures);
        }

        private static void TestQuestNpcDungeonClear(ref int failures)
        {
            var dungeonFile = GameWorld.Dungeon.GetDungeonFile(522);
            var maze = dungeonFile.Mazes[0];
            var run = new DungeonRun(522, 0)
            {
                BossMapPos = new[]
                {
                    maze.BossMap[0],
                    maze.BossMap[1],
                },
                RoomKey = new RoomKey(
                    maze.BossMap[0],
                    maze.BossMap[1],
                    0),
            };
            var completedMeetNpc = new QuestSetTriggerResult
            {
                QuestId = 2602,
                PreviousTriggerValue = 1,
                TriggerValue = 0,
            };

            Check(
                "quest NPC dungeon clears on completed meet-NPC trigger",
                dungeonFile.QuestNpcDungeon == 1
                    && GameWorld.QuestData.IsMeetNpcQuest(2602)
                    && DungeonSettlementHandler.ShouldClearQuestNpcDungeon(
                        run,
                        dungeonFile.QuestNpcDungeon,
                        true,
                        completedMeetNpc),
                ref failures);

            run.RoomKey = new RoomKey(
                maze.StartMap[0],
                maze.StartMap[1],
                0);
            Check(
                "quest NPC dungeon cannot clear outside boss room",
                !DungeonSettlementHandler.ShouldClearQuestNpcDungeon(
                    run,
                    dungeonFile.QuestNpcDungeon,
                    true,
                    completedMeetNpc),
                ref failures);
        }

        private static void TestIgnoreDefaultDungeonClear(ref int failures)
        {
            Check(
                "ignore-default blocks the implicit boss endpoint",
                !DungeonCombatHandler.ShouldClearDungeon(
                    false,
                    true,
                    true),
                ref failures);
            Check(
                "ignore-default keeps explicit monster clear conditions",
                DungeonCombatHandler.ShouldClearDungeon(
                    true,
                    false,
                    true),
                ref failures);
            Check(
                "ignore-default keeps explicit map clear conditions",
                DungeonCombatHandler.ShouldClearDungeon(
                    true,
                    true,
                    true),
                ref failures);
            Check(
                "ordinary boss endpoint clear remains enabled",
                DungeonCombatHandler.ShouldClearDungeon(
                    false,
                    true,
                    false),
                ref failures);

            var stationEscape = GameWorld.Dungeon.GetDungeonFile(2008);
            Check(
                "StationEscape uses ignore-default with an explicit boss condition",
                stationEscape.IgnoreDefaultDungeonClear
                    && stationEscape.Mazes.Exists(
                        maze => maze.ClearConditions.Exists(
                            condition =>
                                condition.Type == 4
                                && condition.TargetId == 69257
                                && condition.Count == 1)),
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
            bool condition,
            ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}
