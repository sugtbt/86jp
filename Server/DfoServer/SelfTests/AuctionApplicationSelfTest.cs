using DfoServer.Game.Auction;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mail;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    public static class AuctionApplicationSelfTest
    {
        private const int AccountId = 941100;
        private const int CharacterId = 941101;
        private const long Now = 1_700_000_000;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== AUCTION_APPLICATION selftest ===");

            VerifyEligibilityPolicy();
            VerifyAuctionGoldLimitLoader();
            VerifyServerTimeProviderControlsAuctionTime();
            VerifyCurrentLeaseAndSellerScope();
            VerifyCurrentLeasePinsReplacementDuringListing();
            VerifySameSessionCharacterSwitchWaitsForPinnedLease();
            VerifyUnrelatedLeaseLifecycleDoesNotBlock();
            VerifyListingTransactionAndMyActive();
            VerifyMyActiveQueryReturnsAllProtocolRecords();
            VerifyActiveListingLimitAndRollback();
            VerifyExpectedItemIdentity();
            VerifyListingRejectionsAndRollback();
            VerifyNonPositiveListingIdRollback();
            VerifyCancellationAndTransactionalMail();
            VerifyExpirationScanner();
            VerifyExpirationScannerReentryGuard();
            VerifyExpirationClockRegistration();
            VerifyExpirationScheduleSignals();
            VerifyExpirationScheduleOrdering();
            VerifyConcurrentExpirationScheduleRefresh();
            VerifyExpirationRetryBackoff();
            VerifyExpirationStartupDrainsAllBatches();
            VerifyClockRegistrationSurvivesStartupFailure();
            VerifyAssumedMailServiceSeam();
            VerifyAuctionModuleComposition();

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void VerifyAssumedMailServiceSeam()
        {
            var databasePath = NewTempDatabasePath("assumed-mail");
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                }.ToString();
                var service = new AssumedSystemMailService();
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var message = new SystemMailMessage
                        {
                            SourceKey = "auction:listing:123:expire",
                            RecipientAccountId = AccountId,
                            RecipientCharacterId = CharacterId,
                            Subject = "return",
                            Body = "return",
                            Gold = 100,
                            Items = Array.Empty<SystemMailItemAttachment>(),
                        };
                        var first = service.Enqueue(
                            connection,
                            transaction,
                            message);
                        var duplicate = service.Enqueue(
                            connection,
                            transaction,
                            message);
                        var invalid = service.Enqueue(
                            connection,
                            transaction,
                            new SystemMailMessage());

                        Check(
                            "assumed mail seam accepts a valid enqueue and keeps source-key idempotency",
                            first.Status == SystemMailEnqueueStatus.Enqueued
                            && duplicate.Status
                                == SystemMailEnqueueStatus.AlreadyExists);
                        Check(
                            "assumed mail seam rejects incomplete messages",
                            invalid.Status
                                == SystemMailEnqueueStatus.Rejected);
                    }
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyAuctionModuleComposition()
        {
            var databasePath = NewTempDatabasePath("module");
            try
            {
                var clock = new ClockService();
                var before = clock.GetDebugSnapshot();
                var module = AuctionModule.Create(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    new AssumedSystemMailService(),
                    clock);
                var after = clock.GetDebugSnapshot();

                Check(
                    "auction module composes all PR1 application services",
                    module.Repository != null
                    && module.ListingService != null
                    && module.QueryService != null
                    && module.ReturnService != null
                    && module.ExpirationScanner != null);
                Check(
                    "auction module leaves no recurring expiry poll or timer when there are no listings",
                    after.MinuteTickCallbacks
                        == before.MinuteTickCallbacks
                    && after.OneShotTimers
                        == before.OneShotTimers);
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyEligibilityPolicy()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            var stack = new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = 941001,
                Count = 10,
            };
            inventory.AttachItem(InventoryListType.Main, 3, stack);
            var policy = new AuctionItemEligibilityPolicy();

            var accepted = policy.Evaluate(
                inventory,
                InventoryListType.Main,
                3,
                4,
                Now);
            Check("eligibility accepts a physical main-bag partial stack",
                accepted.Success
                && accepted.ItemSnapshot != null
                && accepted.ItemSnapshot.Count == 4
                && accepted.ItemSnapshot.ToBytes().Length == ItemCore.Size);

            Check("eligibility rejects virtual and reserved main slots",
                policy.Evaluate(inventory, InventoryListType.Main, 0, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceSlot
                && policy.Evaluate(inventory, InventoryListType.Main, 352, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceSlot);
            Check("eligibility rejects non-main inventory lists",
                policy.Evaluate(inventory, InventoryListType.PersonalCargo, 3, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceList
                && policy.Evaluate(inventory, InventoryListType.Avatar, 3, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceList
                && policy.Evaluate(inventory, InventoryListType.Pet, 3, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceList
                && policy.Evaluate(inventory, InventoryListType.Equipment, 3, 1, Now).Error
                    == AuctionApplicationError.InvalidSourceList);
            Check("eligibility rejects empty source and invalid stack quantities",
                policy.Evaluate(inventory, InventoryListType.Main, 4, 1, Now).Error
                    == AuctionApplicationError.ItemNotFound
                && policy.Evaluate(inventory, InventoryListType.Main, 3, 0, Now).Error
                    == AuctionApplicationError.InvalidQuantity
                && policy.Evaluate(inventory, InventoryListType.Main, 3, 11, Now).Error
                    == AuctionApplicationError.NotEnoughQuantity);

            var equipment = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = 941002,
                InstanceValue = 123456,
            };
            inventory.AttachItem(InventoryListType.Main, 5, equipment);
            Check("eligibility enforces a single quantity for non-stackable items",
                policy.Evaluate(inventory, InventoryListType.Main, 5, 2, Now).Error
                    == AuctionApplicationError.NonStackableQuantity);

            var restricted = stack.Copy();
            restricted.TradeRestriction = 1;
            inventory.AttachItem(InventoryListType.Main, 6, restricted);
            var sortLocked = stack.Copy();
            sortLocked.SortLockFlag = 1;
            inventory.AttachItem(InventoryListType.Main, 7, sortLocked);
            var equipmentLocked = equipment.Copy();
            equipmentLocked.EquipmentLockId = 9;
            inventory.AttachItem(InventoryListType.Main, 8, equipmentLocked);
            inventory.EquipmentLocks.Attach(new EquipmentItemLock
            {
                EquipmentLockId = 9,
                State = 1,
            });
            var expired = stack.Copy();
            expired.ExpireTime = (int)Now;
            inventory.AttachItem(InventoryListType.Main, 9, expired);
            Check("eligibility rejects trade, sort, equipment locks, and expiry boundary",
                policy.Evaluate(inventory, InventoryListType.Main, 6, 1, Now).Error
                    == AuctionApplicationError.TradeRestricted
                && policy.Evaluate(inventory, InventoryListType.Main, 7, 1, Now).Error
                    == AuctionApplicationError.SortLocked
                && policy.Evaluate(inventory, InventoryListType.Main, 8, 1, Now).Error
                    == AuctionApplicationError.EquipmentLocked
                && policy.Evaluate(inventory, InventoryListType.Main, 9, 1, Now).Error
                    == AuctionApplicationError.ItemExpired);
        }

        private static void VerifyAuctionGoldLimitLoader()
        {
            var databasePath = NewTempDatabasePath("gold-limit");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    SeedOwner(connection);
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("auction gold limit defaults to 400m without a saved row",
                            CharacterGoldLimitRepository.LoadEffectiveAuctionGoldLimit(
                                connection,
                                transaction,
                                CharacterId) == GoldLimitDataProvider.BaseAuctionGoldLimit);
                        transaction.Commit();
                    }

                    Execute(connection, @"
INSERT INTO character_gold_limits(character_id, gold_carry_limit, auction_gold_limit)
VALUES(941101, 500000000, 600000000);");
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("auction gold limit uses an upgraded saved value",
                            CharacterGoldLimitRepository.LoadEffectiveAuctionGoldLimit(
                                connection,
                                transaction,
                                CharacterId) == 600_000_000);
                    }
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void VerifyServerTimeProviderControlsAuctionTime()
        {
            using (var scenario = ListingScenario.Create("server-time"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var time = new FixedAuctionTimeProvider(Now);
                var listingService = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: time);
                var result = listingService.TryCreateListing(
                    scenario.Lease,
                    DefaultCommand());
                var stored = repository.LoadListing(result.ListingId);
                Check("listing timestamps come from the injected server clock",
                    result.Success
                    && stored.Listing.CreatedAtUnixSeconds == Now
                    && stored.Listing.ExpiresAtUnixSeconds
                        == Now + DefaultAuctionListingPolicy
                            .ListingLifetimeSeconds);

                var query = new AuctionQueryService(repository, time);
                Check("my active query uses server time rather than caller time",
                    query.LoadMyActiveListings(
                        scenario.Lease,
                        10).Count == 1);
                var activeBundles = query.LoadMyActiveListingBundles(
                    scenario.Lease,
                    10);
                Check("my active query exposes the escrow item for protocol rows",
                    activeBundles.Count == 1
                    && activeBundles[0].Listing.ListingId == result.ListingId
                    && activeBundles[0].Escrow.ItemCore.Length
                        == ItemCore.Size);
                time.UtcNow = stored.Listing.ExpiresAtUnixSeconds;
                Check("my active query applies the server expiry boundary",
                    query.LoadMyActiveListings(
                        scenario.Lease,
                        10).Count == 0);
            }

            using (var scenario = ListingScenario.Create(
                "server-time-cancel"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var listingId = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 10,
                    40);
                var time = new FixedAuctionTimeProvider(Now + 10);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    new FakeSystemMailService(
                        scenario.ConnectionString),
                    time);
                var result = returns.TryCancel(
                    scenario.Lease,
                    listingId,
                    expectedVersion: 0);
                Check("cancel uses server time for the expiry boundary",
                    result.Error
                        == AuctionApplicationError
                            .CancellationWindowExpired);
            }
        }

        private static void VerifyExpectedItemIdentity()
        {
            using (var scenario = ListingScenario.Create("expected-item"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository);
                var command = DefaultCommand();
                command.ExpectedItemTemplateId = 999999;
                var beforeGold = CurrentGold(scenario.Inventory);
                var beforeItem = scenario.Inventory.GetItem(
                    InventoryListType.Main,
                    command.SourceSlotIndex)?.Copy();

                var result = service.TryCreateListing(
                    scenario.Lease,
                    command);
                var afterItem = scenario.Inventory.GetItem(
                    InventoryListType.Main,
                    command.SourceSlotIndex);

                Check("listing rejects a stale client slot identity before mutating assets",
                    result.Error == AuctionApplicationError.ItemMismatch
                    && CountRows(
                        scenario.ConnectionString,
                        "auction_listings") == 0
                    && CurrentGold(scenario.Inventory) == beforeGold
                    && beforeItem != null
                    && afterItem != null
                    && afterItem.ToBytes().SequenceEqual(
                        beforeItem.ToBytes()));
            }
        }

        private static void VerifyCurrentLeaseAndSellerScope()
        {
            using (var scenario = ListingScenario.Create(
                "current-lease"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    41,
                    sellerAccountId: AccountId + 1);
                var cancellable = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    42);

                var oldLease = InventoryContext.Register(
                    Guid.NewGuid(),
                    scenario.Inventory);
                var query = new AuctionQueryService(
                    repository,
                    new FixedAuctionTimeProvider(Now));
                var sellerScopeExcludesMismatchedAccount =
                    query.LoadMyActiveListings(oldLease, 10).Count == 1;

                scenario.Inventory.ClearDirtyState();
                var replacementInventory = scenario.ReloadInventory();
                replacementInventory.ClearDirtyState();
                var replacement = InventoryContext.Register(
                    Guid.NewGuid(),
                    replacementInventory);
                try
                {
                    var listing = new AuctionListingService(
                        scenario.DatabasePath,
                        ServerPaths.SchemaFilePath,
                        repository,
                        timeProvider: new FixedAuctionTimeProvider(Now))
                        .TryCreateListing(oldLease, DefaultCommand());
                    var staleQuery = query.LoadMyActiveListings(
                        oldLease,
                        10);
                    var returns = new AuctionReturnService(
                        scenario.DatabasePath,
                        ServerPaths.SchemaFilePath,
                        repository,
                        new FakeSystemMailService(
                            scenario.ConnectionString),
                        new FixedAuctionTimeProvider(Now + 1));
                    var cancellation = returns.TryCancel(
                        oldLease,
                        cancellable,
                        expectedVersion: 0);

                    Check("query scopes seller by account plus character",
                        sellerScopeExcludesMismatchedAccount);
                    Check("replaced lease cannot list, query, or cancel",
                        listing.Error == AuctionApplicationError.InvalidLease
                        && staleQuery.Count == 0
                        && cancellation.Error
                            == AuctionApplicationError.InvalidLease
                        && scenario.Inventory.GetItem(
                            InventoryListType.Main,
                            3).Count == 10
                        && CurrentGold(scenario.Inventory) == 100_000
                        && repository.LoadListing(cancellable).Listing.Status
                            == AuctionListingStatus.Active
                        && CountRows(
                            scenario.ConnectionString,
                            "auction_listings") == 2);
                }
                finally
                {
                    scenario.Inventory.ClearDirtyState();
                    replacementInventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        replacement.SessionId,
                        replacement.CharacterId);
                }
            }
        }

        private static void VerifyCurrentLeasePinsReplacementDuringListing()
        {
            using (var scenario = ListingScenario.Create(
                "lease-pin"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var writer = new BlockingAuctionListingWriter(repository);
                var service = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    writer,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var replacementInventory = scenario.ReloadInventory();
                replacementInventory.ClearDirtyState();

                var listingTask = Task.Run(
                    () => service.TryCreateListing(
                        scenario.Lease,
                        DefaultCommand()));
                var entered = writer.Entered.Wait(
                    TimeSpan.FromSeconds(5));
                var replacementTask = Task.Run(
                    () => InventoryContext.Register(
                        Guid.NewGuid(),
                        replacementInventory));
                var replacedWhilePinned =
                    replacementTask.Wait(TimeSpan.FromMilliseconds(200));

                const int OtherCharacter = CharacterId + 300;
                var unrelatedInventory = new InventoryService(
                    OtherCharacter,
                    AccountId);
                unrelatedInventory.ClearDirtyState();
                var unrelatedTask = Task.Run(
                    () => InventoryContext.Register(
                        Guid.NewGuid(),
                        unrelatedInventory));
                var unrelatedCompletedQuickly = unrelatedTask.Wait(
                    TimeSpan.FromMilliseconds(300));

                writer.Release.Set();
                var listing = listingTask.GetAwaiter().GetResult();
                var replacement =
                    replacementTask.GetAwaiter().GetResult();
                var unrelated = unrelatedTask.GetAwaiter().GetResult();
                try
                {
                    Check("listing pins current lease against mid-transaction replacement",
                        entered
                        && !replacedWhilePinned
                        && listing.Success
                        && InventoryContext.IsCurrentLease(replacement));
                    Check("waiting same-character replacement does not block unrelated registration",
                        unrelatedCompletedQuickly
                        && InventoryContext.IsCurrentLease(unrelated));
                }
                finally
                {
                    scenario.Inventory.ClearDirtyState();
                    replacementInventory.ClearDirtyState();
                    unrelatedInventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        replacement.SessionId,
                        replacement.CharacterId);
                    InventoryContext.Unregister(
                        unrelated.SessionId,
                        unrelated.CharacterId);
                }
            }
        }

        private static void VerifyUnrelatedLeaseLifecycleDoesNotBlock()
        {
            using (var scenario = ListingScenario.Create(
                "lease-unrelated"))
            {
                const int OtherCharacter = CharacterId + 100;
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var writer = new BlockingAuctionListingWriter(repository);
                var service = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    writer,
                    timeProvider: new FixedAuctionTimeProvider(Now));

                var otherInventory = new InventoryService(
                    OtherCharacter,
                    AccountId);
                var otherLease = InventoryContext.Register(
                    Guid.NewGuid(),
                    otherInventory);
                var query = new AuctionQueryService(
                    repository,
                    new FixedAuctionTimeProvider(Now));
                var replacementInventory = new InventoryService(
                    OtherCharacter,
                    AccountId);

                var listingTask = Task.Run(
                    () => service.TryCreateListing(
                        scenario.Lease,
                        DefaultCommand()));
                var entered = writer.Entered.Wait(
                    TimeSpan.FromSeconds(5));

                InventoryLease observedLease = null;
                var getTask = Task.Run(
                    () => InventoryContext.TryGetLease(
                        OtherCharacter,
                        out observedLease));
                var queryTask = Task.Run(
                    () => query.LoadMyActiveListings(
                        otherLease,
                        10));
                var getCompletedQuickly = getTask.Wait(
                    TimeSpan.FromMilliseconds(300));
                var queryCompletedQuickly = queryTask.Wait(
                    TimeSpan.FromMilliseconds(300));

                otherInventory.ClearDirtyState();
                replacementInventory.ClearDirtyState();
                var replacementTask = Task.Run(
                    () => InventoryContext.Register(
                        Guid.NewGuid(),
                        replacementInventory));
                var registerCompletedQuickly = replacementTask.Wait(
                    TimeSpan.FromMilliseconds(300));

                writer.Release.Set();
                var listing = listingTask.GetAwaiter().GetResult();
                getTask.GetAwaiter().GetResult();
                queryTask.GetAwaiter().GetResult();
                var replacement =
                    replacementTask.GetAwaiter().GetResult();
                try
                {
                    Check("blocked character auction permits unrelated lease lookup",
                        entered
                        && getCompletedQuickly
                        && observedLease != null);
                    Check("blocked character auction permits unrelated query",
                        queryCompletedQuickly);
                    Check("blocked character auction permits unrelated registration",
                        registerCompletedQuickly
                        && InventoryContext.IsCurrentLease(replacement));
                    Check("unrelated lifecycle work does not disturb blocked listing",
                        listing.Success);
                }
                finally
                {
                    scenario.Inventory.ClearDirtyState();
                    otherInventory.ClearDirtyState();
                    replacementInventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        replacement.SessionId,
                        replacement.CharacterId);
                }
            }
        }

        private static void VerifySameSessionCharacterSwitchWaitsForPinnedLease()
        {
            using (var scenario = ListingScenario.Create(
                "lease-character-switch"))
            {
                const int OtherCharacter = CharacterId + 200;
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var writer = new BlockingAuctionListingWriter(repository);
                var service = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    writer,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var replacementInventory = new InventoryService(
                    OtherCharacter,
                    AccountId);
                replacementInventory.ClearDirtyState();

                var listingTask = Task.Run(
                    () => service.TryCreateListing(
                        scenario.Lease,
                        DefaultCommand()));
                var entered = writer.Entered.Wait(
                    TimeSpan.FromSeconds(5));
                var switchTask = Task.Run(
                    () => InventoryContext.Register(
                        scenario.Lease.SessionId,
                        replacementInventory));
                var switchedWhilePinned = switchTask.Wait(
                    TimeSpan.FromMilliseconds(200));

                writer.Release.Set();
                var listing = listingTask.GetAwaiter().GetResult();
                var replacement = switchTask.GetAwaiter().GetResult();
                var staleListing = service.TryCreateListing(
                    scenario.Lease,
                    DefaultCommand());
                try
                {
                    Check("same-session character switch waits for the pinned old lease",
                        entered
                        && !switchedWhilePinned
                        && listing.Success
                        && replacement.CharacterId == OtherCharacter
                        && InventoryContext.IsCurrentLease(replacement));
                    Check("same-session character switch makes the old lease stale",
                        !InventoryContext.IsCurrentLease(scenario.Lease)
                        && staleListing.Error
                            == AuctionApplicationError.InvalidLease);
                }
                finally
                {
                    scenario.Inventory.ClearDirtyState();
                    replacementInventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        replacement.SessionId,
                        replacement.CharacterId);
                }
            }
        }

        private static void VerifyListingTransactionAndMyActive()
        {
            using (var scenario = ListingScenario.Create("listing-success"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                long notifiedExpiry = 0;
                service.ListingCommitted +=
                    expiresAt => notifiedExpiry = expiresAt;
                var original = scenario.Inventory.GetItem(
                    InventoryListType.Main,
                    3).Copy();

                var result = service.TryCreateListing(
                    scenario.Lease,
                    new AuctionListCommand
                    {
                        SourceListType = InventoryListType.Main,
                        SourceSlotIndex = 3,
                        Quantity = 4,
                        UnitPrice = 1_001,
                    });

                Check("listing succeeds and returns fixed-price terms",
                    result.Success
                    && result.ListingId > 0
                    && result.TotalPrice == 4_004
                    && result.DepositAmount == 10_000
                    && notifiedExpiry
                        == repository.LoadListing(result.ListingId)
                            .Listing.ExpiresAtUnixSeconds);
                Check("listing deducts the partial stack and online deposit",
                    scenario.Inventory.GetItem(InventoryListType.Main, 3).Count == 6
                    && CurrentGold(scenario.Inventory) == 90_000);

                var bundle = repository.LoadListing(result.ListingId);
                var escrow = bundle == null
                    ? null
                    : ItemCore.FromBytes(bundle.Escrow.ItemCore);
                Check("listing atomically persists listing and quantity-adjusted escrow",
                    bundle != null
                    && bundle.Listing.SellerAccountId == AccountId
                    && bundle.Listing.SellerCharacterId == CharacterId
                    && bundle.Listing.Quantity == 4
                    && bundle.Listing.DepositAmount == 10_000
                    && escrow != null
                    && escrow.Count == 4
                    && escrow.ItemId == original.ItemId
                    && escrow.TailUnknown0 == original.TailUnknown0);

                var reloaded = scenario.ReloadInventory();
                Check("listing commits inventory item and gold with the auction row",
                    reloaded.GetItem(InventoryListType.Main, 3).Count == 6
                    && CurrentGold(reloaded) == 90_000);

                var query = new AuctionQueryService(
                    repository,
                    new FixedAuctionTimeProvider(Now));
                var mine = query.LoadMyActiveListings(
                    scenario.Lease,
                    10);
                Check("my active application query derives seller from the lease",
                    mine.Count == 1
                    && mine[0].ListingId == result.ListingId
                    && mine[0].SellerCharacterId == CharacterId);
            }
        }

        private static void VerifyListingRejectionsAndRollback()
        {
            using (var insufficient = ListingScenario.Create(
                "insufficient",
                gold: 0))
            {
                var repository = new AuctionRepository(
                    insufficient.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    insufficient.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var result = service.TryCreateListing(
                    insufficient.Lease,
                    DefaultCommand());
                Check("insufficient deposit gold has no inventory or auction side effect",
                    result.Error == AuctionApplicationError.InsufficientDepositGold
                    && insufficient.Inventory.GetItem(InventoryListType.Main, 3).Count == 10
                    && CurrentGold(insufficient.Inventory) == 0
                    && CountRows(insufficient.ConnectionString, "auction_listings") == 0);
            }

            using (var overLimit = ListingScenario.Create(
                "over-limit",
                gold: 100_000_000))
            {
                var repository = new AuctionRepository(
                    overLimit.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    overLimit.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var command = DefaultCommand();
                command.Quantity = 3;
                command.UnitPrice = 200_000_000;
                var result = service.TryCreateListing(overLimit.Lease, command);
                Check("total above effective auction limit is rejected without mutation",
                    result.Error == AuctionApplicationError.AuctionGoldLimitExceeded
                    && overLimit.Inventory.GetItem(InventoryListType.Main, 3).Count == 10
                    && CurrentGold(overLimit.Inventory) == 100_000_000
                    && CountRows(overLimit.ConnectionString, "auction_listings") == 0);
            }

            using (var wrongOwner = ListingScenario.Create(
                "wrong-owner",
                leaseAccountId: AccountId + 1))
            {
                var repository = new AuctionRepository(
                    wrongOwner.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    wrongOwner.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var result = service.TryCreateListing(
                    wrongOwner.Lease,
                    DefaultCommand());
                Check("database character/account mismatch is rejected without mutation",
                    result.Error == AuctionApplicationError.OwnershipMismatch
                    && wrongOwner.Inventory.GetItem(InventoryListType.Main, 3).Count == 10
                    && CurrentGold(wrongOwner.Inventory) == 100_000
                    && CountRows(wrongOwner.ConnectionString, "auction_listings") == 0);
            }

            using (var upgradedLimit = ListingScenario.Create(
                "upgraded-limit",
                gold: 100_000_000))
            {
                using (var connection = new SqliteConnection(
                    upgradedLimit.ConnectionString))
                {
                    connection.Open();
                    Execute(connection, @"
INSERT INTO character_gold_limits(
    character_id,
    gold_carry_limit,
    auction_gold_limit
) VALUES(
    941101,
    600000000,
    600000000
);");
                }
                var repository = new AuctionRepository(
                    upgradedLimit.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    upgradedLimit.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var command = DefaultCommand();
                command.Quantity = 5;
                command.UnitPrice = 100_000_000;
                var result = service.TryCreateListing(
                    upgradedLimit.Lease,
                    command);
                Check("upgraded auction limit permits a total above the 400m default",
                    result.Success
                    && result.TotalPrice == 500_000_000
                    && result.DepositAmount == 10_000);
            }

            using (var repositoryFailure = ListingScenario.Create("repository-failure"))
            {
                var repository = new AuctionRepository(
                    repositoryFailure.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var service = new AuctionListingService(
                    repositoryFailure.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    new ThrowingAuctionListingWriter(repository),
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var result = service.TryCreateListing(
                    repositoryFailure.Lease,
                    DefaultCommand());
                var reloaded = repositoryFailure.ReloadInventory();
                Check("repository exception restores memory and rolls back persisted assets",
                    result.Error == AuctionApplicationError.PersistenceFailed
                    && repositoryFailure.Inventory.GetItem(InventoryListType.Main, 3).Count == 10
                    && CurrentGold(repositoryFailure.Inventory) == 100_000
                    && reloaded.GetItem(InventoryListType.Main, 3).Count == 10
                    && CurrentGold(reloaded) == 100_000
                    && CountRows(repositoryFailure.ConnectionString, "auction_listings") == 0);
            }
        }

        private static void VerifyActiveListingLimitAndRollback()
        {
            using (var full = ListingScenario.Create("active-listing-limit"))
            {
                var repository = new AuctionRepository(
                    full.DatabasePath,
                    ServerPaths.SchemaFilePath);
                for (short slot = 10; slot < 15; slot++)
                {
                    CreateDirectListing(
                        full,
                        repository,
                        Now,
                        Now + 100,
                        slot);
                }

                var service = new AuctionListingService(
                    full.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var result = service.TryCreateListing(
                    full.Lease,
                    DefaultCommand());
                var reloaded = full.ReloadInventory();

                Check(
                    "sixth active listing is rejected before item, gold, or auction mutation",
                    result.Error
                        == AuctionApplicationError.ActiveListingLimitReached
                    && full.Inventory.GetItem(
                        InventoryListType.Main,
                        3).Count == 10
                    && CurrentGold(full.Inventory) == 100_000
                    && reloaded.GetItem(
                        InventoryListType.Main,
                        3).Count == 10
                    && CurrentGold(reloaded) == 100_000
                    && CountRows(
                        full.ConnectionString,
                        "auction_listings") == 5);
            }

            using (var reusable = ListingScenario.Create(
                "inactive-listings-do-not-count"))
            {
                var repository = new AuctionRepository(
                    reusable.DatabasePath,
                    ServerPaths.SchemaFilePath);
                for (short slot = 20; slot < 23; slot++)
                {
                    CreateDirectListing(
                        reusable,
                        repository,
                        Now,
                        Now + 100,
                        slot);
                }
                CreateDirectListing(
                    reusable,
                    repository,
                    Now - 200,
                    Now - 1,
                    23);
                var cancelledId = CreateDirectListing(
                    reusable,
                    repository,
                    Now,
                    Now + 100,
                    24);
                using (var connection = new SqliteConnection(
                    reusable.ConnectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
UPDATE auction_listings
SET status=@cancelledStatus
WHERE listing_id=@listingId;";
                        command.Parameters.AddWithValue(
                            "@cancelledStatus",
                            (int)AuctionListingStatus.Cancelled);
                        command.Parameters.AddWithValue(
                            "@listingId",
                            cancelledId);
                        command.ExecuteNonQuery();
                    }
                }

                var service = new AuctionListingService(
                    reusable.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    timeProvider: new FixedAuctionTimeProvider(Now));
                var result = service.TryCreateListing(
                    reusable.Lease,
                    DefaultCommand());

                Check(
                    "expired and terminal listings do not consume active capacity",
                    result.Success
                    && reusable.Inventory.GetItem(
                        InventoryListType.Main,
                        3).Count == 8
                    && CurrentGold(reusable.Inventory) == 90_000);
            }
        }

        private static void VerifyMyActiveQueryReturnsAllProtocolRecords()
        {
            using (var scenario = ListingScenario.Create(
                "my-active-all-records"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                for (short slot = 30; slot < 36; slot++)
                {
                    CreateDirectListing(
                        scenario,
                        repository,
                        Now,
                        Now + 100,
                        slot);
                }

                var query = new AuctionQueryService(
                    repository,
                    new FixedAuctionTimeProvider(Now));
                var listings = query.LoadMyActiveListingBundles(
                    scenario.Lease);

                Check(
                    "my-active protocol query returns every active row instead of the five-slot listing capacity",
                    listings.Count == 6
                    && listings.Select(
                        bundle => bundle.Listing.ListingId)
                        .Distinct()
                        .Count() == 6);
            }
        }

        private static void VerifyNonPositiveListingIdRollback()
        {
            foreach (var invalidListingId in new long[] { 0, -1 })
            {
                using (var scenario = ListingScenario.Create(
                    $"listing-id-{invalidListingId}"))
                {
                    var service = new AuctionListingService(
                        scenario.DatabasePath,
                        ServerPaths.SchemaFilePath,
                        new NonPositiveAuctionListingWriter(
                            invalidListingId),
                        timeProvider: new FixedAuctionTimeProvider(Now));
                    var result = service.TryCreateListing(
                        scenario.Lease,
                        DefaultCommand());
                    var reloaded = scenario.ReloadInventory();

                    Check($"listing writer id {invalidListingId} rolls back all assets",
                        result.Error == AuctionApplicationError.PersistenceFailed
                        && scenario.Inventory.GetItem(
                            InventoryListType.Main,
                            3).Count == 10
                        && CurrentGold(scenario.Inventory) == 100_000
                        && reloaded.GetItem(
                            InventoryListType.Main,
                            3).Count == 10
                        && CurrentGold(reloaded) == 100_000
                        && CountRows(
                            scenario.ConnectionString,
                            "auction_listings") == 0);
                }
            }
        }

        private static void VerifyCancellationAndTransactionalMail()
        {
            using (var scenario = ListingScenario.Create("cancel"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var listingId = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    10);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var time = new FixedAuctionTimeProvider(Now + 1);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail,
                    time);
                var cancellationNotifications = 0;
                returns.ActiveListingCancelled +=
                    () => cancellationNotifications++;

                var result = returns.TryCancel(
                    scenario.Lease,
                    listingId);
                var returned = mail.Load(listingId);
                var stored = repository.LoadListing(listingId);
                Check("wire cancellation resolves the current version, transitions, and enqueues item plus deposit",
                    result.Success
                    && stored.Listing.Status == AuctionListingStatus.Cancelled
                    && stored.Listing.Version == 1
                    && returned != null
                    && returned.SourceKey
                        == $"auction:listing:{listingId}:return"
                    && returned.AccountId == AccountId
                    && returned.CharacterId == CharacterId
                    && returned.Gold == stored.Listing.DepositAmount
                    && returned.Quantity == stored.Escrow.Quantity
                    && returned.ItemCore.SequenceEqual(stored.Escrow.ItemCore)
                    && cancellationNotifications == 1);

                var repeated = returns.TryCancel(
                    scenario.Lease,
                    listingId,
                    expectedVersion: 0);
                Check("repeated cancel is terminal and does not duplicate mail",
                    repeated.Error == AuctionApplicationError.ListingNotActive
                    && mail.Count == 1
                    && cancellationNotifications == 1);

                var rejectedListing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    11);
                mail.Reject = true;
                var rejected = returns.TryCancel(
                    scenario.Lease,
                    rejectedListing,
                    expectedVersion: 0);
                Check("mail rejection rolls the listing transition back",
                    rejected.Error == AuctionApplicationError.MailRejected
                    && repository.LoadListing(rejectedListing).Listing.Status
                        == AuctionListingStatus.Active
                    && mail.Count == 1);
                mail.Reject = false;

                var insertThenRejectListing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    15);
                mail.InsertThenReject = true;
                var insertThenReject = returns.TryCancel(
                    scenario.Lease,
                    insertThenRejectListing,
                    expectedVersion: 0);
                Check("mail insert followed by Rejected rolls back both modules",
                    insertThenReject.Error
                        == AuctionApplicationError.MailRejected
                    && repository.LoadListing(insertThenRejectListing)
                        .Listing.Status == AuctionListingStatus.Active
                    && mail.Load(insertThenRejectListing) == null);
                mail.InsertThenReject = false;

                var boundaryListing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 10,
                    12);
                time.UtcNow = Now + 10;
                var atBoundary = returns.TryCancel(
                    scenario.Lease,
                    boundaryListing,
                    expectedVersion: 0);
                Check("cancel rejects the exact expiry boundary for scanner ownership",
                    atBoundary.Error
                        == AuctionApplicationError.CancellationWindowExpired
                    && repository.LoadListing(boundaryListing).Listing.Status
                        == AuctionListingStatus.Active);

                var existingListing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    13);
                mail.SeedExisting(
                    repository.LoadListing(existingListing));
                time.UtcNow = Now + 1;
                var idempotent = returns.TryCancel(
                    scenario.Lease,
                    existingListing,
                    expectedVersion: 0);
                Check("mail AlreadyExists is accepted as idempotent completion",
                    idempotent.Success
                    && repository.LoadListing(existingListing).Listing.Status
                        == AuctionListingStatus.Cancelled);

                var guardedListing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    14);
                var otherInventory = new InventoryService(
                    CharacterId + 1,
                    AccountId);
                var otherLease = InventoryContext.Register(
                    Guid.NewGuid(),
                    otherInventory);
                try
                {
                    var wrongOwner = returns.TryCancel(
                        otherLease,
                        guardedListing,
                        expectedVersion: 0);
                    var staleVersion = returns.TryCancel(
                        scenario.Lease,
                        guardedListing,
                        expectedVersion: 9);
                    Check("cancel checks listing owner and expected version before CAS",
                        wrongOwner.Error == AuctionApplicationError.NotOwner
                        && staleVersion.Error
                            == AuctionApplicationError.VersionConflict
                        && repository.LoadListing(guardedListing).Listing.Status
                            == AuctionListingStatus.Active);
                }
                finally
                {
                    otherInventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        otherLease.SessionId,
                        otherLease.CharacterId);
                }
            }
        }

        private static void VerifyExpirationScannerReentryGuard()
        {
            using (var scenario = ListingScenario.Create("expiry-reentry"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var due = CreateDirectListing(
                    scenario,
                    repository,
                    Now - 100,
                    Now,
                    30);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString)
                {
                    BlockNext = true,
                };
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail);
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns);

                var firstTask = Task.Run(() => scanner.Scan(Now, 10));
                var entered = mail.BlockEntered.Wait(
                    TimeSpan.FromSeconds(5));
                var overlapping = scanner.Scan(Now, 10);
                mail.BlockRelease.Set();
                var first = firstTask.GetAwaiter().GetResult();

                Check("expiry scanner rejects overlapping execution while first scan blocks",
                    entered
                    && overlapping.SkippedBecauseRunning
                    && first.CompletedCount == 1
                    && repository.LoadListing(due).Listing.Status
                        == AuctionListingStatus.Expired);
            }
        }

        private static void VerifyExpirationClockRegistration()
        {
            using (var scenario = ListingScenario.Create("expiry-clock"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var due = CreateDirectListing(
                    scenario,
                    repository,
                    now - 100,
                    now - 1,
                    31);
                var future = CreateDirectListing(
                    scenario,
                    repository,
                    now,
                    now + 3600,
                    33);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail);
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns);
                var clock = new ClockService();

                scanner.RegisterClock(clock);
                scanner.RegisterClock(clock);
                var snapshot = clock.GetDebugSnapshot();

                Check("expiry scheduling drains startup work and keeps only one next-expiry timer",
                    repository.LoadListing(due).Listing.Status
                        == AuctionListingStatus.Expired
                    && repository.LoadListing(future).Listing.Status
                        == AuctionListingStatus.Active
                    && mail.Count == 1
                    && snapshot.MinuteTickCallbacks == 0
                    && snapshot.OneShotTimers == 1);

                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(now + 3600)
                        .UtcDateTime);
                var expiredAtDue = SpinWait.SpinUntil(
                    () => repository.LoadListing(future).Listing.Status
                        == AuctionListingStatus.Expired,
                    TimeSpan.FromSeconds(5));
                Check("one-shot callback expires the next listing at its due time",
                    expiredAtDue
                    && mail.Count == 2
                    && clock.GetDebugSnapshot().MinuteTickCallbacks == 0);
            }
        }

        private static void VerifyExpirationScheduleSignals()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-schedule-signals"))
            {
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var time = new FixedAuctionTimeProvider(Now);
                var clock = new ClockService();
                var module = AuctionModule.Create(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    mail,
                    clock,
                    time);
                var empty = clock.GetDebugSnapshot();
                var listing = module.ListingService.TryCreateListing(
                    scenario.Lease,
                    DefaultCommand());
                var scheduled = clock.GetDebugSnapshot();

                Check("module listing commit creates one next-expiry timer without recurring polling",
                    listing.Success
                    && empty.OneShotTimers == 0
                    && scheduled.OneShotTimers == 1
                    && scheduled.MinuteTickCallbacks == 0);

                var cancellation = module.ReturnService.TryCancel(
                    scenario.Lease,
                    listing.ListingId);
                var cleared = SpinWait.SpinUntil(
                    () => clock.GetDebugSnapshot().OneShotTimers == 0,
                    TimeSpan.FromSeconds(5));
                Check("module cancellation clears the stale next-expiry timer",
                    cancellation.Success && cleared);
            }
        }

        private static void VerifyExpirationScheduleOrdering()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-schedule-ordering"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var time = new FixedAuctionTimeProvider(Now);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail,
                    time);
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns,
                    time);
                var clock = new ClockService();
                scanner.RegisterClock(clock);

                var later = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 200,
                    34);
                scanner.NotifyListingCommitted(Now + 200);
                var earlier = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    35);
                scanner.NotifyListingCommitted(Now + 100);
                var latest = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 300,
                    36);
                scanner.NotifyListingCommitted(Now + 300);

                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now + 100)
                        .UtcDateTime);
                var earliestExpired = SpinWait.SpinUntil(
                    () => repository.LoadListing(earlier).Listing.Status
                        == AuctionListingStatus.Expired,
                    TimeSpan.FromSeconds(5));
                Check("earlier commits replace the timer and later commits cannot postpone it",
                    earliestExpired
                    && repository.LoadListing(later).Listing.Status
                        == AuctionListingStatus.Active
                    && repository.LoadListing(latest).Listing.Status
                        == AuctionListingStatus.Active);
            }
        }

        private static void VerifyConcurrentExpirationScheduleRefresh()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-schedule-race"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var source =
                    new BlockingNextExpiryListingSource(repository);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var time = new FixedAuctionTimeProvider(Now);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail,
                    time);
                var scanner = new AuctionExpirationScanner(
                    source,
                    returns,
                    time);
                var clock = new ClockService();
                scanner.RegisterClock(clock);

                source.BlockNextLookup();
                scanner.NotifyActiveListingRemoved();
                var entered = source.BlockEntered.Wait(
                    TimeSpan.FromSeconds(5));
                var listing = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    37);
                scanner.NotifyListingCommitted(Now + 100);
                source.BlockRelease.Set();
                var reconciled = SpinWait.SpinUntil(
                    () => source.LookupCount >= 3,
                    TimeSpan.FromSeconds(5));

                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now + 100)
                        .UtcDateTime);
                var settled = SpinWait.SpinUntil(
                    () => repository.LoadListing(listing).Listing.Status
                            == AuctionListingStatus.Expired
                        && source.LookupCount >= 4,
                    TimeSpan.FromSeconds(5));
                Check("stale empty reconcile cannot cancel a concurrently committed listing timer",
                    entered && reconciled && settled);
            }
        }

        private static void VerifyExpirationRetryBackoff()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-retry-backoff"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var listing = CreateDirectListing(
                    scenario,
                    repository,
                    Now - 100,
                    Now,
                    38);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString)
                {
                    Reject = true,
                };
                var time = new FixedAuctionTimeProvider(Now);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail,
                    time);
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns,
                    time);
                var clock = new ClockService();

                scanner.RegisterClock(clock);
                var firstAttempt = mail.EnqueueCalls == 1;
                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now)
                        .UtcDateTime);
                var noHotLoop = mail.EnqueueCalls == 1;
                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now + 1)
                        .UtcDateTime);
                var secondAttempt = SpinWait.SpinUntil(
                    () => mail.EnqueueCalls == 2,
                    TimeSpan.FromSeconds(5));
                var fiveSecondRetryScheduled = SpinWait.SpinUntil(
                    () => clock.GetDebugSnapshot().OneShotTimers == 1,
                    TimeSpan.FromSeconds(5));
                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now + 5)
                        .UtcDateTime);
                var waitsFiveSeconds = mail.EnqueueCalls == 2;

                mail.Reject = false;
                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(Now + 6)
                        .UtcDateTime);
                var recovered = SpinWait.SpinUntil(
                    () => repository.LoadListing(listing).Listing.Status
                        == AuctionListingStatus.Expired,
                    TimeSpan.FromSeconds(5));
                Check("mail failure retries at one then five seconds without hot polling",
                    firstAttempt
                    && noHotLoop
                    && secondAttempt
                    && fiveSecondRetryScheduled
                    && waitsFiveSeconds
                    && recovered
                    && mail.EnqueueCalls == 3);
            }
        }

        private static void VerifyExpirationStartupDrainsAllBatches()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-startup-backlog"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                for (short slot = 40; slot < 141; slot++)
                {
                    CreateDirectListing(
                        scenario,
                        repository,
                        Now - 100,
                        Now - 1,
                        slot);
                }

                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail,
                    new FixedAuctionTimeProvider(Now));
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns,
                    new FixedAuctionTimeProvider(Now));
                var clock = new ClockService();

                scanner.RegisterClock(clock);
                Check("startup drains an expiry backlog larger than one bounded batch",
                    mail.Count == 101
                    && repository.LoadExpiredCandidates(Now, 100).Count == 0
                    && clock.GetDebugSnapshot().OneShotTimers == 0);
            }
        }

        private static void VerifyClockRegistrationSurvivesStartupFailure()
        {
            using (var scenario = ListingScenario.Create(
                "expiry-clock-startup-failure"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var due = CreateDirectListing(
                    scenario,
                    repository,
                    now - 100,
                    now - 1,
                    32);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail);
                var source = new ThrowOnceExpiredListingSource(repository);
                var scanner = new AuctionExpirationScanner(source, returns);
                var clock = new ClockService();
                Exception registrationError = null;

                try
                {
                    scanner.RegisterClock(clock);
                }
                catch (Exception ex)
                {
                    registrationError = ex;
                }
                var afterRegistration = clock.GetDebugSnapshot();
                clock.CheckOnce(
                    DateTimeOffset
                        .FromUnixTimeSeconds(now + 1)
                        .UtcDateTime);
                var retried = SpinWait.SpinUntil(
                    () => repository.LoadListing(due).Listing.Status
                        == AuctionListingStatus.Expired,
                    TimeSpan.FromSeconds(5));

                Check("startup failure schedules a bounded one-shot retry",
                    registrationError == null
                    && afterRegistration.MinuteTickCallbacks == 0
                    && afterRegistration.OneShotTimers == 1
                    && retried);
            }
        }

        private static void VerifyExpirationScanner()
        {
            using (var scenario = ListingScenario.Create("expiry"))
            {
                var repository = new AuctionRepository(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var due1 = CreateDirectListing(
                    scenario,
                    repository,
                    Now - 100,
                    Now,
                    20);
                var due2 = CreateDirectListing(
                    scenario,
                    repository,
                    Now - 100,
                    Now - 1,
                    21);
                var due3 = CreateDirectListing(
                    scenario,
                    repository,
                    Now - 100,
                    Now - 2,
                    22);
                var future = CreateDirectListing(
                    scenario,
                    repository,
                    Now,
                    Now + 100,
                    23);
                var mail = new FakeSystemMailService(
                    scenario.ConnectionString);
                var returns = new AuctionReturnService(
                    scenario.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    repository,
                    mail);
                var scanner = new AuctionExpirationScanner(
                    repository,
                    returns);

                var first = scanner.Scan(Now, 2);
                Check("expiry scanner processes a bounded due batch only",
                    first.CandidateCount == 2
                    && first.CompletedCount == 2
                    && mail.Count == 2
                    && repository.LoadListing(future).Listing.Status
                        == AuctionListingStatus.Active);

                var second = scanner.Scan(Now, 2);
                var repeated = scanner.Scan(Now, 2);
                Check("repeated expiry scans finish remaining due work once",
                    second.CandidateCount == 1
                    && second.CompletedCount == 1
                    && repeated.CandidateCount == 0
                    && mail.Count == 3
                    && new[] { due1, due2, due3 }.All(
                        id => repository.LoadListing(id).Listing.Status
                            == AuctionListingStatus.Expired));
            }
        }

        private static long CreateDirectListing(
            ListingScenario scenario,
            AuctionRepository repository,
            long createdAt,
            long expiresAt,
            short slot,
            int sellerAccountId = AccountId)
        {
            var terms = new AuctionListingTerms
            {
                UnitPrice = 1_000,
                Quantity = 2,
                TotalPrice = 2_000,
                DepositAmount = 100,
                CreatedAtUnixSeconds = createdAt,
                ExpiresAtUnixSeconds = expiresAt,
            };
            var core = new ItemCore
            {
                ItemKind = ItemCore.KindMaterial,
                ItemId = 942000 + slot,
                Count = 2,
                TailUnknown0 = (ushort)(0x1200 + slot),
            };
            using (var connection = new SqliteConnection(
                scenario.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var id = repository.CreateListing(
                        connection,
                        transaction,
                        new AuctionListingDraft
                        {
                            SellerAccountId = sellerAccountId,
                            SellerCharacterId = CharacterId,
                            SourceListType = (int)InventoryListType.Main,
                            SourceSlotIndex = slot,
                            ItemId = core.ItemId,
                            ItemKind = core.ItemKind,
                            Terms = terms,
                            ItemCore = core.ToBytes(),
                        });
                    transaction.Commit();
                    return id;
                }
            }
        }

        private static AuctionListCommand DefaultCommand()
            => new AuctionListCommand
            {
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = 3,
                Quantity = 2,
                UnitPrice = 1_000,
            };

        private static int CurrentGold(InventoryService inventory)
            => inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static long CountRows(string connectionString, string tableName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM " + tableName + ";";
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static void SeedOwner(SqliteConnection connection)
        {
            Execute(connection, @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(941100, 'auction-application-selftest', '');
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(941101, 'auction-application-selftest-other', '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(941101, 941100, 'auction-application-owner', 60);");
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private sealed class ThrowingAuctionListingWriter
            : IAuctionListingWriter
        {
            private readonly AuctionRepository _inner;

            public ThrowingAuctionListingWriter(AuctionRepository inner)
            {
                _inner = inner;
            }

            public long CreateListing(
                SqliteConnection connection,
                SqliteTransaction transaction,
                AuctionListingDraft draft)
            {
                throw new SqliteException("injected repository failure", 1);
            }
        }

        private sealed class NonPositiveAuctionListingWriter
            : IAuctionListingWriter
        {
            private readonly long _listingId;

            public NonPositiveAuctionListingWriter(long listingId)
            {
                _listingId = listingId;
            }

            public long CreateListing(
                SqliteConnection connection,
                SqliteTransaction transaction,
                AuctionListingDraft draft)
                => _listingId;
        }

        private sealed class BlockingAuctionListingWriter
            : IAuctionListingWriter
        {
            private readonly AuctionRepository _inner;

            public BlockingAuctionListingWriter(AuctionRepository inner)
            {
                _inner = inner;
            }

            public ManualResetEventSlim Entered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim Release { get; } =
                new ManualResetEventSlim(false);

            public long CreateListing(
                SqliteConnection connection,
                SqliteTransaction transaction,
                AuctionListingDraft draft)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException(
                        "blocking listing writer timed out");
                return _inner.CreateListing(
                    connection,
                    transaction,
                    draft);
            }
        }

        private sealed class FixedAuctionTimeProvider
            : IAuctionTimeProvider
        {
            public FixedAuctionTimeProvider(long utcNow)
            {
                UtcNow = utcNow;
            }

            public long UtcNow { get; set; }

            public long UtcNowUnixSeconds()
                => UtcNow;
        }

        private sealed class ThrowOnceExpiredListingSource
            : IAuctionExpiredListingSource
        {
            private readonly AuctionRepository _inner;
            private bool _throw = true;

            public ThrowOnceExpiredListingSource(AuctionRepository inner)
            {
                _inner = inner;
            }

            public IReadOnlyList<AuctionListingRecord> LoadExpiredCandidates(
                long nowUnixSeconds,
                int limit)
            {
                if (_throw)
                {
                    _throw = false;
                    throw new InvalidOperationException(
                        "injected startup scan failure");
                }
                return _inner.LoadExpiredCandidates(
                    nowUnixSeconds,
                    limit);
            }

            public long? LoadNextActiveExpiryUnixSeconds()
                => _inner.LoadNextActiveExpiryUnixSeconds();
        }

        private sealed class BlockingNextExpiryListingSource
            : IAuctionExpiredListingSource
        {
            private readonly AuctionRepository _inner;
            private int _blockNext;
            private int _lookupCount;

            public BlockingNextExpiryListingSource(
                AuctionRepository inner)
            {
                _inner = inner;
            }

            public ManualResetEventSlim BlockEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim BlockRelease { get; } =
                new ManualResetEventSlim(false);
            public int LookupCount => Volatile.Read(ref _lookupCount);

            public void BlockNextLookup()
            {
                BlockEntered.Reset();
                BlockRelease.Reset();
                Interlocked.Exchange(ref _blockNext, 1);
            }

            public IReadOnlyList<AuctionListingRecord>
                LoadExpiredCandidates(
                    long nowUnixSeconds,
                    int limit)
                => _inner.LoadExpiredCandidates(
                    nowUnixSeconds,
                    limit);

            public long? LoadNextActiveExpiryUnixSeconds()
            {
                var result =
                    _inner.LoadNextActiveExpiryUnixSeconds();
                Interlocked.Increment(ref _lookupCount);
                if (Interlocked.Exchange(ref _blockNext, 0) != 0)
                {
                    BlockEntered.Set();
                    if (!BlockRelease.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException(
                            "blocking next-expiry lookup timed out");
                    }
                }
                return result;
            }
        }

        private sealed class FakeMailRow
        {
            public string SourceKey { get; set; }
            public int AccountId { get; set; }
            public int CharacterId { get; set; }
            public long Gold { get; set; }
            public byte[] ItemCore { get; set; }
            public int Quantity { get; set; }
        }

        private sealed class FakeSystemMailService : ISystemMailService
        {
            private readonly string _connectionString;
            private int _enqueueCalls;

            public FakeSystemMailService(string connectionString)
            {
                _connectionString = connectionString;
                using (var connection = new SqliteConnection(
                    _connectionString))
                {
                    connection.Open();
                    Execute(connection, @"
CREATE TABLE IF NOT EXISTS fake_system_mail (
    source_key TEXT PRIMARY KEY,
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL,
    gold INTEGER NOT NULL,
    item_core BLOB NOT NULL,
    quantity INTEGER NOT NULL
);");
                }
            }

            public bool Reject { get; set; }
            public bool InsertThenReject { get; set; }
            public bool BlockNext { get; set; }
            public ManualResetEventSlim BlockEntered { get; } =
                new ManualResetEventSlim(false);
            public ManualResetEventSlim BlockRelease { get; } =
                new ManualResetEventSlim(false);
            public int EnqueueCalls =>
                Volatile.Read(ref _enqueueCalls);

            public int Count
            {
                get
                {
                    using (var connection = new SqliteConnection(
                        _connectionString))
                    {
                        connection.Open();
                        return (int)CountRows(
                            _connectionString,
                            "fake_system_mail");
                    }
                }
            }

            public SystemMailEnqueueResult Enqueue(
                SqliteConnection connection,
                SqliteTransaction transaction,
                SystemMailMessage message)
            {
                Interlocked.Increment(ref _enqueueCalls);
                if (Reject)
                {
                    return new SystemMailEnqueueResult(
                        SystemMailEnqueueStatus.Rejected,
                        "injected rejection");
                }
                if (message == null
                    || message.Items == null
                    || message.Items.Count != 1)
                {
                    return new SystemMailEnqueueResult(
                        SystemMailEnqueueStatus.Rejected,
                        "expected one item");
                }

                if (BlockNext)
                {
                    BlockNext = false;
                    BlockEntered.Set();
                    if (!BlockRelease.Wait(TimeSpan.FromSeconds(5)))
                    {
                        return new SystemMailEnqueueResult(
                            SystemMailEnqueueStatus.Rejected,
                            "blocking fake timed out");
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO fake_system_mail(
    source_key,
    account_id,
    character_id,
    gold,
    item_core,
    quantity
) VALUES(
    @sourceKey,
    @accountId,
    @characterId,
    @gold,
    @itemCore,
    @quantity
);";
                    command.Parameters.AddWithValue(
                        "@sourceKey",
                        message.SourceKey);
                    command.Parameters.AddWithValue(
                        "@accountId",
                        message.RecipientAccountId);
                    command.Parameters.AddWithValue(
                        "@characterId",
                        message.RecipientCharacterId);
                    command.Parameters.AddWithValue("@gold", message.Gold);
                    command.Parameters.AddWithValue(
                        "@itemCore",
                        message.Items[0].ItemCore);
                    command.Parameters.AddWithValue(
                        "@quantity",
                        message.Items[0].Quantity);
                    var inserted = command.ExecuteNonQuery();
                    if (InsertThenReject)
                    {
                        return new SystemMailEnqueueResult(
                            SystemMailEnqueueStatus.Rejected,
                            "injected rejection after insert");
                    }
                    return new SystemMailEnqueueResult(
                        inserted == 1
                            ? SystemMailEnqueueStatus.Enqueued
                            : SystemMailEnqueueStatus.AlreadyExists);
                }
            }

            public void SeedExisting(AuctionListingBundle bundle)
            {
                using (var connection = new SqliteConnection(
                    _connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        Enqueue(
                            connection,
                            transaction,
                            BuildMessage(bundle));
                        transaction.Commit();
                    }
                }
            }

            public FakeMailRow Load(long listingId)
            {
                using (var connection = new SqliteConnection(
                    _connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT source_key, account_id, character_id, gold, item_core, quantity
FROM fake_system_mail
WHERE source_key=@sourceKey;";
                        command.Parameters.AddWithValue(
                            "@sourceKey",
                            $"auction:listing:{listingId}:return");
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                                return null;
                            return new FakeMailRow
                            {
                                SourceKey = reader.GetString(0),
                                AccountId = reader.GetInt32(1),
                                CharacterId = reader.GetInt32(2),
                                Gold = reader.GetInt64(3),
                                ItemCore = (byte[])reader[4],
                                Quantity = reader.GetInt32(5),
                            };
                        }
                    }
                }
            }

            private static SystemMailMessage BuildMessage(
                AuctionListingBundle bundle)
                => new SystemMailMessage
                {
                    SourceKey = bundle.Escrow.ReturnSourceKey,
                    RecipientAccountId =
                        bundle.Listing.SellerAccountId,
                    RecipientCharacterId =
                        bundle.Listing.SellerCharacterId,
                    Gold = bundle.Listing.DepositAmount,
                    Items = new[]
                    {
                        new SystemMailItemAttachment
                        {
                            ItemCore = bundle.Escrow.ItemCore,
                            Quantity = bundle.Escrow.Quantity,
                        },
                    },
                };
        }

        private sealed class ListingScenario : IDisposable
        {
            private ListingScenario(
                string databasePath,
                string connectionString,
                InventoryService inventory,
                InventoryLease lease)
            {
                DatabasePath = databasePath;
                ConnectionString = connectionString;
                Inventory = inventory;
                Lease = lease;
            }

            public string DatabasePath { get; }
            public string ConnectionString { get; }
            public InventoryService Inventory { get; }
            public InventoryLease Lease { get; }

            public static ListingScenario Create(
                string suffix,
                int gold = 100_000,
                int leaseAccountId = AccountId)
            {
                var databasePath = NewTempDatabasePath(suffix);
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    SeedOwner(connection);
                }

                var inventory = new InventoryService(CharacterId, leaseAccountId);
                inventory.SetItem(
                    InventoryListType.Main,
                    3,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindMaterial,
                        ItemId = 941003,
                        Count = 10,
                        TailUnknown0 = 0x1234,
                    });
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    gold);
                var seedLease = new InventoryLease(
                    Guid.NewGuid(),
                    CharacterId,
                    inventory,
                    1);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            seedLease);
                        transaction.Commit();
                    }
                }
                inventory.ClearDirtyState();
                var lease = InventoryContext.Register(
                    Guid.NewGuid(),
                    inventory);
                return new ListingScenario(
                    databasePath,
                    connectionString,
                    inventory,
                    lease);
            }

            public InventoryService ReloadInventory()
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    return InventoryService.LoadFromDb(
                        connection,
                        CharacterId,
                        AccountId);
                }
            }

            public void Dispose()
            {
                Inventory.ClearDirtyState();
                InventoryContext.Unregister(
                    Lease.SessionId,
                    Lease.CharacterId);
                DeleteTempDatabase(DatabasePath);
            }
        }

        private static string NewTempDatabasePath(string suffix)
            => Path.Combine(
                Path.GetTempPath(),
                $"auction_application_{suffix}_{Guid.NewGuid():N}.db");

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
