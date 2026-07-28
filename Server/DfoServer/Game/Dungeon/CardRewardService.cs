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
            if (characterId <= 0 || lease == null || run == null)
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
            try
            {
                var deliverySucceeded = true;
                var carryLimit = InventoryGoldCarryLimitLoader.Load(characterId);
                lock (lease.SyncRoot)
                {
                    if (side == CardRewardSide.Free)
                    {
                        CollectGoldReward(
                            lease.Inventory,
                            carryLimit,
                            cards,
                            0,
                            changes);
                        CollectItemReward(lease.Inventory, cards, 1, changes);
                    }
                    else
                    {
                        deliverySucceeded = SpendPaidCardGold(
                            lease.Inventory,
                            CardRewardRules.GetPaidGoldCost(run),
                            changes);
                        if (deliverySucceeded)
                            CollectItemReward(lease.Inventory, cards, 5, changes);
                    }
                }

                if (!deliverySucceeded)
                {
                    run.Effects.TryFail(reservation);
                    CardRewardRules.ClearSelectedSlot(run, side);
                    return CardRewardDeliveryResult.NotCommitted;
                }

                // Inventory mutation is irreversible in this process. Commit the
                // ledger before any network projection retries can occur.
                if (!run.Effects.TryCommit(reservation))
                    return CardRewardDeliveryResult.NotCommitted;

                CardRewardRules.ProjectDelivery(run, side);
                CardRewardRules.CompleteSettlementIfFinished(run);
                FileLogger.Log(
                    $"[CardRewardService] {side} rewards committed: " +
                    $"{changes.Count} entries");
                return new CardRewardDeliveryResult(true, changes);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        private static void CollectGoldReward(
            InventoryService inventory,
            int carryLimit,
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            int index,
            List<InventorySlotMutation> changes)
        {
            if (cards.Count <= index
                || !cards[index].IsGold
                || cards[index].GoldAmount <= 0)
            {
                return;
            }
            try
            {
                if (!inventory.TryGrantGold(
                        cards[index].GoldAmount,
                        carryLimit,
                        out _,
                        out _))
                {
                    return;
                }
                AddChangedSlot(
                    changes,
                    InventoryListType.Main,
                    InventoryService.MainVirtualCurrencySlotStart);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[CardRewardService] CollectGoldReward ERROR: {ex.Message}");
            }
        }

        private static bool SpendPaidCardGold(
            InventoryService inventory,
            int cost,
            List<InventorySlotMutation> changes)
        {
            cost = Math.Max(0, cost);
            if (cost <= 0)
                return true;
            try
            {
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
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[CardRewardService] SpendPaidCardGold ERROR: {ex.Message}");
                return false;
            }
        }

        private static void CollectItemReward(
            InventoryService inventory,
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            int index,
            List<InventorySlotMutation> changes)
        {
            if (cards.Count <= index
                || cards[index].IsGold
                || cards[index].ItemId <= 0)
            {
                return;
            }
            var card = cards[index];
            try
            {
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        card.ItemId,
                        ItemCreateReason.DungeonDrop,
                        card.StackCount,
                        out var grant)
                    || !grant.Success)
                {
                    return;
                }
                AddChangedSlots(changes, grant.Changes);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[CardRewardService] CollectItemReward ERROR: {ex.Message}");
            }
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
    }
}
