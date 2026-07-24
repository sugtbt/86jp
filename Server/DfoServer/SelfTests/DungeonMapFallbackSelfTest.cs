using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonMapFallbackSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_MAP_FALLBACK selftest ===");
            var failures = 0;

            var mapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "map", X = 0, Y = 0, Index = 13417 },
            };
            var mapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 14999, FilePath = "eternal_dream/q_7_0.map" },
            };
            var mapDirs = new List<string> { "eternal_dream" };

            var mapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out var reason);

            Check("quest start room prefers coordinate quest variant over first ordinary map spec",
                mapId == 14999 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            try
            {
                var flatSpecialPassiveMap = MapFile.Parse(
                    "[special passive object]\n" +
                    "10001 10 20 0 " +
                    "10002 30 40 1\n");
                Check("special passive object parser keeps legacy flat rows",
                    flatSpecialPassiveMap.SpecialPassiveObjects.Count == 2
                    && flatSpecialPassiveMap.SpecialPassiveObjects[0].ObjectCode == 10001
                    && flatSpecialPassiveMap.SpecialPassiveObjects[1].ObjectCode == 10002
                    && flatSpecialPassiveMap.SpecialPassiveObjects[0].Spawns.Count == 0,
                    ref failures);

                var extendedSpecialPassiveMap = MapFile.Parse(
                    "[special passive object]\n" +
                    "14056 100 200 0 2 " +
                    "`[monster]` 61801 62 0 0 0 " +
                    "`[monster]` 59013 62 0 1 0\n");
                Check("special passive object parser reads inline spawn rows",
                    extendedSpecialPassiveMap.SpecialPassiveObjects.Count == 1
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns.Count == 2
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns[0].Code == 61801
                    && extendedSpecialPassiveMap.SpecialPassiveObjects[0].Spawns[1].Code == 59013,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] special passive object parser compatibility: {ex.Message}");
                failures++;
            }

            var passiveObjectMaze = new DungeonData.MazeSumInfo
            {
                Monsters = new List<DungeonData.MonsterSumInfo>
                {
                    new DungeonData.MonsterSumInfo { Code = 100, Type = 0, Level = 1, IsBlocking = true },
                    new DungeonData.MonsterSumInfo { Code = 14056, Type = 9, Level = 1, IsBlocking = false },
                },
            };
            Check("passive start-map objects do not count as tracked monsters",
                DungeonMapHandler.CountServerTrackedMonsters(passiveObjectMaze) == 1,
                ref failures);

            var compactQuestMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 15000, FilePath = "eternal_dream/q7_0.map" },
            };
            var compactQuestMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: compactQuestMapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out reason);

            Check("quest variant detection accepts q-prefixed coordinate map names",
                compactQuestMapId == 15000 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            var ordinaryMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: false,
                reason: out reason);

            Check("ordinary fallback keeps first map spec when quest variant is not preferred",
                ordinaryMapId == 13417 && reason == "first map spec",
                ref failures);

            var rottenMapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
            };
            var rottenMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 18914, FilePath = "158_DecayArea/18914(4,0)B.map" },
                new LstEntry { Id = 36041, FilePath = "158_DecayArea/maze(2,2).map" },
                new LstEntry { Id = 18911, FilePath = "158_DecayArea/18911(0,5)N.map" },
            };
            var rottenFallbackMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 158,
                mazeIndex: 0,
                x: 1,
                y: 5,
                mapSpecifications: rottenMapSpecs,
                mapEntries: rottenMapEntries,
                mapDirCandidates: new List<string> { "158_DecayArea" },
                preferQuestVariant: false,
                reason: out reason);

            Check("unresolved rotten land room uses nearby normal coordinate before distant maze template",
                rottenFallbackMapId == 18911 && reason.StartsWith("nearest coordinate map", StringComparison.Ordinal),
                ref failures);

            var lowercaseBossEntries = new List<LstEntry>
            {
                new LstEntry { Id = 77001, FilePath = "generic/77001(2,4)b.map" },
                new LstEntry { Id = 77002, FilePath = "generic/77002(2,6)N.map" },
            };
            var lowercaseBossFallbackMapId = DfoServer.GameWorld.DungeonMapResolver.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 9000,
                mazeIndex: 0,
                x: 2,
                y: 5,
                mapSpecifications: null,
                mapEntries: lowercaseBossEntries,
                mapDirCandidates: new List<string> { "generic" },
                preferQuestVariant: false,
                reason: out reason);

            Check("coordinate fallback ignores lowercase boss suffix",
                lowercaseBossFallbackMapId == 77002 && reason.StartsWith("nearest coordinate map", StringComparison.Ordinal),
                ref failures);


            CheckSuitableLevelEligibility(ref failures);

            var towerClassificationIsSafe = false;
            try
            {
                towerClassificationIsSafe = !DungeonData.TryGetTowerOfDespairFloor(
                    int.MaxValue,
                    out _);
            }
            catch
            {
                towerClassificationIsSafe = false;
            }
            Check("tower classification treats an unknown dungeon as non-tower without throwing",
                towerClassificationIsSafe,
                ref failures);

            try
            {
                var despairTowerFirstFloor = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 11008,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 0);
                Check("tower of despair first floor resolves its PVF map and boss APC",
                    despairTowerFirstFloor.Index == 15130
                    && ContainsMonster(despairTowerFirstFloor, 20426),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] tower of despair first floor resolves its PVF map and boss APC: {ex.Message}");
                failures++;
            }

            try
            {
                var issue189StartMap = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 165,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 4);
                Check("issue 189 quest maze start room uses map specification",
                    issue189StartMap.Index == 33060,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 189 quest maze start room uses map specification: {ex.Message}");
                failures++;
            }

            try
            {
                var upperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 1 });
                var middleBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 2,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 2 });
                var lowerBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 3,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 3 });

                Check("issue 180 upper boss room uses boss actor map",
                    upperBoss.Index == 8179 && ContainsMonster(upperBoss, 65312),
                    ref failures);
                Check("issue 180 middle boss room skips duplicate non-boss map",
                    middleBoss.Index == 8180 && ContainsMonster(middleBoss, 65312),
                    ref failures);
                Check("issue 180 lower boss room uses boss actor map",
                    lowerBoss.Index == 8181 && ContainsMonster(lowerBoss, 65312),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 180 boss rooms use boss actor maps: {ex.Message}");
                failures++;
            }

            try
            {
                var issue227Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });
                var issue227Adjacent = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 1,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });

                Check("issue 227 selected boss room keeps boss actor map",
                    issue227Boss.Index == 18915 && ContainsMonster(issue227Boss, 65029),
                    ref failures);
                Check("issue 227 adjacent room uses declared PVF map without false boss",
                    (issue227Adjacent.Index == 18901
                        || issue227Adjacent.Index == 18905
                        || issue227Adjacent.Index == 18908)
                    && !ContainsMonster(issue227Adjacent, 65029),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 227 rotten land unresolved room map selection: {ex.Message}");
                failures++;
            }

            try
            {
                var issue361StartRoom = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 2,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361SelectedUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 4,
                    y: 0,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361UnselectedLowerBossCoordinate = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });
                var issue361LowerConnectorCoordinate = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 1,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 0 });

                Check("issue 361 start room still prefers start map variant",
                    issue361StartRoom.Index == 18916,
                    ref failures);
                Check("issue 361 selected upper boss room keeps boss actor map",
                    issue361SelectedUpperBoss.Index == 18914 && ContainsMonster(issue361SelectedUpperBoss, 65029),
                    ref failures);
                var repeatedUpperBossSelectionsKeepActor = true;
                for (var selection = 0; selection < 32; selection++)
                {
                    var repeatedUpperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 158,
                        x: 4,
                        y: 0,
                        mazeIndex: 0,
                        bossPos: new[] { 4, 0 });
                    if (repeatedUpperBoss.Index != 18914
                        || !ContainsMonster(repeatedUpperBoss, 65029))
                    {
                        repeatedUpperBossSelectionsKeepActor = false;
                        break;
                    }
                }
                Check("issue 361 repeated upper boss selections keep boss actor map",
                    repeatedUpperBossSelectionsKeepActor,
                    ref failures);
                var issue361QuestMazeBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 4,
                    y: 0,
                    mazeIndex: 3,
                    bossPos: new[] { 4, 0 });
                Check("issue 361 quest maze keeps its explicit APC boss map",
                    issue361QuestMazeBoss.Index == 18919
                    && ContainsMonster(issue361QuestMazeBoss, 56408)
                    && !ContainsMonster(issue361QuestMazeBoss, 65029),
                    ref failures);
                Check("issue 361 unselected lower boss coordinate does not spawn false boss",
                    issue361UnselectedLowerBossCoordinate.Index != 18915
                    && !ContainsMonster(issue361UnselectedLowerBossCoordinate, 65029),
                    ref failures);
                Check("issue 361 lower connector uses declared PVF map specification",
                    (issue361LowerConnectorCoordinate.Index == 18901
                        || issue361LowerConnectorCoordinate.Index == 18905
                        || issue361LowerConnectorCoordinate.Index == 18908)
                    && !ContainsMonster(issue361LowerConnectorCoordinate, 65029),
                    ref failures);

                var issue361Maze = new MazeInfo
                {
                    Width = 5,
                    Height = 6,
                    StartMap = new[] { 0, 2 },
                    BossMap = new[] { 4, 0 },
                    MapSpecifications = new List<MapSpecificationItem>
                    {
                        new MapSpecificationItem { X = 0, Y = 2, Index = 18904 },
                        new MapSpecificationItem { X = 1, Y = 2, Index = 18905 },
                        new MapSpecificationItem { X = 2, Y = 2, Index = 18906 },
                        new MapSpecificationItem { X = 2, Y = 3, Index = 18907 },
                        new MapSpecificationItem { X = 2, Y = 4, Index = 18910 },
                        new MapSpecificationItem { X = 2, Y = 5, Index = 18913 },
                        new MapSpecificationItem { X = 0, Y = 5, Index = 18911 },
                        new MapSpecificationItem { X = 1, Y = 5, Index = 18901, MapCandidates = new[] { 18901, 18905, 18908 } },
                        new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
                    },
                };
                var resolvedIssue361Move = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 0,
                    maze: issue361Maze,
                    currentRoom: new RoomKey(0, 5, -1),
                    requestedX: 1,
                    requestedY: 5,
                    bossMapPos: new[] { 4, 0 },
                    target: out var issue361MoveTarget,
                    reason: out var issue361MoveReason);
                Check("issue 361 sparse MOVE_MAP keeps declared lower connector room",
                    resolvedIssue361Move
                    && issue361MoveTarget.X == 1
                    && issue361MoveTarget.Y == 5
                    && issue361MoveReason == "known room",
                    ref failures);

                var issue361ShortMaze = new MazeInfo
                {
                    Width = 5,
                    Height = 3,
                    StartMap = new[] { 0, 2 },
                    BossMap = new[] { 4, 0 },
                    MapSpecifications = new List<MapSpecificationItem>
                    {
                        new MapSpecificationItem { X = 0, Y = 2, Index = 18904 },
                        new MapSpecificationItem { X = 1, Y = 2, Index = 18905 },
                        new MapSpecificationItem { X = 2, Y = 2, Index = 18906 },
                        new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
                    },
                };
                var resolvedOutOfBoundsMove = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 1,
                    maze: issue361ShortMaze,
                    currentRoom: new RoomKey(0, 2, -1),
                    requestedX: 0,
                    requestedY: 3,
                    bossMapPos: new[] { 4, 0 },
                    target: out var outOfBoundsMoveTarget,
                    reason: out var outOfBoundsMoveReason);
                Check("issue 361 topology ignores same-directory PVF coordinates outside current maze bounds",
                    !resolvedOutOfBoundsMove
                    && outOfBoundsMoveTarget.X == 0
                    && outOfBoundsMoveTarget.Y == 0
                    && outOfBoundsMoveReason == "outside known dungeon room coordinates",
                    ref failures);

                var resolvedFarMove = DungeonRoomTopology.TryResolveMoveTarget(
                    dungeonId: 158,
                    mazeIndex: 0,
                    maze: issue361Maze,
                    currentRoom: new RoomKey(0, 5, -1),
                    requestedX: 4,
                    requestedY: 5,
                    bossMapPos: new[] { 4, 0 },
                    target: out var farMoveTarget,
                    reason: out var farMoveReason);
                Check("issue 361 topology does not normalize non-adjacent MOVE_MAP requests",
                    !resolvedFarMove
                    && farMoveTarget.X == 0
                    && farMoveTarget.Y == 0
                    && farMoveReason == "outside known dungeon room coordinates",
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 361 unselected boss coordinate map selection: {ex.Message}");
                failures++;
            }

            try
            {
                var issue167Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 89,
                    x: 0,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 1 });
                var issue167QuestStart = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 89,
                    x: 5,
                    y: 1,
                    mazeIndex: 1);

                Check("issue 167 gent defense final room uses AI boss map",
                    issue167Boss.Index == 21314 && ContainsMonster(issue167Boss, 10409),
                    ref failures);
                Check("issue 167 hostile AI boss is APC and does not block room clear",
                    !ContainsBlockingMonster(issue167Boss, 10409),
                    ref failures);
                Check("issue 167 final room includes special passive wave monster templates",
                    CountMonster(issue167Boss, 61801) > 0 && CountMonster(issue167Boss, 61803) > 0,
                    ref failures);
                Check("issue 167 special passive wave templates do not block clear",
                    ContainsMonster(issue167Boss, 61801) && !ContainsBlockingMonster(issue167Boss, 61801),
                    ref failures);
                Check("issue 167 special passive wave templates preserve object grouping",
                    HasTemplate(issue167Boss, 61801, 0, 0, 0)
                    && HasTemplate(issue167Boss, 61801, 1, 0, 1)
                    && HasTemplate(issue167Boss, 61494, 2, 0, 2)
                    && HasTemplate(issue167Boss, 59013, 2, 1, 2),
                    ref failures);
                Check("issue 167 final room includes special passive wave checker object",
                    HasStartMapObject(issue167Boss, 14056, 9, 9) && !ContainsBlockingMonster(issue167Boss, 14056),
                    ref failures);
                Check("issue 167 special passive object rows precede hidden templates",
                    IndexOfMonster(issue167Boss, 14056) >= 0
                    && IndexOfMonster(issue167Boss, 14056) < IndexOfFirstHiddenTemplate(issue167Boss)
                    && IndexOfMonster(issue167Boss, 10409) > IndexOfLastHiddenTemplate(issue167Boss),
                    ref failures);
                Check("issue 167 friendly quest AI remains non-blocking",
                    ContainsMonster(issue167QuestStart, 10625) && !ContainsBlockingMonster(issue167QuestStart, 10625),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 167 gent defense AI boss room: {ex.Message}");
                failures++;
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool ContainsMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode)
                    return true;
            return false;
        }

        private static bool ContainsBlockingMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode && monster.IsBlocking)
                    return true;
            return false;
        }

        private static int CountMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            var count = 0;
            if (maze.Monsters == null)
                return count;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode)
                    count++;
            return count;
        }

        private static bool HasTemplate(DungeonData.MazeSumInfo maze, int monsterCode, ushort templateOrder, int packetIndex, byte flag1)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
            {
                if (monster.Code == monsterCode
                    && monster.Type == 0
                    && monster.TemplateOrder == templateOrder
                    && monster.PacketIndex == packetIndex
                    && monster.Flag0 == 1
                    && monster.Flag1 == flag1)
                    return true;
            }
            return false;
        }

        private static bool HasStartMapObject(DungeonData.MazeSumInfo maze, int objectCode, byte type, int packetIndex)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
            {
                if (monster.Code == objectCode
                    && monster.Type == type
                    && monster.PacketIndex == packetIndex
                    && monster.Flag0 == 0)
                    return true;
            }
            return false;
        }

        private static int IndexOfMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return -1;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Code == monsterCode)
                    return i;
            return -1;
        }

        private static int IndexOfFirstHiddenTemplate(DungeonData.MazeSumInfo maze)
        {
            if (maze.Monsters == null)
                return -1;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Flag0 == 1)
                    return i;
            return -1;
        }

        private static int IndexOfLastHiddenTemplate(DungeonData.MazeSumInfo maze)
        {
            var index = -1;
            if (maze.Monsters == null)
                return index;
            for (var i = 0; i < maze.Monsters.Count; i++)
                if (maze.Monsters[i].Flag0 == 1)
                    index = i;
            return index;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok) failures++;
        }

        private static void CheckSuitableLevelEligibility(ref int failures)
        {
            Check("suitable dungeon range uses minimum and basis level",
                DungeonData.TryGetSuitableLevelRange(144, out var minLevel, out var maxLevel)
                && minLevel == 1
                && maxLevel == 5,
                ref failures);
            Check("suitable dungeon rejects level below min",
                !DungeonData.IsSuitableLevelDungeon(144, 0),
                ref failures);
            Check("suitable dungeon accepts min level",
                DungeonData.IsSuitableLevelDungeon(144, 1),
                ref failures);
            Check("suitable dungeon accepts max level",
                DungeonData.IsSuitableLevelDungeon(144, 5),
                ref failures);
            Check("suitable dungeon rejects level above max",
                !DungeonData.IsSuitableLevelDungeon(144, 6),
                ref failures);
        }
    }
}
