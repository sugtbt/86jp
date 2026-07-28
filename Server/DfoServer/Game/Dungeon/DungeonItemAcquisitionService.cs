using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonItemAcquisitionSource
    {
        QuestAutomaticDrop = 0,
        TutorialReward = 1,
        SpecialMechanismReward = 2,
    }

    internal sealed class DungeonItemGrantRequest
    {
        internal int QuestId { get; set; }
        internal int ItemTemplateId { get; set; }
        internal int Count { get; set; }
        internal DungeonItemAcquisitionSource Source { get; set; }
    }

    internal sealed class DungeonItemGrantEntry
    {
        internal DungeonItemGrantRequest Request { get; set; }
        internal InventoryRewardGrantResult Grant { get; set; }
    }

    internal sealed class DungeonItemGrantBatchResult
    {
        private readonly List<DungeonItemGrantEntry> _entries =
            new List<DungeonItemGrantEntry>();

        internal bool Success { get; set; }
        internal InventoryRewardGrantError Error { get; set; }
        internal IReadOnlyList<DungeonItemGrantEntry> Entries => _entries;
        internal InventoryMutationSet Changes { get; } = new InventoryMutationSet();

        internal void Add(
            DungeonItemGrantRequest request,
            InventoryRewardGrantResult grant)
        {
            _entries.Add(new DungeonItemGrantEntry
            {
                Request = request,
                Grant = grant,
            });
            if (grant != null)
                Changes.AddRange(grant.Changes);
        }
    }

    internal sealed class DungeonItemAcquisitionService
    {
        private readonly DropService _drops;

        internal DungeonItemAcquisitionService(DropService drops)
        {
            _drops = drops ?? throw new ArgumentNullException(nameof(drops));
        }

        internal PickupResult AcquireGroundDrop(
            DungeonRun run,
            ushort sceneSlot,
            EnhancedClientSession session)
        {
            return _drops.TryPickup(run, sceneSlot, session);
        }

        internal bool TryGrantItems(
            InventoryLease lease,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out DungeonItemGrantBatchResult result)
        {
            if (lease == null)
            {
                result = Failed(InventoryRewardGrantError.InvalidInventory);
                return false;
            }

            lock (lease.SyncRoot)
                return TryGrantItems(lease.Inventory, requests, out result);
        }

        internal bool TryGrantItems(
            InventoryService inventory,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out DungeonItemGrantBatchResult result)
        {
            result = new DungeonItemGrantBatchResult();
            if (inventory == null)
            {
                result.Error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }
            if (requests == null)
            {
                result.Error = InventoryRewardGrantError.InvalidRequest;
                return false;
            }
            if (requests.Count == 0)
            {
                result.Success = true;
                return true;
            }

            var grants = new List<InventoryRewardGrantRequest>(requests.Count);
            foreach (var request in requests)
            {
                if (request == null
                    || request.ItemTemplateId <= 0
                    || request.Count <= 0)
                {
                    result.Error = InventoryRewardGrantError.InvalidRequest;
                    return false;
                }

                grants.Add(InventoryRewardGrantRequest.Create(
                    request.ItemTemplateId,
                    request.Count,
                    ItemCreateReason.QuestReward));
            }

            if (!InventoryRewardGrantService.TryGrantBatch(
                    inventory,
                    grants,
                    out var granted))
            {
                result.Error = granted?.Error
                    ?? InventoryRewardGrantError.InsertApplyFailed;
                return false;
            }

            for (var index = 0; index < requests.Count; index++)
            {
                var grant = index < granted.Results.Count
                    ? granted.Results[index]
                    : null;
                if (grant == null || !grant.Success)
                {
                    result.Error = grant?.Error
                        ?? InventoryRewardGrantError.InsertApplyFailed;
                    return false;
                }
                result.Add(requests[index], grant);
            }

            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            return true;
        }

        private static DungeonItemGrantBatchResult Failed(
            InventoryRewardGrantError error)
            => new DungeonItemGrantBatchResult
            {
                Success = false,
                Error = error,
            };
    }
}
