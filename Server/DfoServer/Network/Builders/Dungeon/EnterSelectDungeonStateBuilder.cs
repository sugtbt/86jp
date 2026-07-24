using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class EnterSelectDungeonStateBuilder
    {
        public static byte[] BuildUserState(PlayerContext player)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteUInt16(player.UserId);
            writer.WriteByte(player.UserState);
            return writer.ToArray();
        }

        public static byte[] BuildEnterSelectDungeon(
            PlayerContext player,
            int towerOfDespairFloor)
        {
            var writer = new GamePacketWriter();

            writer.WriteInt32(0x01);
            writer.WriteUInt16(0x0000);
            writer.WriteByte(0x01);
            writer.WriteUInt16(player.UserId);
            writer.WriteByte(0x00);
            writer.WriteInt32(0x00);
            // Client NOTI 0x001B reads this u16 at body offset 14 and uses it
            // directly for the Tower of Despair card's "(N floor)" suffix.
            writer.WriteUInt16((ushort)towerOfDespairFloor);
            writer.WriteZeroBytes(3);
            return writer.ToArray();
        }
    }
}
