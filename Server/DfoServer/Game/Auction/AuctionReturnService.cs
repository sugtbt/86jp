using DfoServer.Game.Inventory;
using DfoServer.Game.Mail;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionReturnService
    {
        private readonly string _connectionString;
        private readonly AuctionRepository _repository;
        private readonly ISystemMailService _mailService;
        private readonly IAuctionTimeProvider _timeProvider;

        public event Action ActiveListingCancelled;

        public AuctionReturnService(
            string databasePath,
            string schemaFilePath,
            AuctionRepository repository,
            ISystemMailService mailService,
            IAuctionTimeProvider timeProvider = null)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                schemaFilePath);
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _mailService = mailService
                ?? throw new ArgumentNullException(nameof(mailService));
            _timeProvider = timeProvider
                ?? SystemAuctionTimeProvider.Instance;
        }

        public AuctionReturnResult TryCancel(
            InventoryLease lease,
            long listingId)
            => TryCancelCore(lease, listingId, null);

        public AuctionReturnResult TryCancel(
            InventoryLease lease,
            long listingId,
            int expectedVersion)
            => TryCancelCore(lease, listingId, expectedVersion);

        private AuctionReturnResult TryCancelCore(
            InventoryLease lease,
            long listingId,
            int? expectedVersion)
        {
            if (!InventoryContext.TryExecuteCurrentLease(
                    lease,
                    current => TryCancelPinned(
                        current,
                        listingId,
                        expectedVersion),
                    out AuctionReturnResult result))
            {
                return Reject(
                    listingId,
                    AuctionApplicationError.InvalidLease);
            }
            return result;
        }

        private AuctionReturnResult TryCancelPinned(
            InventoryLease lease,
            long listingId,
            int? expectedVersion)
        {
            if (lease == null
                || lease.Inventory == null
                || lease.CharacterId <= 0
                || lease.AccountId <= 0
                || lease.Inventory.CharacterId != lease.CharacterId
                || lease.Inventory.AccountId != lease.AccountId)
            {
                return Reject(
                    listingId,
                    AuctionApplicationError.InvalidLease);
            }
            if (listingId <= 0)
            {
                return Reject(
                    listingId,
                    AuctionApplicationError.ListingNotFound);
            }

            var nowUnixSeconds = _timeProvider.UtcNowUnixSeconds();
            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(
                        deferred: false))
                    {
                        var bundle = _repository.LoadListing(
                            connection,
                            transaction,
                            listingId);
                        if (bundle == null)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.ListingNotFound);
                        }
                        if (bundle.Listing.SellerCharacterId
                                != lease.CharacterId
                            || bundle.Listing.SellerAccountId
                                != lease.AccountId)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.NotOwner);
                        }
                        if (bundle.Listing.Status
                            != AuctionListingStatus.Active)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.ListingNotActive);
                        }
                        if (expectedVersion.HasValue
                            && bundle.Listing.Version
                                != expectedVersion.Value)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.VersionConflict);
                        }
                        if (bundle.Listing.ExpiresAtUnixSeconds
                            <= nowUnixSeconds)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError
                                    .CancellationWindowExpired);
                        }

                        return TransitionAndMail(
                            connection,
                            transaction,
                            bundle,
                            AuctionListingStatus.Cancelled,
                            nowUnixSeconds);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Auction] cancel failed listing={listingId}: {ex.Message}");
                return Reject(
                    listingId,
                    AuctionApplicationError.PersistenceFailed);
            }
        }

        public AuctionReturnResult TryExpire(
            long listingId,
            long nowUnixSeconds)
        {
            if (listingId <= 0)
            {
                return Reject(
                    listingId,
                    AuctionApplicationError.ListingNotFound);
            }

            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(
                        deferred: false))
                    {
                        var bundle = _repository.LoadListing(
                            connection,
                            transaction,
                            listingId);
                        if (bundle == null)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.ListingNotFound);
                        }
                        if (bundle.Listing.Status
                            != AuctionListingStatus.Active)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.ListingNotActive);
                        }
                        if (bundle.Listing.ExpiresAtUnixSeconds
                            > nowUnixSeconds)
                        {
                            return Reject(
                                listingId,
                                AuctionApplicationError.NotExpired);
                        }

                        return TransitionAndMail(
                            connection,
                            transaction,
                            bundle,
                            AuctionListingStatus.Expired,
                            nowUnixSeconds);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Auction] expiry failed listing={listingId}: {ex.Message}");
                return Reject(
                    listingId,
                    AuctionApplicationError.PersistenceFailed);
            }
        }

        private AuctionReturnResult TransitionAndMail(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AuctionListingBundle bundle,
            AuctionListingStatus targetStatus,
            long nowUnixSeconds)
        {
            if (!_repository.TryTransitionActive(
                    connection,
                    transaction,
                    bundle.Listing.ListingId,
                    bundle.Listing.SellerCharacterId,
                    bundle.Listing.Version,
                    targetStatus,
                    nowUnixSeconds))
            {
                return Reject(
                    bundle.Listing.ListingId,
                    AuctionApplicationError.VersionConflict);
            }

            var mailResult = _mailService.Enqueue(
                connection,
                transaction,
                BuildReturnMail(bundle));
            if (!mailResult.Success)
            {
                return Reject(
                    bundle.Listing.ListingId,
                    AuctionApplicationError.MailRejected);
            }

            transaction.Commit();
            if (targetStatus == AuctionListingStatus.Cancelled)
                NotifyActiveListingCancelled();
            return new AuctionReturnResult
            {
                ListingId = bundle.Listing.ListingId,
                Status = targetStatus,
            };
        }

        private static SystemMailMessage BuildReturnMail(
            AuctionListingBundle bundle)
            => new SystemMailMessage
            {
                SourceKey = bundle.Escrow.ReturnSourceKey,
                RecipientAccountId =
                    bundle.Listing.SellerAccountId,
                RecipientCharacterId =
                    bundle.Listing.SellerCharacterId,
                Subject = "Auction listing return",
                Body = "Your auction item and refundable deposit were returned.",
                Gold = bundle.Listing.DepositAmount,
                Items = new[]
                {
                    new SystemMailItemAttachment
                    {
                        ItemCore = (byte[])bundle.Escrow.ItemCore.Clone(),
                        Quantity = bundle.Escrow.Quantity,
                    },
                },
            };

        private void NotifyActiveListingCancelled()
        {
            var handlers = ActiveListingCancelled;
            if (handlers == null)
                return;

            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[Auction] cancellation expiry notification failed: {ex.Message}");
                }
            }
        }

        private static AuctionReturnResult Reject(
            long listingId,
            AuctionApplicationError error)
            => new AuctionReturnResult
            {
                ListingId = listingId,
                Error = error,
            };
    }
}
