using System;

namespace DfoServer.Game.Dungeon
{
    internal abstract class DungeonCommand
    {
        protected DungeonCommand(ushort wireType)
        {
            WireType = wireType;
        }

        internal ushort WireType { get; }
    }

    internal sealed class SummonMonsterDungeonCommand : DungeonCommand
    {
        internal SummonMonsterDungeonCommand(
            ushort wireType,
            ushort conditionalType,
            int monsterCode,
            int stateId,
            int mapId,
            ushort conditionalParam0,
            ushort conditionalParam1,
            byte matchCount)
            : base(wireType)
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

    internal sealed class TimerModifyInfoDungeonCommand : DungeonCommand
    {
        internal TimerModifyInfoDungeonCommand(ushort wireType, byte[] payload)
            : base(wireType)
        {
            Payload = Clone(payload);
        }

        internal byte[] Payload { get; }

        private static byte[] Clone(byte[] value)
            => value == null || value.Length == 0
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
    }

    internal sealed class SeaChaseResultDungeonCommand : DungeonCommand
    {
        internal SeaChaseResultDungeonCommand(ushort wireType, int result)
            : base(wireType)
        {
            Result = result;
        }

        internal int Result { get; }
    }

    internal sealed class SeaChaseObservedDungeonCommand : DungeonCommand
    {
        internal SeaChaseObservedDungeonCommand(
            ushort wireType,
            byte[] payload)
            : base(wireType)
        {
            Payload = Clone(payload);
        }

        internal byte[] Payload { get; }

        private static byte[] Clone(byte[] value)
            => value == null || value.Length == 0
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
    }

    internal sealed class NpcItemDropDungeonCommand : DungeonCommand
    {
        internal NpcItemDropDungeonCommand(ushort wireType, byte[] payload)
            : base(wireType)
        {
            Payload = Clone(payload);
        }

        internal byte[] Payload { get; }
        internal bool HasUnexpectedPayload => Payload.Length != 0;

        private static byte[] Clone(byte[] value)
            => value == null || value.Length == 0
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
    }

    internal sealed class BreakTrapResultDungeonCommand : DungeonCommand
    {
        internal BreakTrapResultDungeonCommand(ushort wireType, byte[] payload)
            : base(wireType)
        {
            Payload = Clone(payload);
        }

        internal byte[] Payload { get; }

        private static byte[] Clone(byte[] value)
            => value == null || value.Length == 0
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
    }
}
