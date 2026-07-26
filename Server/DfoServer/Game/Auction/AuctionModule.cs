using DfoServer.Game.Mail;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// PR1 composition root. Network code receives application services from
    /// this module instead of constructing storage, assets, mail, and clock
    /// dependencies independently.
    /// </summary>
    internal sealed class AuctionModule
    {
        private AuctionModule(
            AuctionRepository repository,
            AuctionListingService listingService,
            AuctionQueryService queryService,
            AuctionReturnService returnService,
            AuctionExpirationScanner expirationScanner)
        {
            Repository = repository;
            ListingService = listingService;
            QueryService = queryService;
            ReturnService = returnService;
            ExpirationScanner = expirationScanner;
        }

        public AuctionRepository Repository { get; }
        public AuctionListingService ListingService { get; }
        public AuctionQueryService QueryService { get; }
        public AuctionReturnService ReturnService { get; }
        public AuctionExpirationScanner ExpirationScanner { get; }

        public static AuctionModule Create(
            string databasePath,
            string schemaFilePath,
            ISystemMailService mailService,
            ClockService clock,
            IAuctionTimeProvider timeProvider = null)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException(
                    "database path is required",
                    nameof(databasePath));
            }
            if (string.IsNullOrWhiteSpace(schemaFilePath))
            {
                throw new ArgumentException(
                    "schema path is required",
                    nameof(schemaFilePath));
            }
            if (mailService == null)
                throw new ArgumentNullException(nameof(mailService));
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            var effectiveTimeProvider = timeProvider
                ?? SystemAuctionTimeProvider.Instance;
            var repository = new AuctionRepository(
                databasePath,
                schemaFilePath);
            var queryService = new AuctionQueryService(
                repository,
                effectiveTimeProvider);
            var returnService = new AuctionReturnService(
                databasePath,
                schemaFilePath,
                repository,
                mailService,
                effectiveTimeProvider);
            var expirationScanner = new AuctionExpirationScanner(
                repository,
                returnService,
                effectiveTimeProvider);
            var listingService = new AuctionListingService(
                databasePath,
                schemaFilePath,
                repository,
                timeProvider: effectiveTimeProvider);
            listingService.ListingCommitted +=
                expirationScanner.NotifyListingCommitted;
            returnService.ActiveListingCancelled +=
                expirationScanner.NotifyActiveListingRemoved;
            expirationScanner.RegisterClock(clock);

            return new AuctionModule(
                repository,
                listingService,
                queryService,
                returnService,
                expirationScanner);
        }
    }
}
