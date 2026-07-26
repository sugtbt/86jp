using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Auction
{
    internal sealed class AuctionRegisterItemRequest
    {
        public byte PayType { get; set; }
        public InventoryListType SourceListType { get; set; }
        public short SourceSlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public int Quantity { get; set; }
        public int BidPrice { get; set; }
        public int InstantPrice { get; set; }
        public int UnitPrice { get; set; }
        public byte[] RoiCategories { get; set; }
        public byte[] OpaqueTrailer { get; set; }
    }

    internal static class AuctionRegisterItemRequestParser
    {
        private const int BodyLength = 37;
        private const int RoiCategoriesOffset = 24;
        private const int RoiCategoriesLength = 9;
        private const int OpaqueTrailerOffset = 33;
        private const int OpaqueTrailerLength = 4;

        public static bool TryParse(
            byte[] body,
            out AuctionRegisterItemRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var payType = body[0];
            var inventoryType = body[1];
            var slotIndex = BitConverter.ToInt16(body, 2);
            var itemTemplateId = BitConverter.ToInt32(body, 4);
            var quantity = BitConverter.ToInt32(body, 8);
            var bidPrice = BitConverter.ToInt32(body, 12);
            var instantPrice = BitConverter.ToInt32(body, 16);
            var unitPrice = BitConverter.ToInt32(body, 20);

            if (payType != 0
                || inventoryType != (byte)InventoryListType.Main
                || slotIndex < InventoryService.MainSlotStart
                || slotIndex > InventoryService.MainSlotEnd
                || itemTemplateId <= 0
                || quantity <= 0
                || bidPrice != -1
                || instantPrice <= 0
                || unitPrice <= 0
                || instantPrice != unitPrice)
            {
                return false;
            }

            request = new AuctionRegisterItemRequest
            {
                PayType = payType,
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = slotIndex,
                ItemTemplateId = itemTemplateId,
                Quantity = quantity,
                BidPrice = bidPrice,
                InstantPrice = instantPrice,
                UnitPrice = unitPrice,
                RoiCategories = Copy(
                    body,
                    RoiCategoriesOffset,
                    RoiCategoriesLength),
                OpaqueTrailer = Copy(
                    body,
                    OpaqueTrailerOffset,
                    OpaqueTrailerLength),
            };
            return true;
        }

        private static byte[] Copy(
            byte[] source,
            int offset,
            int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }
    }
}
