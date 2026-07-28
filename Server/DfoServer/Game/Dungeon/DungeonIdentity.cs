using System;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonIdentityGenerator
    {
        private static long _instanceId;
        private static long _runId;
        private static long _roomId;

        internal static long NextInstanceId() => Next(ref _instanceId);
        internal static long NextRunId() => Next(ref _runId);
        internal static long NextRoomId() => Next(ref _roomId);

        private static long Next(ref long value)
        {
            var next = Interlocked.Increment(ref value);
            if (next > 0)
                return next;

            throw new InvalidOperationException("Dungeon identity sequence exhausted.");
        }
    }

    public readonly struct DungeonRunIdentity : IEquatable<DungeonRunIdentity>
    {
        public DungeonRunIdentity(
            long partyDungeonInstanceId,
            long runId,
            long runGeneration)
        {
            PartyDungeonInstanceId = partyDungeonInstanceId;
            RunId = runId;
            RunGeneration = runGeneration;
        }

        public long PartyDungeonInstanceId { get; }
        public long RunId { get; }
        public long RunGeneration { get; }
        public bool IsValid => PartyDungeonInstanceId > 0 && RunId > 0 && RunGeneration > 0;

        public bool Equals(DungeonRunIdentity other) =>
            PartyDungeonInstanceId == other.PartyDungeonInstanceId
            && RunId == other.RunId
            && RunGeneration == other.RunGeneration;

        public override bool Equals(object obj) =>
            obj is DungeonRunIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(PartyDungeonInstanceId, RunId, RunGeneration);
    }

    public readonly struct DungeonRoomIdentity : IEquatable<DungeonRoomIdentity>
    {
        public DungeonRoomIdentity(DungeonRunIdentity run, long roomInstanceId)
        {
            Run = run;
            RoomInstanceId = roomInstanceId;
        }

        public DungeonRunIdentity Run { get; }
        public long RoomInstanceId { get; }
        public bool IsValid => Run.IsValid && RoomInstanceId > 0;

        public bool Equals(DungeonRoomIdentity other) =>
            Run.Equals(other.Run) && RoomInstanceId == other.RoomInstanceId;

        public override bool Equals(object obj) =>
            obj is DungeonRoomIdentity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Run, RoomInstanceId);
    }
}
