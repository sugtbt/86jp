using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionListingService
    {
        private readonly string _connectionString;
        private readonly IAuctionListingWriter _listingWriter;
        private readonly IAuctionListingPolicy _listingPolicy;
        private readonly AuctionItemEligibilityPolicy _eligibilityPolicy;
        private readonly IAuctionTimeProvider _timeProvider;

        public event Action<long> ListingCommitted;

        public AuctionListingService(
            string databasePath,
            string schemaFilePath,
            IAuctionListingWriter listingWriter,
            IAuctionListingPolicy listingPolicy = null,
            AuctionItemEligibilityPolicy eligibilityPolicy = null,
            IAuctionTimeProvider timeProvider = null)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                schemaFilePath);
            _listingWriter = listingWriter
                ?? throw new ArgumentNullException(nameof(listingWriter));
            _listingPolicy = listingPolicy ?? new DefaultAuctionListingPolicy();
            _eligibilityPolicy = eligibilityPolicy
                ?? new AuctionItemEligibilityPolicy();
            _timeProvider = timeProvider
                ?? SystemAuctionTimeProvider.Instance;
        }

        public AuctionListResult TryCreateListing(
            InventoryLease lease,
            AuctionListCommand command)
        {
            if (!InventoryContext.TryExecuteCurrentLease(
                    lease,
                    current => TryCreateListingPinned(
                        current,
                        command),
                    out AuctionListResult result))
            {
                return Reject(AuctionApplicationError.InvalidLease);
            }
            return result;
        }

        private AuctionListResult TryCreateListingPinned(
            InventoryLease lease,
            AuctionListCommand command)
        {
            if (lease == null
                || lease.Inventory == null
                || lease.CharacterId <= 0
                || lease.AccountId <= 0
                || lease.Inventory.CharacterId != lease.CharacterId
                || lease.Inventory.AccountId != lease.AccountId)
            {
                return Reject(AuctionApplicationError.InvalidLease);
            }
            if (command == null)
                return Reject(AuctionApplicationError.InvalidTerms);

            var nowUnixSeconds = _timeProvider.UtcNowUnixSeconds();
            var termsResult = _listingPolicy.Evaluate(
                command.UnitPrice,
                command.Quantity,
                nowUnixSeconds);
            if (!termsResult.Success)
                return Reject(AuctionApplicationError.InvalidTerms);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var eligibility = _eligibilityPolicy.Evaluate(
                    inventory,
                    command.SourceListType,
                    command.SourceSlotIndex,
                    command.Quantity,
                    nowUnixSeconds);
                if (!eligibility.Success)
                    return Reject(eligibility.Error);
                if (command.ExpectedItemTemplateId > 0
                    && eligibility.SourceSnapshot.ItemId
                        != command.ExpectedItemTemplateId)
                {
                    return Reject(AuctionApplicationError.ItemMismatch);
                }

                var originalGold = inventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                var sourceMutated = false;
                var goldMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(
                            deferred: false))
                        {
                            if (!CharacterBelongsToAccount(
                                    connection,
                                    transaction,
                                    lease.CharacterId,
                                    lease.AccountId))
                            {
                                return Reject(
                                    AuctionApplicationError.OwnershipMismatch);
                            }
                            if (CountActiveListings(
                                    connection,
                                    transaction,
                                    lease.AccountId,
                                    lease.CharacterId,
                                    nowUnixSeconds)
                                >= DefaultAuctionListingPolicy
                                    .MaximumActiveListings)
                            {
                                return Reject(
                                    AuctionApplicationError
                                        .ActiveListingLimitReached);
                            }

                            var auctionLimit =
                                CharacterGoldLimitRepository
                                    .LoadEffectiveAuctionGoldLimit(
                                        connection,
                                        transaction,
                                        lease.CharacterId);
                            if (termsResult.Terms.TotalPrice > auctionLimit)
                            {
                                return Reject(
                                    AuctionApplicationError
                                        .AuctionGoldLimitExceeded);
                            }
                            if (termsResult.Terms.DepositAmount > originalGold)
                            {
                                return Reject(
                                    AuctionApplicationError
                                        .InsufficientDepositGold);
                            }

                            var currentSource = inventory.GetItem(
                                command.SourceListType,
                                command.SourceSlotIndex);
                            if (!SameSource(
                                    currentSource,
                                    eligibility.SourceSnapshot))
                            {
                                return Reject(
                                    AuctionApplicationError
                                        .InventoryMutationFailed);
                            }

                            if (!InventoryDeleteService.TryDecreaseStack(
                                    inventory,
                                    command.SourceListType,
                                    command.SourceSlotIndex,
                                    command.Quantity,
                                    out var deleteResult)
                                || !deleteResult.Success
                                || deleteResult.DeletedCount
                                    != command.Quantity)
                            {
                                return Reject(
                                    AuctionApplicationError
                                        .InventoryMutationFailed);
                            }
                            sourceMutated = true;

                            if (termsResult.Terms.DepositAmount > int.MaxValue
                                || !inventory.TryConsumeMainItem(
                                    0,
                                    (int)termsResult.Terms.DepositAmount,
                                    out var goldResult)
                                || !goldResult.Success)
                            {
                                RestoreAssets(
                                    inventory,
                                    command,
                                    eligibility.SourceSnapshot,
                                    originalGold);
                                sourceMutated = false;
                                return Reject(
                                    AuctionApplicationError
                                        .InsufficientDepositGold);
                            }
                            goldMutated = true;

                            if (!InventoryPersistenceService
                                .SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                RestoreAssets(
                                    inventory,
                                    command,
                                    eligibility.SourceSnapshot,
                                    originalGold);
                                sourceMutated = false;
                                goldMutated = false;
                                return Reject(
                                    AuctionApplicationError.PersistenceFailed);
                            }

                            var listingId = _listingWriter.CreateListing(
                                connection,
                                transaction,
                                new AuctionListingDraft
                                {
                                    SellerAccountId = lease.AccountId,
                                    SellerCharacterId = lease.CharacterId,
                                    SourceListType =
                                        (int)command.SourceListType,
                                    SourceSlotIndex =
                                        command.SourceSlotIndex,
                                    ItemId =
                                        eligibility.ItemSnapshot.ItemId,
                                    ItemKind =
                                        eligibility.ItemSnapshot.ItemKind,
                                    Terms = termsResult.Terms,
                                    ItemCore =
                                        eligibility.ItemSnapshot.ToBytes(),
                                });
                            if (listingId <= 0)
                            {
                                throw new InvalidOperationException(
                                    "Auction listing writer returned a non-positive listing id.");
                            }

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            sourceMutated = false;
                            goldMutated = false;
                            NotifyListingCommitted(
                                termsResult.Terms.ExpiresAtUnixSeconds);
                            return new AuctionListResult
                            {
                                ListingId = listingId,
                                TotalPrice = termsResult.Terms.TotalPrice,
                                DepositAmount =
                                    termsResult.Terms.DepositAmount,
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (sourceMutated || goldMutated)
                    {
                        RestoreAssets(
                            inventory,
                            command,
                            eligibility.SourceSnapshot,
                            originalGold);
                    }
                    FileLogger.Log(
                        $"[Auction] listing transaction failed cid={lease.CharacterId} slot={command.SourceSlotIndex}: {ex.Message}");
                    return Reject(AuctionApplicationError.PersistenceFailed);
                }
            }
        }

        private static bool CharacterBelongsToAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM characters
WHERE character_id=@characterId
  AND account_id=@accountId
  AND delete_flag=0;";
                command.Parameters.AddWithValue(
                    "@characterId",
                    characterId);
                command.Parameters.AddWithValue("@accountId", accountId);
                return command.ExecuteScalar() != null;
            }
        }

        private static long CountActiveListings(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int sellerAccountId,
            int sellerCharacterId,
            long nowUnixSeconds)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM auction_listings
WHERE seller_account_id=@sellerAccountId
  AND seller_character_id=@sellerCharacterId
  AND status=@activeStatus
  AND expires_at>@now;";
                command.Parameters.AddWithValue(
                    "@sellerAccountId",
                    sellerAccountId);
                command.Parameters.AddWithValue(
                    "@sellerCharacterId",
                    sellerCharacterId);
                command.Parameters.AddWithValue(
                    "@activeStatus",
                    (int)AuctionListingStatus.Active);
                command.Parameters.AddWithValue("@now", nowUnixSeconds);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static bool SameSource(ItemCore current, ItemCore expected)
        {
            if (current == null || expected == null)
                return false;
            var currentBytes = current.ToBytes();
            var expectedBytes = expected.ToBytes();
            return currentBytes.AsSpan().SequenceEqual(expectedBytes);
        }

        private static void RestoreAssets(
            InventoryService inventory,
            AuctionListCommand command,
            ItemCore sourceSnapshot,
            int originalGold)
        {
            if (inventory == null)
                return;
            if (sourceSnapshot != null)
            {
                inventory.SetItem(
                    command.SourceListType,
                    command.SourceSlotIndex,
                    sourceSnapshot.Copy());
            }
            inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                originalGold);
        }

        private static AuctionListResult Reject(
            AuctionApplicationError error)
            => new AuctionListResult { Error = error };

        private void NotifyListingCommitted(long expiresAtUnixSeconds)
        {
            var handlers = ListingCommitted;
            if (handlers == null)
                return;

            foreach (Action<long> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(expiresAtUnixSeconds);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[Auction] listing expiry notification failed expiresAt={expiresAtUnixSeconds}: {ex.Message}");
                }
            }
        }
    }
}
