using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Auction
{
    internal interface IAuctionExpiredListingSource
    {
        IReadOnlyList<AuctionListingRecord> LoadExpiredCandidates(
            long nowUnixSeconds,
            int limit);

        long? LoadNextActiveExpiryUnixSeconds();
    }

    internal interface IAuctionListingWriter
    {
        long CreateListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AuctionListingDraft draft);
    }

    internal sealed class AuctionRepository :
        IAuctionListingWriter,
        IAuctionExpiredListingSource
    {
        private const int MaximumQueryLimit = 500;
        private const string ListingColumns = @"
l.listing_id,
l.seller_account_id,
l.seller_character_id,
l.source_list_type,
l.source_slot_index,
l.item_id,
l.item_kind,
l.quantity,
l.unit_price,
l.total_price,
l.deposit_amount,
l.status,
l.created_at,
l.expires_at,
l.updated_at,
l.version";

        private readonly string _connectionString;

        public AuctionRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public long CreateListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AuctionListingDraft draft)
        {
            ValidateTransaction(connection, transaction);
            ValidateDraft(draft);

            long listingId;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO auction_listings (
    seller_account_id,
    seller_character_id,
    source_list_type,
    source_slot_index,
    item_id,
    item_kind,
    quantity,
    unit_price,
    total_price,
    deposit_amount,
    status,
    created_at,
    expires_at,
    updated_at,
    version
) VALUES (
    @sellerAccountId,
    @sellerCharacterId,
    @sourceListType,
    @sourceSlotIndex,
    @itemId,
    @itemKind,
    @quantity,
    @unitPrice,
    @totalPrice,
    @depositAmount,
    @activeStatus,
    @createdAt,
    @expiresAt,
    @createdAt,
    0
);
SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@sellerAccountId", draft.SellerAccountId);
                command.Parameters.AddWithValue("@sellerCharacterId", draft.SellerCharacterId);
                command.Parameters.AddWithValue("@sourceListType", draft.SourceListType);
                command.Parameters.AddWithValue("@sourceSlotIndex", draft.SourceSlotIndex);
                command.Parameters.AddWithValue("@itemId", draft.ItemId);
                command.Parameters.AddWithValue("@itemKind", draft.ItemKind);
                command.Parameters.AddWithValue("@quantity", draft.Terms.Quantity);
                command.Parameters.AddWithValue("@unitPrice", draft.Terms.UnitPrice);
                command.Parameters.AddWithValue("@totalPrice", draft.Terms.TotalPrice);
                command.Parameters.AddWithValue("@depositAmount", draft.Terms.DepositAmount);
                command.Parameters.AddWithValue("@activeStatus", (int)AuctionListingStatus.Active);
                command.Parameters.AddWithValue("@createdAt", draft.Terms.CreatedAtUnixSeconds);
                command.Parameters.AddWithValue("@expiresAt", draft.Terms.ExpiresAtUnixSeconds);
                listingId = Convert.ToInt64(command.ExecuteScalar());
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO auction_escrow_items (
    listing_id,
    item_core,
    quantity,
    return_source_key
) VALUES (
    @listingId,
    @itemCore,
    @quantity,
    @returnSourceKey
);";
                command.Parameters.AddWithValue("@listingId", listingId);
                command.Parameters.AddWithValue("@itemCore", draft.ItemCore);
                command.Parameters.AddWithValue("@quantity", draft.Terms.Quantity);
                command.Parameters.AddWithValue(
                    "@returnSourceKey",
                    BuildReturnSourceKey(listingId));
                command.ExecuteNonQuery();
            }

            return listingId;
        }

        public AuctionListingBundle LoadListing(long listingId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadListing(connection, null, listingId);
            }
        }

        public AuctionListingBundle LoadListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction != null && transaction.Connection != connection)
                throw new ArgumentException(
                    "Transaction must belong to the supplied connection.",
                    nameof(transaction));
            if (listingId <= 0)
                return null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
SELECT
{ListingColumns},
e.item_core,
e.quantity,
e.return_source_key
FROM auction_listings l
JOIN auction_escrow_items e ON e.listing_id = l.listing_id
WHERE l.listing_id = @listingId;";
                command.Parameters.AddWithValue("@listingId", listingId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new AuctionListingBundle
                    {
                        Listing = ReadListing(reader),
                        Escrow = new AuctionEscrowItemRecord
                        {
                            ListingId = reader.GetInt64(0),
                            ItemCore = (byte[])reader[16],
                            Quantity = reader.GetInt32(17),
                            ReturnSourceKey = reader.GetString(18),
                        },
                    };
                }
            }
        }

        public IReadOnlyList<AuctionListingRecord> LoadMyActiveListings(
            int sellerAccountId,
            int sellerCharacterId,
            long nowUnixSeconds,
            int limit)
        {
            if (sellerAccountId <= 0 || sellerCharacterId <= 0)
                return Array.Empty<AuctionListingRecord>();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
SELECT {ListingColumns}
FROM auction_listings l
WHERE l.seller_character_id = @sellerCharacterId
  AND l.seller_account_id = @sellerAccountId
  AND l.status = @activeStatus
  AND l.expires_at > @now
ORDER BY l.listing_id DESC
LIMIT @limit;";
                    command.Parameters.AddWithValue("@sellerCharacterId", sellerCharacterId);
                    command.Parameters.AddWithValue("@sellerAccountId", sellerAccountId);
                    command.Parameters.AddWithValue("@activeStatus", (int)AuctionListingStatus.Active);
                    command.Parameters.AddWithValue("@now", nowUnixSeconds);
                    command.Parameters.AddWithValue("@limit", NormalizeLimit(limit));
                    return ReadListings(command);
                }
            }
        }

        public IReadOnlyList<AuctionListingBundle> LoadMyActiveListingBundles(
            int sellerAccountId,
            int sellerCharacterId,
            long nowUnixSeconds,
            int limit)
        {
            if (sellerAccountId <= 0 || sellerCharacterId <= 0)
                return Array.Empty<AuctionListingBundle>();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
SELECT
{ListingColumns},
e.item_core,
e.quantity,
e.return_source_key
FROM auction_listings l
JOIN auction_escrow_items e ON e.listing_id = l.listing_id
WHERE l.seller_character_id = @sellerCharacterId
  AND l.seller_account_id = @sellerAccountId
  AND l.status = @activeStatus
  AND l.expires_at > @now
ORDER BY l.listing_id DESC
LIMIT @limit;";
                    command.Parameters.AddWithValue(
                        "@sellerCharacterId",
                        sellerCharacterId);
                    command.Parameters.AddWithValue(
                        "@sellerAccountId",
                        sellerAccountId);
                    command.Parameters.AddWithValue(
                        "@activeStatus",
                        (int)AuctionListingStatus.Active);
                    command.Parameters.AddWithValue("@now", nowUnixSeconds);
                    command.Parameters.AddWithValue(
                        "@limit",
                        NormalizeLimit(limit));
                    return ReadListingBundles(command);
                }
            }
        }

        public IReadOnlyList<AuctionListingRecord> LoadExpiredCandidates(
            long nowUnixSeconds,
            int limit)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
SELECT {ListingColumns}
FROM auction_listings l
WHERE l.status = @activeStatus
  AND l.expires_at <= @now
ORDER BY l.expires_at, l.listing_id
LIMIT @limit;";
                    command.Parameters.AddWithValue("@activeStatus", (int)AuctionListingStatus.Active);
                    command.Parameters.AddWithValue("@now", nowUnixSeconds);
                    command.Parameters.AddWithValue("@limit", NormalizeLimit(limit));
                    return ReadListings(command);
                }
            }
        }

        public long? LoadNextActiveExpiryUnixSeconds()
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT expires_at
FROM auction_listings
WHERE status = @activeStatus
ORDER BY expires_at, listing_id
LIMIT 1;";
                    command.Parameters.AddWithValue(
                        "@activeStatus",
                        (int)AuctionListingStatus.Active);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? (long?)null
                        : Convert.ToInt64(value);
                }
            }
        }

        public bool TryTransitionActive(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int sellerCharacterId,
            int expectedVersion,
            AuctionListingStatus targetStatus,
            long updatedAtUnixSeconds)
        {
            ValidateTransaction(connection, transaction);
            if (listingId <= 0 || sellerCharacterId <= 0 || expectedVersion < 0)
                return false;
            if (updatedAtUnixSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(updatedAtUnixSeconds));
            if (targetStatus != AuctionListingStatus.Cancelled
                && targetStatus != AuctionListingStatus.Expired
                && targetStatus != AuctionListingStatus.Sold)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetStatus),
                    "An active listing can only transition to a terminal status.");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE auction_listings
SET status = @targetStatus,
    updated_at = @updatedAt,
    version = version + 1
WHERE listing_id = @listingId
  AND seller_character_id = @sellerCharacterId
  AND status = @activeStatus
  AND version = @expectedVersion;";
                command.Parameters.AddWithValue("@targetStatus", (int)targetStatus);
                command.Parameters.AddWithValue("@updatedAt", updatedAtUnixSeconds);
                command.Parameters.AddWithValue("@listingId", listingId);
                command.Parameters.AddWithValue("@sellerCharacterId", sellerCharacterId);
                command.Parameters.AddWithValue("@activeStatus", (int)AuctionListingStatus.Active);
                command.Parameters.AddWithValue("@expectedVersion", expectedVersion);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static IReadOnlyList<AuctionListingRecord> ReadListings(
            SqliteCommand command)
        {
            var listings = new List<AuctionListingRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    listings.Add(ReadListing(reader));
            }
            return listings;
        }

        private static IReadOnlyList<AuctionListingBundle> ReadListingBundles(
            SqliteCommand command)
        {
            var bundles = new List<AuctionListingBundle>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    bundles.Add(new AuctionListingBundle
                    {
                        Listing = ReadListing(reader),
                        Escrow = new AuctionEscrowItemRecord
                        {
                            ListingId = reader.GetInt64(0),
                            ItemCore = (byte[])reader[16],
                            Quantity = reader.GetInt32(17),
                            ReturnSourceKey = reader.GetString(18),
                        },
                    });
                }
            }
            return bundles;
        }

        private static AuctionListingRecord ReadListing(SqliteDataReader reader)
        {
            return new AuctionListingRecord
            {
                ListingId = reader.GetInt64(0),
                SellerAccountId = reader.GetInt32(1),
                SellerCharacterId = reader.GetInt32(2),
                SourceListType = reader.GetInt32(3),
                SourceSlotIndex = reader.GetInt32(4),
                ItemId = reader.GetInt32(5),
                ItemKind = reader.GetInt32(6),
                Quantity = reader.GetInt32(7),
                UnitPrice = reader.GetInt64(8),
                TotalPrice = reader.GetInt64(9),
                DepositAmount = reader.GetInt64(10),
                Status = (AuctionListingStatus)reader.GetInt32(11),
                CreatedAtUnixSeconds = reader.GetInt64(12),
                ExpiresAtUnixSeconds = reader.GetInt64(13),
                UpdatedAtUnixSeconds = reader.GetInt64(14),
                Version = reader.GetInt32(15),
            };
        }

        private static int NormalizeLimit(int limit)
        {
            if (limit <= 0)
                return 1;
            return Math.Min(limit, MaximumQueryLimit);
        }

        private static string BuildReturnSourceKey(long listingId)
        {
            return $"auction:listing:{listingId}:return";
        }

        private static void ValidateTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (transaction.Connection != connection)
                throw new ArgumentException(
                    "Transaction must belong to the supplied connection.",
                    nameof(transaction));
        }

        private static void ValidateDraft(AuctionListingDraft draft)
        {
            if (draft == null)
                throw new ArgumentNullException(nameof(draft));
            if (draft.SellerAccountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(draft.SellerAccountId));
            if (draft.SellerCharacterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(draft.SellerCharacterId));
            if (draft.SourceListType < 0)
                throw new ArgumentOutOfRangeException(nameof(draft.SourceListType));
            if (draft.SourceSlotIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(draft.SourceSlotIndex));
            if (draft.ItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(draft.ItemId));
            if (draft.Terms == null)
                throw new ArgumentNullException(nameof(draft.Terms));
            if (draft.ItemCore == null || draft.ItemCore.Length != ItemCore.Size)
                throw new ArgumentException(
                    $"Escrow ItemCore must contain exactly {ItemCore.Size} bytes.",
                    nameof(draft.ItemCore));
            var escrowItem = ItemCore.FromBytes(draft.ItemCore);
            if (escrowItem.IsEmpty
                || escrowItem.ItemId <= 0
                || escrowItem.ItemId != draft.ItemId
                || escrowItem.ItemKind != draft.ItemKind)
            {
                throw new ArgumentException(
                    "Escrow ItemCore identity must match the listing item.",
                    nameof(draft.ItemCore));
            }
            if (draft.Terms.UnitPrice <= 0
                || draft.Terms.Quantity <= 0
                || draft.Terms.TotalPrice <= 0
                || draft.Terms.DepositAmount < 0
                || draft.Terms.CreatedAtUnixSeconds < 0
                || draft.Terms.ExpiresAtUnixSeconds <= draft.Terms.CreatedAtUnixSeconds)
            {
                throw new ArgumentException(
                    "Listing terms are not valid for persistence.",
                    nameof(draft.Terms));
            }
            if (InventoryStackRuleService.IsStackable(escrowItem))
            {
                if (escrowItem.Count != draft.Terms.Quantity)
                {
                    throw new ArgumentException(
                        "Stackable escrow ItemCore count must match the listing quantity.",
                        nameof(draft.ItemCore));
                }
            }
            else if (draft.Terms.Quantity != 1)
            {
                throw new ArgumentException(
                    "Non-stackable escrow listings must contain exactly one item.",
                    nameof(draft.Terms));
            }

            long expectedTotal;
            try
            {
                expectedTotal = checked(draft.Terms.UnitPrice * draft.Terms.Quantity);
            }
            catch (OverflowException ex)
            {
                throw new ArgumentException(
                    "Listing total price overflows Int64.",
                    nameof(draft.Terms),
                    ex);
            }
            if (expectedTotal != draft.Terms.TotalPrice)
                throw new ArgumentException(
                    "Listing total price does not match unit price and quantity.",
                    nameof(draft.Terms));
        }
    }
}
