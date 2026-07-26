using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mail;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class AuctionStorageSelfTest
    {
        private const int AccountId = 940100;
        private const int CharacterId = 940101;
        private const int OtherCharacterId = 940102;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== AUCTION_STORAGE selftest ===");

            VerifyFreshDatabaseShape();
            VerifyVersion39Upgrade();
            VerifyDefaultListingPolicy();
            VerifySystemMailResultContract();
            VerifyRepositoryPersistenceAndQueries();

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void VerifySystemMailResultContract()
        {
            Check("default system-mail result fails closed",
                !default(SystemMailEnqueueResult).Success);
            Check("system-mail enqueued and idempotent-existing results are successful",
                new SystemMailEnqueueResult(SystemMailEnqueueStatus.Enqueued).Success
                && new SystemMailEnqueueResult(SystemMailEnqueueStatus.AlreadyExists).Success);
            Check("system-mail rejection remains an explicit failure",
                !new SystemMailEnqueueResult(SystemMailEnqueueStatus.Rejected, "mail unavailable").Success);
        }

        private static void VerifyRepositoryPersistenceAndQueries()
        {
            const long Now = 1_700_000_000;
            var databasePath = NewTempDatabasePath("repository");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedAuctionOwners(connectionString);

                var policy = new DefaultAuctionListingPolicy();
                var terms = policy.Evaluate(101, 3, Now).Terms;
                var expectedItemCore = new ItemCore
                {
                    ItemKind = ItemCore.KindMaterial,
                    ItemId = 123456,
                    Count = 3,
                    TailUnknown0 = 0x1234,
                    TailUnknown3 = 0x56,
                }.ToBytes();
                var draft = new AuctionListingDraft
                {
                    SellerAccountId = AccountId,
                    SellerCharacterId = CharacterId,
                    SourceListType = 0,
                    SourceSlotIndex = 9,
                    ItemId = 123456,
                    ItemKind = ItemCore.KindMaterial,
                    Terms = terms,
                    ItemCore = (byte[])expectedItemCore.Clone(),
                };

                var repository = new AuctionRepository(databasePath, ServerPaths.SchemaFilePath);
                long listingId;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        listingId = repository.CreateListing(connection, transaction, draft);
                        transaction.Commit();
                    }
                }

                draft.ItemCore[0] = 0xFF;
                var stored = repository.LoadListing(listingId);
                Check("repository persists listing and escrow atomically",
                    stored != null
                    && stored.Listing.ListingId == listingId
                    && stored.Escrow.ListingId == listingId);
                Check("repository preserves listing ownership, source, and price fields",
                    stored != null
                    && stored.Listing.SellerAccountId == AccountId
                    && stored.Listing.SellerCharacterId == CharacterId
                    && stored.Listing.SourceListType == 0
                    && stored.Listing.SourceSlotIndex == 9
                    && stored.Listing.ItemId == 123456
                    && stored.Listing.ItemKind == ItemCore.KindMaterial
                    && stored.Listing.Quantity == 3
                    && stored.Listing.UnitPrice == 101
                    && stored.Listing.TotalPrice == 303
                    && stored.Listing.DepositAmount == 10_000);
                Check("escrow stores an independent byte-complete 82-byte ItemCore",
                    stored != null
                    && stored.Escrow.ItemCore.SequenceEqual(expectedItemCore)
                    && stored.Escrow.Quantity == 3);
                Check("escrow stores a stable listing-scoped return source key",
                    stored != null
                    && stored.Escrow.ReturnSourceKey
                        == $"auction:listing:{listingId}:return"
                    && repository.LoadListing(listingId).Escrow.ReturnSourceKey
                        == stored.Escrow.ReturnSourceKey);

                var active = repository.LoadMyActiveListings(
                    AccountId,
                    CharacterId,
                    Now,
                    10);
                Check("my active listings includes a live active listing",
                    active.Count == 1 && active[0].ListingId == listingId);
                var activeBundles = repository.LoadMyActiveListingBundles(
                    AccountId,
                    CharacterId,
                    Now,
                    10);
                Check("my active listing bundles include the escrow item core",
                    activeBundles.Count == 1
                    && activeBundles[0].Listing.ListingId == listingId
                    && activeBundles[0].Escrow.ItemCore.SequenceEqual(
                        expectedItemCore));
                Check("my active listings excludes the exact expiry boundary",
                    repository.LoadMyActiveListings(
                        AccountId,
                        CharacterId,
                        terms.ExpiresAtUnixSeconds,
                        10).Count == 0);

                var due = repository.LoadExpiredCandidates(terms.ExpiresAtUnixSeconds, 10);
                Check("expiry scan includes an active listing at its expiry boundary",
                    due.Count == 1 && due[0].ListingId == listingId);
                Check("expiry scheduler reads the earliest active expiry through the durable index",
                    repository.LoadNextActiveExpiryUnixSeconds()
                        == terms.ExpiresAtUnixSeconds);

                draft.ItemCore = (byte[])expectedItemCore.Clone();
                long rolledBackListingId;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        rolledBackListingId = repository.CreateListing(connection, transaction, draft);
                    }
                }
                Check("rolling back listing creation leaves neither listing nor escrow",
                    repository.LoadListing(rolledBackListingId) == null);

                var rejectedShortCore = false;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var invalidDraft = new AuctionListingDraft
                        {
                            SellerAccountId = AccountId,
                            SellerCharacterId = CharacterId,
                            SourceListType = 0,
                            SourceSlotIndex = 10,
                            ItemId = 654321,
                            ItemKind = 2,
                            Terms = terms,
                            ItemCore = new byte[81],
                        };
                        try
                        {
                            repository.CreateListing(connection, transaction, invalidDraft);
                        }
                        catch (ArgumentException)
                        {
                            rejectedShortCore = true;
                        }
                    }
                }
                Check("repository rejects a non-82-byte escrow blob", rejectedShortCore);
                Check("repository rejects empty or mismatched escrow ItemCore fields",
                    RepositoryRejectsDraft(
                        connectionString,
                        repository,
                        CreateDraft(
                            terms,
                            new ItemCore().ToBytes(),
                            123456,
                            ItemCore.KindMaterial))
                    && RepositoryRejectsDraft(
                        connectionString,
                        repository,
                        CreateDraft(
                            terms,
                            new ItemCore
                            {
                                ItemKind = ItemCore.KindMaterial,
                                ItemId = 999999,
                                Count = 3,
                            }.ToBytes(),
                            123456,
                            ItemCore.KindMaterial))
                    && RepositoryRejectsDraft(
                        connectionString,
                        repository,
                        CreateDraft(
                            terms,
                            new ItemCore
                            {
                                ItemKind = ItemCore.KindConsumable,
                                ItemId = 123456,
                                Count = 3,
                            }.ToBytes(),
                            123456,
                            ItemCore.KindMaterial)));
                Check("repository enforces escrow quantity semantics by item kind",
                    RepositoryRejectsDraft(
                        connectionString,
                        repository,
                        CreateDraft(
                            terms,
                            new ItemCore
                            {
                                ItemKind = ItemCore.KindMaterial,
                                ItemId = 123456,
                                Count = 2,
                            }.ToBytes(),
                            123456,
                            ItemCore.KindMaterial))
                    && RepositoryRejectsDraft(
                        connectionString,
                        repository,
                        CreateDraft(
                            policy.Evaluate(101, 2, Now).Terms,
                            new ItemCore
                            {
                                ItemKind = ItemCore.KindEquipment,
                                ItemId = 123457,
                                InstanceValue = 777,
                            }.ToBytes(),
                            123457,
                            ItemCore.KindEquipment)));

                Exception invalidStatusError = null;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            repository.TryTransitionActive(
                                connection,
                                transaction,
                                listingId,
                                CharacterId,
                                0,
                                (AuctionListingStatus)99,
                                Now + 1);
                        }
                        catch (Exception ex)
                        {
                            invalidStatusError = ex;
                        }
                    }
                }
                Check("CAS transition rejects an undefined status before SQL",
                    invalidStatusError is ArgumentOutOfRangeException);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("CAS transition rejects a different seller",
                            !repository.TryTransitionActive(
                                connection,
                                transaction,
                                listingId,
                                OtherCharacterId,
                                0,
                                AuctionListingStatus.Cancelled,
                                Now + 1));
                        Check("CAS transition accepts matching seller and version",
                            repository.TryTransitionActive(
                                connection,
                                transaction,
                                listingId,
                                CharacterId,
                                0,
                                AuctionListingStatus.Cancelled,
                                Now + 1));
                        transaction.Commit();
                    }
                }

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("CAS transition rejects a repeated stale version",
                            !repository.TryTransitionActive(
                                connection,
                                transaction,
                                listingId,
                                CharacterId,
                                0,
                                AuctionListingStatus.Expired,
                                Now + 2));
                    }
                }

                var transitioned = repository.LoadListing(listingId);
                Check("successful CAS transition increments version and timestamp",
                    transitioned != null
                    && transitioned.Listing.Status == AuctionListingStatus.Cancelled
                    && transitioned.Listing.Version == 1
                    && transitioned.Listing.UpdatedAtUnixSeconds == Now + 1);
                Check("terminal listing is absent from active and expiry queries",
                    repository.LoadMyActiveListings(
                        AccountId,
                        CharacterId,
                        Now,
                        10).Count == 0
                    && repository.LoadExpiredCandidates(terms.ExpiresAtUnixSeconds, 10).Count == 0);
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyDefaultListingPolicy()
        {
            const long Now = 1_700_000_000;
            var policy = new DefaultAuctionListingPolicy();

            Check("default listing-terms result fails closed",
                !default(AuctionListingTermsResult).Success);

            var normal = policy.Evaluate(101, 3, Now);
            Check("listing policy calculates checked fixed-price total",
                normal.Success && normal.Terms.TotalPrice == 303);
            Check("listing policy charges the client-displayed fixed deposit",
                normal.Success && normal.Terms.DepositAmount == 10_000);
            Check("listing policy assigns an exact 24-hour lifetime",
                normal.Success
                && normal.Terms.CreatedAtUnixSeconds == Now
                && normal.Terms.ExpiresAtUnixSeconds == Now + 86_400);

            var lowPrice = policy.Evaluate(20, 1, Now);
            var highPrice = policy.Evaluate(100_000_000, 3, Now);
            Check("listing deposit is independent from the five-percent transaction fee",
                lowPrice.Success
                && lowPrice.Terms.DepositAmount == 10_000
                && highPrice.Success
                && highPrice.Terms.DepositAmount == 10_000);

            Check("listing policy rejects a non-positive unit price",
                policy.Evaluate(0, 1, Now).Error == AuctionListingRuleError.InvalidUnitPrice);
            Check("listing policy rejects a non-positive quantity",
                policy.Evaluate(1, 0, Now).Error == AuctionListingRuleError.InvalidQuantity);
            Check("listing policy rejects a negative timestamp",
                policy.Evaluate(1, 1, -1).Error == AuctionListingRuleError.InvalidTimestamp);
            Check("listing policy reports total-price overflow",
                policy.Evaluate(long.MaxValue, 2, Now).Error == AuctionListingRuleError.PriceOverflow);
        }

        private static AuctionListingDraft CreateDraft(
            AuctionListingTerms terms,
            byte[] itemCore,
            int itemId,
            int itemKind)
            => new AuctionListingDraft
            {
                SellerAccountId = AccountId,
                SellerCharacterId = CharacterId,
                SourceListType = 0,
                SourceSlotIndex = 10,
                ItemId = itemId,
                ItemKind = itemKind,
                Terms = terms,
                ItemCore = itemCore,
            };

        private static bool RepositoryRejectsDraft(
            string connectionString,
            AuctionRepository repository,
            AuctionListingDraft draft)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        repository.CreateListing(
                            connection,
                            transaction,
                            draft);
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        return true;
                    }
                }
            }
        }

        private static void VerifyFreshDatabaseShape()
        {
            var databasePath = NewTempDatabasePath("fresh");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    Check("fresh database advances to schema version 40", ReadUserVersion(connection) == 40);
                    Check("fresh database creates auction_listings", TableExists(connection, "auction_listings"));
                    Check("fresh database creates auction_escrow_items", TableExists(connection, "auction_escrow_items"));
                    Check("fresh database creates seller-active index",
                        IndexExists(connection, "idx_auction_listings_seller_active"));
                    Check("fresh database creates expiry-scan index",
                        IndexExists(connection, "idx_auction_listings_active_expiry"));
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyVersion39Upgrade()
        {
            var databasePath = NewTempDatabasePath("upgrade");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    Execute(connection, @"
DROP TABLE IF EXISTS auction_escrow_items;
DROP TABLE IF EXISTS auction_listings;
PRAGMA user_version = 39;");

                    SqliteMigrations.Apply(connection);

                    Check("v39 upgrade advances to version 40", ReadUserVersion(connection) == 40);
                    Check("v39 upgrade creates auction_listings", TableExists(connection, "auction_listings"));
                    Check("v39 upgrade creates auction_escrow_items", TableExists(connection, "auction_escrow_items"));
                    Check("v39 upgrade creates seller-active index",
                        IndexExists(connection, "idx_auction_listings_seller_active"));
                    Check("v39 upgrade creates expiry-scan index",
                        IndexExists(connection, "idx_auction_listings_active_expiry"));
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static string NewTempDatabasePath(string suffix)
        {
            return Path.Combine(
                Path.GetTempPath(),
                $"auction_storage_{suffix}_{Guid.NewGuid():N}.db");
        }

        private static long ReadUserVersion(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            return SchemaObjectExists(connection, "table", tableName);
        }

        private static bool IndexExists(SqliteConnection connection, string indexName)
        {
            return SchemaObjectExists(connection, "index", indexName);
        }

        private static bool SchemaObjectExists(
            SqliteConnection connection,
            string objectType,
            string objectName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = @type AND name = @name;";
                command.Parameters.AddWithValue("@type", objectType);
                command.Parameters.AddWithValue("@name", objectName);
                return Convert.ToInt64(command.ExecuteScalar()) == 1;
            }
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void SeedAuctionOwners(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'auction-storage-selftest', '');
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'auction-storage-owner');
INSERT INTO characters(character_id, account_id, name)
VALUES(@otherCharacterId, @accountId, 'auction-storage-other');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@otherCharacterId", OtherCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }
    }
}
