namespace DfoServer.Network.Builders.Auction
{
    public static class AuctionAveragePriceAckBuilder
    {
        public const int BodyLength = 30;
        private const int PriceFieldCount = 7;

        public static byte[] BuildNoHistory()
        {
            var writer = new GamePacketWriter();

            // Current 86JP capture proves the request type and 19-byte request body.
            // A symbolized comparison server proves this client-facing shape:
            // success byte, one opaque byte, then seven little-endian int32 values.
            // PR1 has no completed-auction history, so every numeric sample is zero.
            writer.WriteByte(0x01);
            writer.WriteByte(0x00);
            for (var index = 0; index < PriceFieldCount; index++)
                writer.WriteInt32(0);
            return writer.ToArray();
        }
    }
}
