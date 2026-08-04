using System;
using DfoServer.Game.Characters;
using DfoServer.GameWorld;

namespace DfoServer.Network
{
    public sealed class GameChannelSpawn
    {
        public GameChannelSpawn(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction,
            byte areaState,
            bool isTransient)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
            AreaState = areaState;
            IsTransient = isTransient;
        }

        public byte TownId { get; }

        public byte AreaId { get; }

        public short X { get; }

        public short Y { get; }

        public byte Direction { get; }

        public byte AreaState { get; }

        public bool IsTransient { get; }

        public void ApplyTo(CharacterRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            record.TownId = TownId;
            record.AreaId = AreaId;
            record.PosX = X;
            record.PosY = Y;
            record.Direction = Direction;
            record.AreaState = AreaState;
        }
    }

    public static class GameChannelSpawnPolicy
    {
        public static GameChannelSpawn Resolve(
            int listenerGamePort,
            int persistedTownId)
        {
            var townId = persistedTownId > 0
                ? persistedTownId
                : 1;
            var gate = Town.GetCeraRoomInfo(townId);
            if (gate.Town <= 0)
                throw new InvalidOperationException(
                    $"Town {townId} has no Seria-room gate.");

            return new GameChannelSpawn(
                gate.Town,
                gate.Area,
                gate.X,
                gate.Y,
                direction: 5,
                areaState: 3,
                isTransient: false);
        }

        public static bool ShouldPersistPosition(int listenerGamePort)
        {
            return true;
        }
    }
}
