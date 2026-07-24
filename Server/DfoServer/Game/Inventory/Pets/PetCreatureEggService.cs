using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureEggService
    {
        internal static bool TryHatchCreatureEgg(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out CreatureHatchResult result)
        {
            result = null;
            if (inventory == null || listType != InventoryListType.Pet)
                return false;

            if (!inventory.TryGetItem(listType, slotIndex, out var source)
                || source == null
                || source.ItemKind != ItemCore.KindCreature)
                return false;

            if (expectedItemTemplateId > 0 && source.ItemId != expectedItemTemplateId)
                return false;

            if (!CreatureEggResolver.TryResolveHatchedCreatureItemId(source.ItemId, out var hatchedItemTemplateId))
                return false;

            var creatureKey = source.Value > 0
                ? source.Value
                : PetInventoryAccessor.NextCreatureKey(inventory);
            if (creatureKey <= 0)
                return false;

            var updated = source.Copy();
            updated.ItemId = hatchedItemTemplateId;
            updated.Value = creatureKey;
            updated.SealFlag = 0;
            if (!inventory.SetItem(listType, slotIndex, updated))
                return false;

            var detail = inventory.CreatureDetails.GetDetail(creatureKey) ?? CreateDefaultCreatureDetail(creatureKey, hatchedItemTemplateId);
            if (detail.ExpireDate <= 0)
                detail.ExpireDate = CreatureDetail.GetExpireDate(hatchedItemTemplateId);
            inventory.CreatureDetails.Put(detail);

            result = new CreatureHatchResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                EggItemTemplateId = source.ItemId,
                HatchedItemTemplateId = hatchedItemTemplateId,
                PetSerialOrHandle = creatureKey,
            };
            FileLogger.Log($"[PetCreatureEgg] hatch cid={inventory.CharacterId} slot={slotIndex} egg=0x{source.ItemId:X8} pet=0x{hatchedItemTemplateId:X8} key=0x{creatureKey:X8}");
            return true;
        }

        internal static CreatureDetail CreateDefaultCreatureDetail(int creatureKey, int itemTemplateId)
        {
            return new CreatureDetail
            {
                Uid = creatureKey,
                Field04 = 100,
                ModeFlag = 0,
                ProgressValue32 = 0,
                FieldAfterValue32 = 1,
                ExpireDate = CreatureDetail.GetExpireDate(itemTemplateId),
            };
        }
    }
}
