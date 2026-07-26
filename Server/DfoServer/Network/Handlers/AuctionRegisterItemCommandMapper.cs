using DfoServer.Game.Auction;
using DfoServer.Network.Parsers.Auction;

namespace DfoServer.Network.Handlers
{
    internal static class AuctionRegisterItemCommandMapper
    {
        public static AuctionListCommand Map(
            AuctionRegisterItemRequest request)
        {
            if (request == null)
                return null;

            return new AuctionListCommand
            {
                SourceListType = request.SourceListType,
                SourceSlotIndex = request.SourceSlotIndex,
                ExpectedItemTemplateId = request.ItemTemplateId,
                Quantity = request.Quantity,
                UnitPrice = request.UnitPrice,
            };
        }
    }
}
