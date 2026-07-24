using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public enum HellPartyPhase
    {
        None = 0,
        WaitingStart = 1,
        Started = 2,
        Complete = 3,
    }

    public struct RoomKey : IEquatable<RoomKey>
    {
        public int X;
        public int Y;
        public int OverrideMapId;

        public RoomKey(int x, int y, int overrideMapId)
        {
            X = x;
            Y = y;
            OverrideMapId = overrideMapId;
        }

        public bool Equals(RoomKey other) =>
            X == other.X && Y == other.Y && OverrideMapId == other.OverrideMapId;

        public override bool Equals(object obj) => obj is RoomKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X * 397;
                hash = (hash ^ Y) * 397;
                hash ^= OverrideMapId;
                return hash;
            }
        }
    }

    public class RoomState
    {
        public GameWorld.Dungeon.MazeSumInfo Maze;
        public ushort FirstSeqId;
        public ushort MonsterCount;
        public HashSet<ushort> KilledSeqIds;
        public uint Seed;
        public DnfLcg Lcg;
        public bool IsHellPartyRoom;
        public bool HellPartyVeryDifficult;
        public int HellPartyPillarObjectCode;
        public int HellPartySpawnX;
        public int HellPartySpawnY;
        public bool PetExperienceGranted;
        public List<GameWorld.Dungeon.HellPartyWaveInfo> HellPartyWaves;
        public HellPartyPhase HellPartyPhase;
        // 深渊小队剩余成员数。key 为 group index，value 为该 group 尚未收到死亡包的成员数。
        public Dictionary<int, int> HellPartyGroupRemaining;
        public bool TimeSpiralHiddenBossActive;
        public ushort TimeSpiralHiddenBossSeqId;
        public int TimeSpiralHiddenBossCode;
        public string TimeSpiralHiddenBossSource;

        public bool IsCleared => KilledSeqIds.Count >= MonsterCount && MonsterCount > 0;
    }
}
