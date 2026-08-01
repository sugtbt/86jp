using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon.BloodAltar
{
    internal sealed class BloodAltarRewardApplicationService
    {
        private readonly Func<InventoryLease, bool> _persist;

        internal BloodAltarRewardApplicationService(
            Func<InventoryLease, bool> persist = null)
        {
            _persist = persist ?? InventoryPersistenceService.SaveDirty;
        }

        internal BloodAltarRewardCommitResult Commit(
            BloodAltarSettlementPlan settlement,
            InventoryLease lease,
            Guid ownerSessionId)
        {
            if (settlement == null)
                throw new ArgumentNullException(nameof(settlement));
            if (lease == null
                || !InventoryContext.IsCurrentLease(
                    lease,
                    ownerSessionId,
                    lease.CharacterId))
            {
                throw new InvalidOperationException(
                    "Blood altar reward requires the current owned inventory lease.");
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        ownerSessionId,
                        lease.CharacterId))
                {
                    throw new InvalidOperationException(
                        "Blood altar inventory lease changed before reward planning.");
                }

                var inventory = lease.Inventory;
                var requests = new List<InventoryRewardGrantRequest>();
                long requestedGold = 0;
                foreach (var reward in settlement.Rewards)
                {
                    if (reward.IsGold)
                    {
                        requestedGold = Math.Min(
                            int.MaxValue,
                            requestedGold + Math.Max(0, reward.GoldAmount));
                        continue;
                    }
                    if (reward.ItemId <= 0 || reward.StackCount <= 0)
                        continue;
                    requests.Add(InventoryRewardGrantRequest.Create(
                        reward.ItemId,
                        reward.StackCount,
                        ItemCreateReason.DungeonDrop));
                }

                var currentGold = inventory.CountMainItem(0);
                var carryLimit = Math.Max(
                    0,
                    InventoryGoldCarryLimitLoader.Load(lease.CharacterId));
                var grantedGold = (int)Math.Min(
                    Math.Max(0L, requestedGold),
                    Math.Max(0L, (long)carryLimit - currentGold));
                if (grantedGold > 0)
                {
                    requests.Insert(
                        0,
                        InventoryRewardGrantRequest.Create(
                            0,
                            grantedGold,
                            ItemCreateReason.DungeonDrop));
                }

                if (!InventoryRewardGrantService.TryPlanBatch(
                        inventory,
                        requests,
                        out var inventoryPlan))
                {
                    throw new InvalidOperationException(
                        "Blood altar reward planning failed: " +
                        (inventoryPlan?.Error.ToString() ?? "unknown"));
                }
                foreach (var entry in inventoryPlan.Entries)
                {
                    if ((entry.Kind != InventoryRewardGrantKind.InventoryItem
                            && entry.Kind
                                != InventoryRewardGrantKind.MainVirtualCount)
                        || entry.ListType != InventoryListType.Main)
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward resolved to an unsupported " +
                            $"inventory kind: {entry.Kind}/{entry.ListType}.");
                    }
                }

                var snapshotPlan = new DungeonItemGrantBatchPlan
                {
                    Success = true,
                    InventoryPlan = inventoryPlan,
                };
                if (!DungeonItemGrantMutationSnapshot.TryCapture(
                        inventory,
                        snapshotPlan,
                        out var rollback))
                {
                    throw new InvalidOperationException(
                        "Blood altar reward snapshot failed.");
                }

                try
                {
                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            inventory,
                            inventoryPlan,
                            out var grant)
                        || !grant.Success)
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward application failed.");
                    }
                    if (!InventoryContext.IsCurrentLease(
                            lease,
                            ownerSessionId,
                            lease.CharacterId))
                    {
                        throw new InvalidOperationException(
                            "Blood altar inventory lease changed before persistence.");
                    }
                    if (!_persist(lease))
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward persistence failed.");
                    }

                    return new BloodAltarRewardCommitResult(
                        (int)Math.Min(int.MaxValue, requestedGold),
                        grantedGold,
                        inventory.CountMainItem(0),
                        CopyChanges(grant.Changes));
                }
                catch
                {
                    rollback.Restore(inventory, snapshotPlan);
                    throw;
                }
            }
        }

        private static IReadOnlyList<InventorySlotMutation> CopyChanges(
            InventoryMutationSet changes)
        {
            if (changes == null || changes.Slots.Count == 0)
                return Array.Empty<InventorySlotMutation>();
            return new ReadOnlyCollection<InventorySlotMutation>(
                new List<InventorySlotMutation>(changes.Slots));
        }
    }
}
