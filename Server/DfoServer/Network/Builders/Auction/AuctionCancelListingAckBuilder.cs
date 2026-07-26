using System;

namespace DfoServer.Network.Builders.Auction
{
    internal static class AuctionCancelListingAckBuilder
    {
        public static byte[] BuildSuccess(byte mode)
            => Build(0x01, mode);

        public static byte[] BuildFailure(byte mode)
            => Build(0x00, mode);

        private static byte[] Build(byte result, byte mode)
        {
            if (mode > 1)
                throw new ArgumentOutOfRangeException(nameof(mode));

            return new[]
            {
                result,
                mode,
            };
        }
    }
}
