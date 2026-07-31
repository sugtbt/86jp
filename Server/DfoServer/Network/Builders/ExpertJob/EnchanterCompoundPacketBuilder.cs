using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Builders.ExpertJob
{
    internal static class EnchanterCompoundPacketBuilder
    {
        internal static byte[] BuildSuccess(EnchanterCompoundResult result)
        {
            if (result == null || result.ErrorCode != 0 || result.Outputs.Count > byte.MaxValue)
                throw new ArgumentException("a valid compound result is required", nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte((byte)result.Outputs.Count);
            foreach (var output in result.Outputs)
            {
                writer.WriteInt32(output.ItemId);
                writer.WriteInt32(output.Count);
            }
            writer.WriteInt32(result.SuccessCount);
            writer.WriteInt32(result.FailureCount);
            writer.WriteByte(0);
            return writer.ToArray();
        }

        internal static byte[] BuildError(byte errorCode)
            => new[] { (byte)0, errorCode };
    }
}
