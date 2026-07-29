using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class ExpertJobInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00CD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var info = snapshot.InitializationSnapshot.ExpertJobInfo;
            var writer = new GamePacketWriter();
            writer.WriteByte(info.State0);
            writer.WriteByte(info.Mode);

            if (info.Mode == 1 || info.Mode == 2 || info.Mode == 4)
            {
                writer.WriteByte((byte)info.Entries.Count);
                foreach (var entry in info.Entries)
                    writer.WriteInt32(entry);
            }
            else if (info.Mode == 3)
            {
                writer.WriteInt32(info.DisjointMachineGrade);
                writer.WriteInt32(info.DisjointMachineEndurance);
            }

            body = writer.ToArray();
            return true;
        }
    }
}
