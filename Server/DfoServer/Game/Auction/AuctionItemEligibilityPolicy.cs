using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction
{
    internal sealed class AuctionItemEligibilityPolicy
    {
        public AuctionItemEligibilityResult Evaluate(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int quantity,
            long nowUnixSeconds)
        {
            if (inventory == null)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.InvalidLease);
            if (listType != InventoryListType.Main)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.InvalidSourceList);
            if (slotIndex < InventoryService.MainSlotStart
                || slotIndex > InventoryService.MainSlotEnd)
            {
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.InvalidSourceSlot);
            }
            if (quantity <= 0)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.InvalidQuantity);

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null || source.IsEmpty || source.ItemId <= 0)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.ItemNotFound);

            var stackable = InventoryStackRuleService.IsStackable(source);
            if (stackable)
            {
                if (source.Count < quantity)
                    return AuctionItemEligibilityResult.Rejected(
                        AuctionApplicationError.NotEnoughQuantity);
            }
            else if (quantity != 1)
            {
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.NonStackableQuantity);
            }

            if (source.TradeRestriction != 0)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.TradeRestricted);
            if (source.SortLockFlag == 1)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.SortLocked);
            if (source.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(
                    source.EquipmentLockId,
                    out var equipmentLock)
                && equipmentLock != null
                && equipmentLock.State != 0)
            {
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.EquipmentLocked);
            }
            if (source.ExpireTime > 0 && source.ExpireTime <= nowUnixSeconds)
                return AuctionItemEligibilityResult.Rejected(
                    AuctionApplicationError.ItemExpired);

            var sourceSnapshot = source.Copy();
            var escrowSnapshot = source.Copy();
            if (stackable)
                escrowSnapshot.Count = quantity;
            return AuctionItemEligibilityResult.Accepted(
                sourceSnapshot,
                escrowSnapshot);
        }
    }
}
