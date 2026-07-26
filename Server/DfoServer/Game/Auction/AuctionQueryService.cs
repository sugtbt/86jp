using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionQueryService
    {
        private const int MaximumProtocolListingCount = byte.MaxValue;

        private readonly AuctionRepository _repository;
        private readonly IAuctionTimeProvider _timeProvider;

        public AuctionQueryService(
            AuctionRepository repository,
            IAuctionTimeProvider timeProvider = null)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _timeProvider = timeProvider
                ?? SystemAuctionTimeProvider.Instance;
        }

        public IReadOnlyList<AuctionListingRecord> LoadMyActiveListings(
            InventoryLease lease,
            int limit)
        {
            if (!InventoryContext.IsCurrentLease(lease))
            {
                return Array.Empty<AuctionListingRecord>();
            }

            return _repository.LoadMyActiveListings(
                lease.AccountId,
                lease.CharacterId,
                _timeProvider.UtcNowUnixSeconds(),
                limit);
        }

        public IReadOnlyList<AuctionListingBundle>
            LoadMyActiveListingBundles(InventoryLease lease)
            => LoadMyActiveListingBundles(
                lease,
                MaximumProtocolListingCount);

        public IReadOnlyList<AuctionListingBundle>
            LoadMyActiveListingBundles(
                InventoryLease lease,
                int limit)
        {
            if (!InventoryContext.IsCurrentLease(lease))
            {
                return Array.Empty<AuctionListingBundle>();
            }

            return _repository.LoadMyActiveListingBundles(
                lease.AccountId,
                lease.CharacterId,
                _timeProvider.UtcNowUnixSeconds(),
                limit);
        }
    }
}
