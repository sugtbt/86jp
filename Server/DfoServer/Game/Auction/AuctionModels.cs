using System;

namespace DfoServer.Game.Auction
{
    internal enum AuctionListingStatus
    {
        Active = 0,
        Cancelled = 1,
        Expired = 2,
        Sold = 3,
    }

    internal sealed class AuctionListingDraft
    {
        public int SellerAccountId { get; set; }
        public int SellerCharacterId { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public int ItemId { get; set; }
        public int ItemKind { get; set; }
        public AuctionListingTerms Terms { get; set; }
        public byte[] ItemCore { get; set; }
    }

    internal sealed class AuctionListingRecord
    {
        public long ListingId { get; set; }
        public int SellerAccountId { get; set; }
        public int SellerCharacterId { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public int ItemId { get; set; }
        public int ItemKind { get; set; }
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long TotalPrice { get; set; }
        public long DepositAmount { get; set; }
        public AuctionListingStatus Status { get; set; }
        public long CreatedAtUnixSeconds { get; set; }
        public long ExpiresAtUnixSeconds { get; set; }
        public long UpdatedAtUnixSeconds { get; set; }
        public int Version { get; set; }
    }

    internal sealed class AuctionEscrowItemRecord
    {
        public long ListingId { get; set; }
        public byte[] ItemCore { get; set; }
        public int Quantity { get; set; }
        public string ReturnSourceKey { get; set; }
    }

    internal sealed class AuctionListingBundle
    {
        public AuctionListingRecord Listing { get; set; }
        public AuctionEscrowItemRecord Escrow { get; set; }
    }
}
