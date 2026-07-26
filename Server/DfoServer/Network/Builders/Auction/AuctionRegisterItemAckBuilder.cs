namespace DfoServer.Network.Builders.Auction
{
    internal static class AuctionRegisterItemAckBuilder
    {
        public static byte[] BuildSuccess(int characterId)
            => new byte[]
            {
                0x01,
                0x00,
            };

        public static byte[] BuildFailure(
            byte reason,
            int characterId)
            => new byte[]
            {
                0x00,
                0x00,
            };
    }
}
