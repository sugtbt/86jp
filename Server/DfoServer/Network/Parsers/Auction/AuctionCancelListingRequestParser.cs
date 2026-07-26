using System;

namespace DfoServer.Network.Parsers.Auction
{
    internal sealed class AuctionCancelListingRequest
    {
        public byte Mode { get; set; }
        public long ListingId { get; set; }
    }

    internal static class AuctionCancelListingRequestParser
    {
        private const int RequestSize = 9;
        private const int ListingIdOffset = 1;

        public static bool TryParse(
            byte[] body,
            out AuctionCancelListingRequest request)
        {
            request = null;
            if (body == null
                || body.Length != RequestSize
                || body[0] > 1)
            {
                return false;
            }

            var listingId = BitConverter.ToInt64(body, ListingIdOffset);
            if (listingId <= 0)
                return false;

            request = new AuctionCancelListingRequest
            {
                Mode = body[0],
                ListingId = listingId,
            };
            return true;
        }
    }
}
