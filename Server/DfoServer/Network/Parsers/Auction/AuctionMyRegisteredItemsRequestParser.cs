namespace DfoServer.Network.Parsers.Auction
{
    internal sealed class AuctionMyRegisteredItemsRequest
    {
        public byte Mode { get; set; }
    }

    internal static class AuctionMyRegisteredItemsRequestParser
    {
        private const int RequestSize = 1;

        public static bool TryParse(
            byte[] body,
            out AuctionMyRegisteredItemsRequest request)
        {
            request = null;
            if (body == null
                || body.Length != RequestSize
                || body[0] > 1)
            {
                return false;
            }

            request = new AuctionMyRegisteredItemsRequest
            {
                Mode = body[0],
            };
            return true;
        }
    }
}
