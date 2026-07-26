using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionListCommand
    {
        public InventoryListType SourceListType { get; set; }
        public short SourceSlotIndex { get; set; }
        public int ExpectedItemTemplateId { get; set; }
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
    }

    internal sealed class AuctionListResult
    {
        public AuctionApplicationError Error { get; set; }
        public long ListingId { get; set; }
        public long TotalPrice { get; set; }
        public long DepositAmount { get; set; }
        public bool Success =>
            Error == AuctionApplicationError.None && ListingId > 0;
    }

    internal sealed class AuctionReturnResult
    {
        public AuctionApplicationError Error { get; set; }
        public long ListingId { get; set; }
        public AuctionListingStatus Status { get; set; }
        public bool Success =>
            Error == AuctionApplicationError.None && ListingId > 0;
    }

    internal sealed class AuctionExpirationScanResult
    {
        public int CandidateCount { get; set; }
        public int CompletedCount { get; set; }
        public bool SkippedBecauseRunning { get; set; }
    }

    internal enum AuctionApplicationError
    {
        None = 0,
        InvalidLease,
        OwnershipMismatch,
        InvalidSourceList,
        InvalidSourceSlot,
        ItemNotFound,
        ItemMismatch,
        InvalidQuantity,
        NotEnoughQuantity,
        NonStackableQuantity,
        TradeRestricted,
        SortLocked,
        EquipmentLocked,
        ItemExpired,
        InvalidTerms,
        AuctionGoldLimitExceeded,
        ActiveListingLimitReached,
        InsufficientDepositGold,
        InventoryMutationFailed,
        PersistenceFailed,
        ListingNotFound,
        NotOwner,
        ListingNotActive,
        VersionConflict,
        CancellationWindowExpired,
        NotExpired,
        MailRejected,
    }

    internal readonly struct AuctionItemEligibilityResult
    {
        private AuctionItemEligibilityResult(
            ItemCore sourceSnapshot,
            ItemCore itemSnapshot,
            AuctionApplicationError error)
        {
            SourceSnapshot = sourceSnapshot;
            ItemSnapshot = itemSnapshot;
            Error = error;
        }

        public ItemCore SourceSnapshot { get; }
        public ItemCore ItemSnapshot { get; }
        public AuctionApplicationError Error { get; }
        public bool Success =>
            Error == AuctionApplicationError.None
            && SourceSnapshot != null
            && ItemSnapshot != null;

        public static AuctionItemEligibilityResult Accepted(
            ItemCore sourceSnapshot,
            ItemCore itemSnapshot)
            => new AuctionItemEligibilityResult(
                sourceSnapshot,
                itemSnapshot,
                AuctionApplicationError.None);

        public static AuctionItemEligibilityResult Rejected(
            AuctionApplicationError error)
            => new AuctionItemEligibilityResult(null, null, error);
    }
}
