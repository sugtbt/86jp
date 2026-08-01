using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    internal sealed class CardRewardDeliveryResult
    {
        internal static CardRewardDeliveryResult NotCommitted { get; } =
            new CardRewardDeliveryResult(false, Array.Empty<InventorySlotMutation>());

        internal CardRewardDeliveryResult(
            bool committed,
            IReadOnlyList<InventorySlotMutation> changes)
        {
            Committed = committed;
            Changes = changes ?? Array.Empty<InventorySlotMutation>();
        }

        internal bool Committed { get; }
        internal IReadOnlyList<InventorySlotMutation> Changes { get; }
    }

    // Card reward application service. It owns inventory + effect-ledger
    // transitions and has no session, packet, builder, or timer dependency.
    internal sealed class CardRewardService
    {
        private readonly Func<InventoryLease, bool> _persist;

        internal CardRewardService(
            Func<InventoryLease, bool> persist = null)
        {
            _persist = persist ?? InventoryPersistenceService.SaveDirty;
        }

        internal bool CanPayPaidCard(InventoryLease lease, DungeonRun run)
        {
            var cost = CardRewardRules.GetPaidGoldCost(run);
            if (cost <= 0)
                return true;
            if (lease == null)
                return false;
            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(0) >= cost;
        }

        internal CardRewardDeliveryResult Deliver(
            int characterId,
            InventoryLease lease,
            DungeonRun run,
            CardRewardSide side)
        {
            if (characterId <= 0
                || lease == null
                || lease.CharacterId != characterId
                || run == null)
                return CardRewardDeliveryResult.NotCommitted;
            if (!CardRewardRules.TryReserveDelivery(
                    run,
                    side,
                    out var cards,
                    out var reservation))
            {
                return CardRewardDeliveryResult.NotCommitted;
            }

            var changes = new List<InventorySlotMutation>();
            CardInventoryMutationSnapshot snapshot = null;
            InventoryRewardGrantBatchPlan appliedPlan = null;
            var inventoryPersisted = false;
            try
            {
                var carryLimit = InventoryGoldCarryLimitLoader.Load(characterId);
                lock (lease.SyncRoot)
                {
                    if (!TryBuildRewardPlan(
                            lease.Inventory,
                            run,
                            side,
                            cards,
                            carryLimit,
                            out var plan,
                            out var paidCost))
                    {
                        return FailDelivery(run, side, reservation);
                    }
                    appliedPlan = plan;

                    snapshot = CardInventoryMutationSnapshot.Capture(
                        lease.Inventory,
                        plan,
                        includeGold: side == CardRewardSide.Paid);
                    if (side == CardRewardSide.Paid
                        && !TrySpendPaidCardGold(
                            lease.Inventory,
                            paidCost,
                            changes))
                    {
                        snapshot.Restore(lease.Inventory, plan);
                        return FailDelivery(run, side, reservation);
                    }

                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            lease.Inventory,
                            plan,
                            out var grantBatch)
                        || !grantBatch.Success)
                    {
                        snapshot.Restore(lease.Inventory, plan);
                        return FailDelivery(run, side, reservation);
                    }
                    AddChangedSlots(changes, grantBatch.Changes);

                    if (!_persist(lease))
                    {
                        snapshot.Restore(lease.Inventory, plan);
                        return FailDelivery(run, side, reservation);
                    }
                    inventoryPersisted = true;
                }

                if (!run.Effects.TryCommit(reservation))
                {
                    throw new InvalidOperationException(
                        "Card reward effect reservation was lost after persistence.");
                }

                CardRewardRules.ProjectDelivery(run, side);
                CardRewardRules.CompleteSettlementIfFinished(run);
                FileLogger.Log(
                    $"[CardRewardService] {side} rewards committed: " +
                    $"{changes.Count} entries");
                return new CardRewardDeliveryResult(true, changes);
            }
            catch (Exception ex)
            {
                if (!inventoryPersisted && snapshot != null)
                {
                    lock (lease.SyncRoot)
                        snapshot.Restore(lease.Inventory, appliedPlan);
                }
                run.Effects.TryFail(reservation);
                CardRewardRules.ClearSelectedSlot(run, side);
                FileLogger.Log(
                    $"[CardRewardService] {side} delivery failed: {ex.Message}");
                return CardRewardDeliveryResult.NotCommitted;
            }
        }

        private static bool TryBuildRewardPlan(
            InventoryService inventory,
            DungeonRun run,
            CardRewardSide side,
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            int carryLimit,
            out InventoryRewardGrantBatchPlan plan,
            out int paidCost)
        {
            plan = null;
            paidCost = side == CardRewardSide.Paid
                ? CardRewardRules.GetPaidGoldCost(run)
                : 0;
            if (inventory == null || cards == null)
                return false;
            if (paidCost > inventory.CountMainItem(0))
                return false;

            var requests = new List<InventoryRewardGrantRequest>();
            if (side == CardRewardSide.Free)
            {
                if (cards.Count > 0
                    && cards[0].IsGold
                    && cards[0].GoldAmount > 0)
                {
                    var currentGold = inventory.CountMainItem(0);
                    var targetGold = (int)Math.Min(
                        Math.Max(0, carryLimit),
                        (long)currentGold + cards[0].GoldAmount);
                    var grantedGold = Math.Max(0, targetGold - currentGold);
                    if (grantedGold > 0)
                    {
                        requests.Add(InventoryRewardGrantRequest.Create(
                            0,
                            grantedGold,
                            ItemCreateReason.DungeonDrop));
                    }
                }
                AddItemRewardRequest(cards, 1, requests);
            }
            else
            {
                AddItemRewardRequest(cards, 5, requests);
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    requests,
                    out plan))
            {
                FileLogger.Log(
                    $"[CardRewardService] {side} reward planning failed: " +
                    $"{plan?.Error.ToString() ?? "unknown"}");
                return false;
            }

            foreach (var entry in plan.Entries)
            {
                if (entry.Kind != InventoryRewardGrantKind.InventoryItem
                    && entry.Kind != InventoryRewardGrantKind.MainVirtualCount)
                    return false;
                if (entry.ListType != InventoryListType.Main)
                    return false;
            }
            return true;
        }

        private static void AddItemRewardRequest(
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            int index,
            ICollection<InventoryRewardGrantRequest> requests)
        {
            if (cards.Count <= index
                || cards[index].IsGold
                || cards[index].ItemId <= 0
                || cards[index].StackCount <= 0)
                return;

            requests.Add(InventoryRewardGrantRequest.Create(
                cards[index].ItemId,
                cards[index].StackCount,
                ItemCreateReason.DungeonDrop));
        }

        private static bool TrySpendPaidCardGold(
            InventoryService inventory,
            int cost,
            List<InventorySlotMutation> changes)
        {
            cost = Math.Max(0, cost);
            if (cost <= 0)
                return true;
            if (!inventory.TryConsumeMainItem(
                    0,
                    cost,
                    out var consumeResult)
                || !consumeResult.Success)
            {
                return false;
            }
            AddChangedSlots(changes, consumeResult.Changes);
            return true;
        }

        private static CardRewardDeliveryResult FailDelivery(
            DungeonRun run,
            CardRewardSide side,
            DungeonEffectReservation reservation)
        {
            run.Effects.TryFail(reservation);
            CardRewardRules.ClearSelectedSlot(run, side);
            return CardRewardDeliveryResult.NotCommitted;
        }

        private static void AddChangedSlots(
            List<InventorySlotMutation> changes,
            InventoryMutationSet mutation)
        {
            if (mutation == null)
                return;
            foreach (var slot in mutation.Slots)
                AddChangedSlot(changes, slot.ListType, slot.SlotIndex);
        }

        private static void AddChangedSlot(
            List<InventorySlotMutation> changes,
            InventoryListType listType,
            short slotIndex)
        {
            foreach (var existing in changes)
            {
                if (existing.ListType == listType
                    && existing.SlotIndex == slotIndex)
                {
                    return;
                }
            }
            changes.Add(new InventorySlotMutation(listType, slotIndex));
        }

        private sealed class CardInventoryMutationSnapshot
        {
            private readonly Dictionary<(InventoryListType, short), ItemCore>
                _items = new Dictionary<(InventoryListType, short), ItemCore>();
            private readonly Dictionary<short, int> _virtualCounts =
                new Dictionary<short, int>();

            internal static CardInventoryMutationSnapshot Capture(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan,
                bool includeGold)
            {
                var snapshot = new CardInventoryMutationSnapshot();
                if (includeGold)
                {
                    snapshot.CaptureVirtual(
                        inventory,
                        InventoryService.MainVirtualCurrencySlotStart);
                }

                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        snapshot.CaptureVirtual(inventory, entry.SlotIndex);
                        continue;
                    }
                    if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                        continue;

                    var key = (entry.ListType, entry.SlotIndex);
                    if (!snapshot._items.ContainsKey(key))
                    {
                        snapshot._items[key] = inventory.TryGetItem(
                            entry.ListType,
                            entry.SlotIndex,
                            out var item)
                            ? item.Copy()
                            : null;
                    }
                }
                return snapshot;
            }

            internal void Restore(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                if (inventory == null)
                    return;

                if (plan != null)
                {
                    foreach (var entry in plan.Entries)
                    {
                        if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                            && entry.CreateResult != null)
                        {
                            InventoryCreateService.DetachCreatedDetails(
                                inventory,
                                entry.CreateResult);
                        }
                    }
                }

                foreach (var pair in _items)
                {
                    if (pair.Value == null)
                        inventory.RemoveItem(pair.Key.Item1, pair.Key.Item2);
                    else
                        inventory.SetItem(
                            pair.Key.Item1,
                            pair.Key.Item2,
                            pair.Value.Copy());
                }
                foreach (var pair in _virtualCounts)
                    inventory.SetMainVirtualCount(pair.Key, pair.Value);
            }

            private void CaptureVirtual(
                InventoryService inventory,
                short slotIndex)
            {
                if (_virtualCounts.ContainsKey(slotIndex))
                    return;
                _virtualCounts[slotIndex] =
                    inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0;
            }
        }
    }
}
