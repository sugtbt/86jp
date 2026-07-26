using System;

namespace DfoServer.Network.Parsers.Auction
{
    public sealed class AuctionAskAveragePriceRequest
    {
        public byte QueryMode { get; set; }

        public int ItemTemplateId { get; set; }

        public byte[] OpaqueItemDescriptor { get; set; }
    }

    public static class AuctionAskAveragePriceRequestParser
    {
        public const int BodyLength = 19;
        private const int ItemTemplateIdOffset = 1;
        private const int OpaqueDescriptorOffset = 5;
        private const int OpaqueDescriptorLength = 14;

        public static bool TryParse(byte[] body, out AuctionAskAveragePriceRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var queryMode = body[0];
            if (queryMode > 1)
                return false;

            var itemTemplateId = BitConverter.ToInt32(body, ItemTemplateIdOffset);
            if (itemTemplateId <= 0)
                return false;

            var opaqueItemDescriptor = new byte[OpaqueDescriptorLength];
            Buffer.BlockCopy(
                body,
                OpaqueDescriptorOffset,
                opaqueItemDescriptor,
                0,
                opaqueItemDescriptor.Length);

            request = new AuctionAskAveragePriceRequest
            {
                QueryMode = queryMode,
                ItemTemplateId = itemTemplateId,
                OpaqueItemDescriptor = opaqueItemDescriptor,
            };
            return true;
        }
    }
}
