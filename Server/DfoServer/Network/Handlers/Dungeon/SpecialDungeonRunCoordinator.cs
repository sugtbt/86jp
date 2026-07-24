using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class SpecialDungeonRunCoordinator
    {
        internal static bool IsBossEntranceSummonKind(SpecialDungeonKind kind)
            => kind == SpecialDungeonKind.MeltdownHelpus
                || kind == SpecialDungeonKind.StationEscape;

        internal static void InitializeRuntime(
            EnhancedClientSession session,
            int dungeonId,
            string source)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            run.SpecialDungeon = SpecialDungeonModuleConfig.CreateRuntime(dungeonId);
            var special = run.SpecialDungeon;
            if (special == null)
                return;

            FileLogger.Log(
                $"[SpecialDungeonModule] runtime init source={source} " +
                $"cid={session.Player.CharacterId} dungeon={dungeonId} kind={special.Kind}");
        }

        internal static void ConfigureSelection(
            DungeonRun run,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (run == null)
                return;

            DungeonFile dungeonFile;
            try
            {
                dungeonFile = DungeonData.GetDungeonFile(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] selection config failed: " +
                    $"dungeon={run.DungeonId} maze={run.MazeIndex} error={ex.Message}");
                return;
            }

            run.IgnoreDefaultDungeonClear =
                dungeonFile.IgnoreDefaultDungeonClear
                || TimeSpiralDungeonCoordinator.IsDungeon(run.DungeonId);
            run.MeltdownHelpusHostages.Clear();
            run.SpecialMinimapIconGroups = null;

            var special = run.SpecialDungeon;
            if (special == null)
                return;

            if (IsBossEntranceSummonKind(special.Kind))
            {
                run.MeltdownHelpusHostages = BuildBossEntranceAssignments(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos,
                    dungeonFile);
                run.SpecialMinimapIconGroups =
                    BuildMinimapIconGroupsFromAssignments(run.MeltdownHelpusHostages);
            }
            else if (special.Kind == SpecialDungeonKind.GentInfiltrate)
            {
                special.Config.TimerSecondsByDungeonId.TryGetValue(
                    run.DungeonId,
                    out var timerSeconds);
                special.ConfigureGentInfiltrateBossEntrance(
                    dungeonFile.BossRoomEntranceCondition,
                    timerSeconds);
                run.SpecialMinimapIconGroups = BuildGentTowerIconGroups(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos,
                    special);
            }

            if (special.Kind == SpecialDungeonKind.TimeCrack)
            {
                run.SelectedBossMapId = ResolveSelectedBossMapId(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos,
                    activeQuests);
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] selection configured: " +
                $"dungeon={run.DungeonId} maze={run.MazeIndex} kind={special.Kind} " +
                $"ignoreDefault={run.IgnoreDefaultDungeonClear} " +
                $"conditionTargets={run.MeltdownHelpusHostages.Count} " +
                $"iconGroups={run.SpecialMinimapIconGroups?.Count ?? 0} " +
                $"timer={special.GentInfiltrateTimerSeconds}");
        }

        internal static void CloneSelectionState(DungeonRun source, DungeonRun target)
        {
            if (source == null || target == null)
                return;

            target.SpecialDungeon = source.SpecialDungeon?.CloneFresh();
            target.IgnoreDefaultDungeonClear = source.IgnoreDefaultDungeonClear;
            target.SpecialMinimapIconGroups = CloneMinimapIconGroups(
                source.SpecialMinimapIconGroups);
            target.MeltdownHelpusHostages = CloneAssignments(
                source.MeltdownHelpusHostages);
            target.SelectedBossMapId = source.SelectedBossMapId;
        }

        internal static IReadOnlyList<IReadOnlyList<(byte, byte)>> ResolveMinimapIconGroups(
            DungeonRun run,
            int dungeonId,
            int mazeIndex)
        {
            if (run?.SpecialMinimapIconGroups != null
                && run.SpecialMinimapIconGroups.Count > 0)
            {
                return run.SpecialMinimapIconGroups;
            }

            MazeInfo maze;
            try
            {
                maze = DungeonData.GetDungeonMaze(dungeonId, mazeIndex);
            }
            catch
            {
                return null;
            }

            if (maze?.RidableScript == null
                || maze.RidableScript.MinimapIcon <= 0
                || run?.RidableObjects == null
                || run.RidableObjects.Count == 0)
            {
                return null;
            }

            var points = new List<(byte, byte)>();
            var seen = new HashSet<int>();
            foreach (var obj in run.RidableObjects)
            {
                var key = (obj.MapX << 8) | obj.MapY;
                if (seen.Add(key))
                    points.Add((obj.MapX, obj.MapY));
            }

            return points.Count > 0
                ? new List<IReadOnlyList<(byte, byte)>> { points }
                : null;
        }

        internal static int ResolveStartMapOverride(
            DungeonRun run,
            int nextX,
            int nextY,
            int requestedOverrideMapId)
        {
            if (requestedOverrideMapId > 0
                || run?.SelectedBossMapId <= 0
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2
                || nextX != run.BossMapPos[0]
                || nextY != run.BossMapPos[1])
            {
                return requestedOverrideMapId;
            }

            return run.SelectedBossMapId;
        }

        internal static void AppendStartMapActors(
            EnhancedClientSession session,
            DungeonData.MazeSumInfo maze)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || !IsBossEntranceSummonKind(special.Kind)
                || maze.Monsters == null)
            {
                return;
            }

            AppendHiddenBossTemplates(run, maze);
            AppendConditionActors(run, maze);
        }

        internal static bool TryApplyGentWarpOverride(
            EnhancedClientSession session,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.GentInfiltrate
                || !special.GentInfiltrateConditionComplete)
            {
                return false;
            }

            if (!DungeonData.TryGetWarpMapOverride(
                    run.DungeonId,
                    run.MazeIndex,
                    moveTarget.X,
                    moveTarget.Y,
                    out var sourceX,
                    out var sourceY,
                    out var destX,
                    out var destY,
                    out var warpMapId))
            {
                return false;
            }

            overrideMapId = warpMapId;
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE warp override: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"target=({moveTarget.X},{moveTarget.Y}) source=({sourceX},{sourceY}) " +
                $"dest=({destX},{destY}) map={warpMapId} " +
                $"strongWarlord={special.GentInfiltrateStrongWarlord} " +
                $"completion={special.GentInfiltrateCompletionSource}");
            return true;
        }

        internal static List<int> GetBossEntranceSummonCodes(int dungeonId)
        {
            try
            {
                return ParseConditionMonsterCodes(
                    DungeonData.GetDungeonFile(dungeonId).BossRoomEntranceCondition,
                    "[summon monster]");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss summon config load failed: " +
                    $"dungeon={dungeonId} error={ex.Message}");
                return new List<int>();
            }
        }

        internal static int ResolveSelectedBossMapId(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (bossPos == null
                || bossPos.Length < 2
                || !GameWorld.DungeonMapResolver.HasExplicitBossCandidatePool(
                    maze,
                    bossPos[0],
                    bossPos[1]))
            {
                return -1;
            }

            var questMapId = ResolveQuestBoundBossMapId(
                dungeonId,
                maze,
                bossPos,
                activeQuests);
            if (questMapId > 0)
                return questMapId;

            try
            {
                return GameWorld.DungeonMapResolver
                    .ResolveExplicitBossCandidateMapId(
                    maze,
                    bossPos[0],
                    bossPos[1]);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss map selection failed: " +
                    $"dungeon={dungeonId} maze={mazeIndex} error={ex.Message}");
                return -1;
            }
        }

        internal static int ResolveQuestBoundBossMapId(
            int dungeonId,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (bossPos == null
                || bossPos.Length < 2
                || activeQuests == null
                || activeQuests.Count == 0)
            {
                return -1;
            }

            var candidateMapIds =
                GameWorld.DungeonMapResolver.GetExplicitBossCandidateMapIds(
                    maze,
                    bossPos[0],
                    bossPos[1]);
            if (candidateMapIds.Count == 0)
                return -1;

            var matchesByMap =
                new Dictionary<int, List<(ActiveQuest Quest, GameWorld.HuntMonsterQuestTarget Target)>>();
            foreach (var activeQuest in activeQuests)
            {
                if (activeQuest == null || activeQuest.TriggerValue == 0)
                    continue;

                foreach (var target in
                    GameWorld.QuestData.GetHuntMonsterTargets(activeQuest.QuestId))
                {
                    if (target.DungeonId != dungeonId
                        || GameWorld.QuestData.GetTriggerChannel(
                            activeQuest.TriggerValue,
                            target.ChannelIndex) <= 0)
                    {
                        continue;
                    }

                    foreach (var candidateMapId in candidateMapIds)
                    {
                        if (target.MapId > 0
                            && target.MapId != candidateMapId)
                        {
                            continue;
                        }

                        if (!GameWorld.DungeonMapResolver.MapContainsMonsterCode(
                                candidateMapId,
                                target.MonsterCode))
                        {
                            continue;
                        }

                        if (!matchesByMap.TryGetValue(
                            candidateMapId,
                            out var matches))
                        {
                            matches =
                                new List<(ActiveQuest, GameWorld.HuntMonsterQuestTarget)>();
                            matchesByMap[candidateMapId] = matches;
                        }
                        matches.Add((activeQuest, target));
                    }
                }
            }

            var bestScore = 0;
            var bestMapIds = new List<int>();
            foreach (var candidateMapId in candidateMapIds)
            {
                if (!matchesByMap.TryGetValue(
                        candidateMapId,
                        out var matches))
                {
                    continue;
                }

                if (matches.Count > bestScore)
                {
                    bestScore = matches.Count;
                    bestMapIds.Clear();
                    bestMapIds.Add(candidateMapId);
                }
                else if (matches.Count == bestScore)
                {
                    bestMapIds.Add(candidateMapId);
                }
            }

            if (bestMapIds.Count == 0)
                return -1;

            var selectedMapId = bestMapIds.Count == 1
                ? bestMapIds[0]
                : bestMapIds[ServerRandom.Next(bestMapIds.Count)];
            FileLogger.Log(
                $"[SpecialDungeonModule] TIME_CRACK quest boss map: " +
                $"dungeon={dungeonId} map={selectedMapId} " +
                $"matches={matchesByMap[selectedMapId].Count}");
            return selectedMapId;
        }

        private static void AppendConditionActors(
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
        {
            var codes = new List<int>();
            foreach (var assignment in run.MeltdownHelpusHostages)
            {
                if (assignment != null
                    && !assignment.Rescued
                    && assignment.X == maze.X
                    && assignment.Y == maze.Y)
                {
                    codes.Add(assignment.MonsterCode);
                }
            }

            if (codes.Count == 0)
                return;

            var actors = DungeonData.GetMapMonsterConditionSummaryInformation(
                maze.Index,
                run.DungeonId,
                maze.X,
                maze.Y,
                codes);
            maze.Monsters.AddRange(actors);
            FileLogger.Log(
                $"[SpecialDungeonModule] condition actors added: " +
                $"dungeon={run.DungeonId} kind={run.SpecialDungeon.Kind} " +
                $"room=({maze.X},{maze.Y}) map={maze.Index} " +
                $"codes={string.Join(",", codes)} count={actors.Count}");
        }

        private static void AppendHiddenBossTemplates(
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
        {
            var bossCodes = GetBossEntranceSummonCodes(run.DungeonId);
            if (bossCodes.Count == 0)
                return;

            var actors = DungeonData.GetMapConditionalSummonSummaryInformation(
                maze.Index,
                run.DungeonId,
                maze.X,
                maze.Y,
                bossCodes);
            var added = 0;
            foreach (var actor in actors)
            {
                if (maze.Monsters.Any(monster => monster.Code == actor.Code))
                    continue;

                var template = actor;
                template.Flag0 = 1;
                template.IsBlocking = false;
                maze.Monsters.Add(template);
                added++;
            }

            if (added > 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] hidden boss templates added: " +
                    $"dungeon={run.DungeonId} kind={run.SpecialDungeon.Kind} " +
                    $"room=({maze.X},{maze.Y}) map={maze.Index} count={added}");
            }
        }

        private static List<MeltdownHelpusHostageAssignment> BuildBossEntranceAssignments(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPos,
            DungeonFile dungeonFile)
        {
            var assignments = new List<MeltdownHelpusHostageAssignment>();
            if (maze?.MapSpecifications == null || dungeonFile == null)
                return assignments;

            var monsterCodes = ParseConditionMonsterCodes(
                dungeonFile.BossRoomEntranceCondition,
                "[hunt monster]");
            if (monsterCodes.Count == 0)
                return assignments;

            var candidates = new List<(byte X, byte Y, int MapId)>();
            foreach (var spec in maze.MapSpecifications)
            {
                if (!string.Equals(spec.Type, "map", StringComparison.OrdinalIgnoreCase)
                    || IsMazePoint(spec.X, spec.Y, maze.StartMap)
                    || IsMazePoint(spec.X, spec.Y, bossPos))
                {
                    continue;
                }

                if (DungeonData.GetMapMonsterConditionSummaryInformation(
                        spec.Index,
                        dungeonId,
                        spec.X,
                        spec.Y,
                        monsterCodes).Count > 0)
                {
                    candidates.Add(((byte)spec.X, (byte)spec.Y, spec.Index));
                }
            }

            if (candidates.Count == 0)
                return assignments;

            var available = new List<(byte X, byte Y, int MapId)>(candidates);
            var logParts = new List<string>();
            foreach (var monsterCode in monsterCodes)
            {
                if (available.Count == 0)
                    available.AddRange(candidates);

                var pick = ServerRandom.Next(available.Count);
                var point = available[pick];
                available.RemoveAt(pick);
                assignments.Add(new MeltdownHelpusHostageAssignment
                {
                    MonsterCode = monsterCode,
                    X = point.X,
                    Y = point.Y,
                });
                logParts.Add($"{monsterCode}@({point.X},{point.Y})#{point.MapId}");
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] condition assignments: " +
                $"dungeon={dungeonId} maze={mazeIndex} " +
                $"assignments={string.Join(",", logParts)}");
            return assignments;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            BuildGentTowerIconGroups(
                int dungeonId,
                int mazeIndex,
                MazeInfo maze,
                int[] bossPos,
                SpecialDungeonRuntime special)
        {
            if (maze?.MapSpecifications == null
                || special?.GentInfiltrateTowerRequired == null
                || special.GentInfiltrateTowerRequired.Count == 0)
            {
                return null;
            }

            var targetCodes = new HashSet<int>(
                special.GentInfiltrateTowerRequired.Keys);
            var points = new List<(byte, byte)>();
            var seen = new HashSet<int>();
            foreach (var spec in maze.MapSpecifications)
            {
                if (!string.Equals(spec.Type, "map", StringComparison.OrdinalIgnoreCase))
                    continue;

                var summary = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    spec.X,
                    spec.Y,
                    mazeIndex,
                    spec.Index,
                    bossPos);
                if (!summary.Monsters.Any(monster => targetCodes.Contains(monster.Code)))
                    continue;

                var key = (spec.X << 16) ^ (spec.Y & 0xFFFF);
                if (seen.Add(key))
                    points.Add(((byte)spec.X, (byte)spec.Y));
            }

            return points.Count > 0
                ? new List<IReadOnlyList<(byte, byte)>> { points }
                : null;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            BuildMinimapIconGroupsFromAssignments(
                IReadOnlyList<MeltdownHelpusHostageAssignment> assignments)
        {
            if (assignments == null || assignments.Count == 0)
                return null;

            var points = new List<(byte, byte)>();
            foreach (var assignment in assignments)
            {
                if (assignment != null)
                    points.Add((assignment.X, assignment.Y));
            }

            return points.Count > 0
                ? new List<IReadOnlyList<(byte, byte)>> { points }
                : null;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            CloneMinimapIconGroups(
                IReadOnlyList<IReadOnlyList<(byte, byte)>> source)
        {
            if (source == null)
                return null;

            var result = new List<IReadOnlyList<(byte, byte)>>();
            foreach (var group in source)
                result.Add(group == null
                    ? Array.Empty<(byte, byte)>()
                    : new List<(byte, byte)>(group));
            return result;
        }

        private static List<MeltdownHelpusHostageAssignment> CloneAssignments(
            IReadOnlyList<MeltdownHelpusHostageAssignment> source)
        {
            var result = new List<MeltdownHelpusHostageAssignment>();
            if (source == null)
                return result;

            foreach (var item in source)
            {
                if (item == null)
                    continue;

                result.Add(new MeltdownHelpusHostageAssignment
                {
                    MonsterCode = item.MonsterCode,
                    X = item.X,
                    Y = item.Y,
                    Rescued = item.Rescued,
                });
            }
            return result;
        }

        private static List<int> ParseConditionMonsterCodes(
            string condition,
            string tag)
        {
            var result = new List<int>();
            var tokens = Tokenize(condition);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!string.Equals(tokens[i], tag, StringComparison.OrdinalIgnoreCase)
                    || i + 1 >= tokens.Count
                    || !int.TryParse(tokens[i + 1], out var count)
                    || count <= 0)
                {
                    continue;
                }

                var pos = i + 2;
                for (var n = 0; n < count && pos < tokens.Count; n++, pos += 3)
                {
                    if (int.TryParse(tokens[pos], out var monsterCode)
                        && monsterCode > 0)
                    {
                        result.Add(monsterCode);
                    }
                }
                break;
            }
            return result;
        }

        private static List<string> Tokenize(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return result;

            foreach (Match match in Regex.Matches(value, "`([^`]*)`|\\S+"))
                result.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            return result;
        }

        private static bool IsMazePoint(int x, int y, int[] point)
            => point != null
                && point.Length >= 2
                && point[0] == x
                && point[1] == y;
    }
}
