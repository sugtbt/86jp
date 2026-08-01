using DfoServer.Game.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct DungeonRoomPoint
    {
        public DungeonRoomPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
            }
        }

        public override bool Equals(object obj)
        {
            return obj is DungeonRoomPoint other && other.X == X && other.Y == Y;
        }
    }

    internal static class DungeonRoomTopology
    {
        private static readonly object MazeCacheLock = new object();
        private static readonly Dictionary<string, MazeInfo> MazeCache =
            new Dictionary<string, MazeInfo>(StringComparer.Ordinal);
        private static readonly object PvfCoordinateCacheLock = new object();
        private static readonly Dictionary<string, DungeonRoomPoint[]> PvfCoordinateCache =
            new Dictionary<string, DungeonRoomPoint[]>(StringComparer.Ordinal);

        public static bool TryResolveMoveTarget(
            int dungeonId,
            int mazeIndex,
            RoomKey currentRoom,
            int requestedX,
            int requestedY,
            int[] bossMapPos,
            out DungeonRoomPoint target,
            out string reason)
        {
            var maze = GetCachedMaze(dungeonId, mazeIndex);
            return TryResolveMoveTarget(
                dungeonId,
                mazeIndex,
                maze,
                currentRoom,
                requestedX,
                requestedY,
                bossMapPos,
                out target,
                out reason);
        }

        public static bool TryResolveMoveTarget(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            RoomKey currentRoom,
            int requestedX,
            int requestedY,
            int[] bossMapPos,
            out DungeonRoomPoint target,
            out string reason)
        {
            var current = new DungeonRoomPoint(currentRoom.X, currentRoom.Y);
            var requested = new DungeonRoomPoint(requestedX, requestedY);
            var cells = BuildMazeCells(dungeonId, mazeIndex, maze);
            cells.Add(current);
            AddBossCell(cells, bossMapPos);

            if (cells.Contains(requested))
            {
                target = requested;
                reason = "known room";
                return true;
            }

            if (TryFindDirectionalNeighbor(current, requested, cells, out target, out reason))
                return true;

            target = default(DungeonRoomPoint);
            reason = "outside known dungeon room coordinates";
            return false;
        }

        private static HashSet<DungeonRoomPoint> BuildMazeCells(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            bool includePvfCoordinates = true)
        {
            var cells = new HashSet<DungeonRoomPoint>();
            if (maze == null)
                return cells;

            AddGreedCells(maze, cells);

            if (maze.MapSpecifications != null)
            {
                foreach (var spec in maze.MapSpecifications)
                    cells.Add(new DungeonRoomPoint(spec.X, spec.Y));
            }

            if (maze.StartMap != null && maze.StartMap.Length >= 2)
                cells.Add(new DungeonRoomPoint(maze.StartMap[0], maze.StartMap[1]));
            if (maze.BossMap != null && maze.BossMap.Length >= 2)
            {
                for (var i = 0; i + 1 < maze.BossMap.Length; i += 2)
                    cells.Add(new DungeonRoomPoint(maze.BossMap[i], maze.BossMap[i + 1]));
            }

            if (includePvfCoordinates)
            {
                foreach (var coordinate in GetCachedPvfCoordinates(dungeonId, mazeIndex, maze))
                {
                    if (!IsWithinMazeBounds(maze, coordinate))
                        continue;

                    cells.Add(coordinate);
                }
            }

            return cells;
        }

        internal static int CountConfiguredRooms(MazeInfo maze)
        {
            var count = BuildMazeCells(
                dungeonId: 0,
                mazeIndex: -1,
                maze,
                includePvfCoordinates: false).Count;
            return Math.Max(1, count);
        }

        private static void AddBossCell(HashSet<DungeonRoomPoint> cells, int[] bossMapPos)
        {
            if (bossMapPos != null && bossMapPos.Length >= 2)
                cells.Add(new DungeonRoomPoint(bossMapPos[0], bossMapPos[1]));
        }

        private static bool TryFindDirectionalNeighbor(
            DungeonRoomPoint current,
            DungeonRoomPoint requested,
            HashSet<DungeonRoomPoint> cells,
            out DungeonRoomPoint target,
            out string reason)
        {
            var deltaX = requested.X - current.X;
            var deltaY = requested.Y - current.Y;
            if (Math.Abs(deltaX) + Math.Abs(deltaY) != 1)
            {
                target = default(DungeonRoomPoint);
                reason = string.Empty;
                return false;
            }

            DungeonRoomPoint? best = null;
            var bestDistance = int.MaxValue;
            foreach (var candidate in cells)
            {
                if (candidate.Equals(current))
                    continue;

                int distance;
                if (deltaX > 0 && candidate.Y == current.Y && candidate.X > current.X)
                    distance = candidate.X - current.X;
                else if (deltaX < 0 && candidate.Y == current.Y && candidate.X < current.X)
                    distance = current.X - candidate.X;
                else if (deltaY > 0 && candidate.X == current.X && candidate.Y > current.Y)
                    distance = candidate.Y - current.Y;
                else if (deltaY < 0 && candidate.X == current.X && candidate.Y < current.Y)
                    distance = current.Y - candidate.Y;
                else
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (!best.HasValue)
            {
                target = default(DungeonRoomPoint);
                reason = string.Empty;
                return false;
            }

            target = best.Value;
            reason = "nearest directional room";
            return true;
        }

        private static IEnumerable<DungeonRoomPoint> GetCachedPvfCoordinates(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze)
        {
            var key = dungeonId.ToString() + ":" + mazeIndex.ToString();
            lock (PvfCoordinateCacheLock)
            {
                if (PvfCoordinateCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var coordinates = GameWorld.Dungeon.GetDungeonRoomCoordinates(dungeonId, mazeIndex, maze)
                .Select(coordinate => new DungeonRoomPoint(coordinate.X, coordinate.Y))
                .ToArray();

            lock (PvfCoordinateCacheLock)
            {
                PvfCoordinateCache[key] = coordinates;
            }

            return coordinates;
        }

        private static MazeInfo GetCachedMaze(int dungeonId, int mazeIndex)
        {
            var key = dungeonId.ToString() + ":" + mazeIndex.ToString();
            lock (MazeCacheLock)
            {
                if (MazeCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var maze = GameWorld.Dungeon.GetDungeonMaze(dungeonId, mazeIndex);
            lock (MazeCacheLock)
            {
                MazeCache[key] = maze;
            }

            return maze;
        }

        private static bool IsWithinMazeBounds(MazeInfo maze, DungeonRoomPoint point)
        {
            if (maze.Width <= 0 || maze.Height <= 0)
                return true;

            return point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height;
        }

        internal static void AddGreedCells(MazeInfo maze, HashSet<DungeonRoomPoint> cells)
        {
            if (maze.Width <= 0 || maze.Height <= 0 || string.IsNullOrWhiteSpace(maze.Greed))
                return;

            var values = maze.Greed
                .Where(ch => !char.IsWhiteSpace(ch) && ch != '`' && ch != ',')
                .ToArray();
            var cellCount = maze.Width * maze.Height;
            var charsPerCell = values.Length >= cellCount * 2 ? 2 : 1;
            if (values.Length < cellCount * charsPerCell)
                return;

            for (var y = 0; y < maze.Height; y++)
            {
                for (var x = 0; x < maze.Width; x++)
                {
                    var valueIndex = (y * maze.Width + x) * charsPerCell;
                    var first = values[valueIndex];
                    var second = charsPerCell == 2 ? values[valueIndex + 1] : '\0';
                    if (IsOpenGreedCell(first, second, charsPerCell))
                        cells.Add(new DungeonRoomPoint(x, y));
                }
            }
        }

        private static bool IsOpenGreedCell(char first, char second, int charsPerCell)
        {
            if (charsPerCell == 2)
            {
                if ((first == 'A' && second == 'A')
                    || (first == '0' && second == '0')
                    || (first == '.' && second == '.')
                    || ((first == 'x' || first == 'X')
                        && (second == 'x' || second == 'X')))
                {
                    return false;
                }
            }

            return first != '0' && first != '.' && first != 'x' && first != 'X';
        }

        internal static DungeonRoomProgress GetCurrentRoomProgress(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run != null && run.Tower == null)
            {
                RoomState roomState;
                lock (run.SyncRoot)
                    run.RoomStates.TryGetValue(run.RoomKey, out roomState);
                if (roomState?.InstanceRoom != null)
                {
                    return GetRoomProgress(
                        session,
                        roomState.InstanceRoom.CaptureKilledActorSequenceIds());
                }
            }

            return GetRoomProgress(session, run?.RoomKilledSeqIds);
        }

        internal static DungeonRoomProgress GetRoomProgress(
            EnhancedClientSession session,
            ISet<ushort> killedSeqIds)
        {
            var run = session?.Player?.CurrentRun;
            var monsters = run?.RoomMonsters ?? Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
            var killed = killedSeqIds ?? new HashSet<ushort>();
            var startSeq = run?.RoomStartSequence ?? 0;

            int trackable = 0, killedTrackable = 0, remaining = 0;
            int blocking = 0, blockingRemaining = 0;
            int apc = 0, normal = 0, killedNormal = 0;

            for (var i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (!IsTrackedForRoomProgress(monster.Type)) continue;

                trackable++;
                if (monster.Type >= 5) apc++; else normal++;
                var isBlocking = IsBlockingForRoomClear(run, monster);
                if (isBlocking) blocking++;

                var seqId = (ushort)(startSeq + i);
                if (killed.Contains(seqId))
                {
                    killedTrackable++;
                    if (monster.Type < 5) killedNormal++;
                    continue;
                }

                remaining++;
                if (isBlocking) blockingRemaining++;
            }

            return new DungeonRoomProgress(
                trackable, killedTrackable, remaining,
                blocking, blockingRemaining,
                apc, normal, killedNormal);
        }

        // ShouldClearAfterApcDialog 已删除: df_game_r 没有"对话触发通关"路径,
        // 通关判定完全由 kill_monster 内的 check_grid_clear + check_end_point/ClearCondition 驱动。

        /// <summary>
        /// 房间通关判定的唯一实现 —— 击杀主路径与组队击杀 relay 共用, 逻辑绝不允许写两份
        /// (曾因两份逻辑一份修了一份漏, 出过 blockingCount>0 门控劈叉)。
        /// 真机 check_grid_clear(0x830A0E8): 所有 spawnType==100 的 blocking 怪死光即通过。
        /// 个别副本可在 IsBlockingForRoomClear 中追加其专用房间目标；空房间(0 blocking)也算通过。
        /// 调用方必须已持有 run.SyncRoot(读 RoomMonsters/RoomKilledSeqIds)。
        /// </summary>
        internal static bool ComputeRoomClearedLocked(Game.Dungeon.DungeonRun run, out int blockingCount, out int killedBlockingCount)
        {
            blockingCount = 0;
            killedBlockingCount = 0;
            var monsters = run.RoomMonsters;
            if (monsters == null)
                return true;
            for (var i = 0; i < monsters.Count; i++)
            {
                if (!IsBlockingForRoomClear(run, monsters[i])) continue;
                blockingCount++;
                var sid = (ushort)(run.RoomStartSequence + i);
                if (run.RoomKilledSeqIds.Contains(sid))
                    killedBlockingCount++;
            }
            return killedBlockingCount >= blockingCount;
        }

        internal static bool IsTrackedForRoomProgress(byte actorType) =>
            actorType != 9;

        internal static bool TryCommitCurrentRoomClear(
            Game.Dungeon.DungeonRun run,
            DungeonEventEnvelope source,
            ushort completingSequenceId,
            out int blockingCount,
            out int killedBlockingCount,
            out DungeonEventEnvelope clearSource)
        {
            blockingCount = 0;
            killedBlockingCount = 0;
            clearSource = null;
            if (run == null || source == null)
                return false;

            RoomState roomState;
            lock (run.SyncRoot)
                run.RoomStates.TryGetValue(run.RoomKey, out roomState);

            if (run.Tower == null && roomState?.InstanceRoom != null)
            {
                var commit = roomState.InstanceRoom.TryCommitClearFromActorDeaths(
                    actor => IsBlockingForRoomClear(run, actor),
                    source,
                    completingSequenceId);
                blockingCount = commit.BlockingCount;
                killedBlockingCount = commit.KilledBlockingCount;
                clearSource = commit.Source;
                return commit.IsCleared;
            }

            lock (run.SyncRoot)
            {
                var cleared = ComputeRoomClearedLocked(
                    run,
                    out blockingCount,
                    out killedBlockingCount);
                if (cleared)
                    clearSource = source;
                return cleared;
            }
        }

        private static bool IsBlockingForRoomClear(
            Game.Dungeon.DungeonRun run,
            GameWorld.Dungeon.MonsterSumInfo actor)
        {
            return actor.IsBlocking
                || IsTowerOfDespairRequiredTarget(run, actor);
        }

        private static bool IsTowerOfDespairRequiredTarget(
            Game.Dungeon.DungeonRun run,
            GameWorld.Dungeon.MonsterSumInfo actor)
        {
            return run != null
                && actor.Type >= (byte)ApcAIType.Normal
                && actor.Type <= (byte)ApcAIType.Boss
                && actor.Faction == ApcFaction.Monster
                && GameWorld.Dungeon.TryGetTowerOfDespairFloor(
                    run.DungeonId,
                    out _);
        }
    }

    internal readonly struct DungeonRoomProgress
    {
        internal DungeonRoomProgress(
            int trackableCount, int killedTrackableCount, int remainingCount,
            int blockingCount, int blockingRemainingCount,
            int apcCount, int normalCount, int killedNormalCount)
        {
            TrackableCount = trackableCount;
            KilledTrackableCount = killedTrackableCount;
            RemainingCount = remainingCount;
            BlockingCount = blockingCount;
            BlockingRemainingCount = blockingRemainingCount;
            ApcCount = apcCount;
            NormalCount = normalCount;
            KilledNormalCount = killedNormalCount;
        }

        internal int TrackableCount { get; }
        internal int KilledTrackableCount { get; }
        internal int RemainingCount { get; }
        internal int BlockingCount { get; }
        internal int BlockingRemainingCount { get; }
        internal int ApcCount { get; }
        internal int NormalCount { get; }
        internal int KilledNormalCount { get; }
        internal bool RoomPassable => BlockingRemainingCount == 0;
    }
}
