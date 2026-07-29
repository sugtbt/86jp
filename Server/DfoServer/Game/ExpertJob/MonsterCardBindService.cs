using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class MonsterCardBindResult
    {
        public int ResultItemId { get; set; }
        public int InputRarity { get; set; }
        public int ResultRarity { get; set; }
        public int BindType { get; set; }
        public InventoryRewardGrantResult Grant { get; set; }
    }

    internal sealed class MonsterCardBindService
    {
        private readonly MonsterCardBindCatalog _catalog;

        internal MonsterCardBindService()
            : this(MonsterCardBindCatalog.Load())
        {
        }

        internal MonsterCardBindService(MonsterCardBindCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        internal bool TryBind(
            InventoryService inventory,
            short binderSlot,
            short firstCardSlot,
            short secondCardSlot,
            out MonsterCardBindResult result,
            out string rejection)
        {
            result = null;
            rejection = null;
            if (inventory == null || binderSlot == firstCardSlot || binderSlot == secondCardSlot)
                return Reject("invalid slot selection", out rejection);

            if (!inventory.TryGetItem(InventoryListType.Main, binderSlot, out var binder)
                || !inventory.TryGetItem(InventoryListType.Main, firstCardSlot, out var firstCard)
                || !inventory.TryGetItem(InventoryListType.Main, secondCardSlot, out var secondCard)
                || binder == null || firstCard == null || secondCard == null)
                return Reject("requested slot is empty", out rejection);

            if (firstCardSlot == secondCardSlot && firstCard.Count < 2)
                return Reject($"same card slot has count={firstCard.Count}, need=2", out rejection);

            if (!ItemMetadataResolver.TryLoadStackableFile(binder.ItemId, out var binderFile)
                || binderFile.MonsterCardBind < 0)
                return Reject($"binder metadata invalid item={binder.ItemId}", out rejection);
            if (!TryResolveCard(firstCard.ItemId, out var firstRarity))
                return Reject($"first item is not a supported monster card item={firstCard.ItemId}", out rejection);
            if (!TryResolveCard(secondCard.ItemId, out var secondRarity))
                return Reject($"second item is not a supported monster card item={secondCard.ItemId}", out rejection);
            if (firstRarity != secondRarity)
                return Reject($"card rarity mismatch {firstRarity}!={secondRarity}", out rejection);
            if (!_catalog.TryRollResult(binderFile.MonsterCardBind, firstRarity, out var selected))
                return Reject($"result pool unavailable bind={binderFile.MonsterCardBind} rarity={firstRarity}", out rejection);

            var snapshots = new Dictionary<short, ItemCore>
            {
                [binderSlot] = binder.Copy(),
                [firstCardSlot] = firstCard.Copy(),
                [secondCardSlot] = secondCard.Copy(),
            };
            if (!ConsumeAt(inventory, binderSlot)
                || !ConsumeAt(inventory, firstCardSlot)
                || !ConsumeAt(inventory, secondCardSlot)
                || !InventoryRewardGrantService.TryGrant(
                    inventory,
                    InventoryRewardGrantRequest.Create(selected.ItemId, 1, ItemCreateReason.Unknown),
                    out var grant)
                || grant == null || !grant.Success)
            {
                Restore(inventory, snapshots);
                return Reject("inventory consume or result grant failed", out rejection);
            }

            result = new MonsterCardBindResult
            {
                ResultItemId = selected.ItemId,
                InputRarity = firstRarity,
                ResultRarity = selected.Rarity,
                BindType = binderFile.MonsterCardBind,
                Grant = grant,
            };
            return true;
        }

        private static bool Reject(string reason, out string rejection)
        {
            rejection = reason;
            return false;
        }

        private static bool TryResolveCard(int itemId, out int rarity)
        {
            rarity = -1;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemId, out var card)
                || !string.Equals(card.ItemCategory, "monster card", StringComparison.OrdinalIgnoreCase)
                || card.Rarity < 0 || card.Rarity > 3)
                return false;
            rarity = card.Rarity;
            return true;
        }

        private static bool ConsumeAt(InventoryService inventory, short slot)
        {
            return InventoryDeleteService.TryDecreaseStack(
                    inventory, InventoryListType.Main, slot, 1, out var deleted)
                && deleted != null && deleted.Success;
        }

        private static void Restore(InventoryService inventory, IReadOnlyDictionary<short, ItemCore> snapshots)
        {
            foreach (var pair in snapshots)
                inventory.SetItem(InventoryListType.Main, pair.Key, pair.Value.Copy());
        }
    }
}
