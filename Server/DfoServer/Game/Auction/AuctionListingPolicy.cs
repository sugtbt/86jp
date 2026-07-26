using System;

namespace DfoServer.Game.Auction
{
    internal enum AuctionListingRuleError
    {
        None = 0,
        InvalidUnitPrice,
        InvalidQuantity,
        InvalidTimestamp,
        PriceOverflow,
    }

    internal sealed class AuctionListingTerms
    {
        public long UnitPrice { get; set; }
        public int Quantity { get; set; }
        public long TotalPrice { get; set; }
        public long DepositAmount { get; set; }
        public long CreatedAtUnixSeconds { get; set; }
        public long ExpiresAtUnixSeconds { get; set; }
    }

    internal readonly struct AuctionListingTermsResult
    {
        private AuctionListingTermsResult(
            AuctionListingTerms terms,
            AuctionListingRuleError error)
        {
            Terms = terms;
            Error = error;
        }

        public AuctionListingTerms Terms { get; }
        public AuctionListingRuleError Error { get; }
        public bool Success =>
            Terms != null && Error == AuctionListingRuleError.None;

        public static AuctionListingTermsResult Accepted(AuctionListingTerms terms)
        {
            if (terms == null)
                throw new ArgumentNullException(nameof(terms));
            return new AuctionListingTermsResult(terms, AuctionListingRuleError.None);
        }

        public static AuctionListingTermsResult Rejected(AuctionListingRuleError error)
        {
            if (error == AuctionListingRuleError.None)
                throw new ArgumentOutOfRangeException(nameof(error));
            return new AuctionListingTermsResult(null, error);
        }
    }

    internal interface IAuctionListingPolicy
    {
        AuctionListingTermsResult Evaluate(
            long unitPrice,
            int quantity,
            long nowUnixSeconds);
    }

    internal sealed class DefaultAuctionListingPolicy : IAuctionListingPolicy
    {
        internal const long ListingLifetimeSeconds = 24 * 60 * 60;
        internal const long ListingDepositGold = 10_000;
        internal const int MaximumActiveListings = 5;

        public AuctionListingTermsResult Evaluate(
            long unitPrice,
            int quantity,
            long nowUnixSeconds)
        {
            if (unitPrice <= 0)
                return AuctionListingTermsResult.Rejected(
                    AuctionListingRuleError.InvalidUnitPrice);
            if (quantity <= 0)
                return AuctionListingTermsResult.Rejected(
                    AuctionListingRuleError.InvalidQuantity);
            if (nowUnixSeconds < 0)
                return AuctionListingTermsResult.Rejected(
                    AuctionListingRuleError.InvalidTimestamp);

            try
            {
                var totalPrice = checked(unitPrice * quantity);
                var expiresAt = checked(nowUnixSeconds + ListingLifetimeSeconds);

                return AuctionListingTermsResult.Accepted(new AuctionListingTerms
                {
                    UnitPrice = unitPrice,
                    Quantity = quantity,
                    TotalPrice = totalPrice,
                    DepositAmount = ListingDepositGold,
                    CreatedAtUnixSeconds = nowUnixSeconds,
                    ExpiresAtUnixSeconds = expiresAt,
                });
            }
            catch (OverflowException)
            {
                return AuctionListingTermsResult.Rejected(
                    AuctionListingRuleError.PriceOverflow);
            }
        }
    }
}
