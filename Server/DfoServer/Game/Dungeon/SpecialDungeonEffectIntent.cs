using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum SpecialDungeonEffectKind
    {
        GaugeChanged,
        BuffAddedAndActivated,
        BuffsCleared,
        BossEntranceMinimap,
        PassGate,
        StrongWarlordSelected,
        SummonMonsterResponse,
        CommandSuccessAck,
        CancelMechanismTimer,
        ResetTimeCrackGauge,
        RecordSeaChaseBuffs,
    }

    internal sealed class SpecialDungeonEffectIntent
    {
        internal SpecialDungeonEffectKind Kind { get; set; }
        internal string Reason { get; set; }
        internal int Value { get; set; }
        internal ushort WireType { get; set; }
        internal IReadOnlyList<int> BuffIds { get; set; }
        internal IReadOnlyList<int> ActiveBuffIds { get; set; }
        internal IReadOnlyList<(byte X, byte Y, int MonsterCode)> MinimapEntries
        {
            get;
            set;
        }
        internal int StateId { get; set; }
        internal int MonsterCode { get; set; }
        internal ushort MonsterLevel { get; set; }
        internal int MapId { get; set; }
        internal int LocalIndex { get; set; }
    }
}
