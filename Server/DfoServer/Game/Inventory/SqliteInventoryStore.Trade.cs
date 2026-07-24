using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryBuyItem(int characterId, int accountId, int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _shopStore.TryBuyItem(connection, transaction, characterId, accountId, itemTemplateId, buyCount, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }


        internal const int QuickSlotStart = 3;
        internal const int QuickSlotEnd = 8;
        internal static bool IsQuickSlot(int slot)
            => slot >= QuickSlotStart && slot <= QuickSlotEnd;
        internal const int RentalBagSlotStart = 9;
        internal const int RentalBagSlotEnd = 64;
        internal const int QuestBagSlotStart = 177;
        internal const int QuestBagSlotEnd = 232;

        // 宠物栏(list 7)"宠物"本体分页槽段(category 5): slot 0..139 共 140 格(实测计数)。
        // 其后 宠物装备=140..188(cat6)、宠物耗品=189..237(cat7)。新购宠物从本页首格开始填。
        // Client pet inventory pages share list 7 but use separate slot ranges:
        // category 5 = pets, category 6 = pet equipment, category 7 = pet consumables.
        internal const int PetInventorySlotStart = 0;
        internal const int PetInventorySlotEnd = 139;
        internal const int PetEquipmentSlotStart = 140;
        internal const int PetEquipmentSlotEnd = 188;
        internal const int PetConsumableSlotStart = 189;
        internal const int PetConsumableSlotEnd = 237;
        internal const int AvatarEmblemSlotStart = 289;
        internal const int AvatarEmblemSlotEnd = 344;
        public bool TryPickupRentalWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int itemTemplateId,
            int expireTime,
            out short assignedSlot,
            out int instanceValue)
            => _equipStore.TryPickupRentalWeapon(connection, transaction, characterId, accountId, itemTemplateId, expireTime, out assignedSlot, out instanceValue);

        public bool TryPickupItem(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var result = TryPickupItemCore(connection, transaction,
                        characterId, accountId,
                        itemTemplateId, stackCount, out assignedSlot);
                    if (result) transaction.Commit();
                    return result;
                }
            }
        }

        internal bool TryPickupItemCore(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId,
            int itemTemplateId, int stackCount, out short assignedSlot)
        {
            assignedSlot = -1;

            // 晶块走账号级存储, 不进 character_items
            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, stackCount);
                assignedSlot = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId);
                return true;
            }

            // 复活币固定 slot1; 行被扣光删除后重建仍回 slot1(必须在 metadata Resolve 之前, 证据见 ReviveCoinService)
            if (itemTemplateId == Game.ReviveCoin.ReviveCoinService.ItemId)
            {
                var existingCoin = _db.FindItemByTemplateIdInRange(
                    connection, transaction, characterId, InventoryListType.Main,
                    Game.ReviveCoin.ReviveCoinService.ItemId,
                    Game.ReviveCoin.ReviveCoinService.WalletSlot, Game.ReviveCoin.ReviveCoinService.WalletSlot);
                if (existingCoin != null)
                {
                    _db.UpdateStackCount(connection, transaction, existingCoin.ItemUid, existingCoin.StackCount + stackCount);
                }
                else
                {
                    _db.InsertCharacterItem(
                        connection, transaction, characterId, InventoryListType.Main, Game.ReviveCoin.ReviveCoinService.WalletSlot,
                        Game.ReviveCoin.ReviveCoinService.ItemId, "stackable", stackCount, stackCount, 0, 0, 0, 0, 0, 0, "{}");
                }
                assignedSlot = Game.ReviveCoin.ReviveCoinService.WalletSlot;
                return true;
            }

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            bool isConsumable = metadata.IsPrimaryStackableFamily("waste");

            var placement = ItemIntake.ResolvePlacement(itemTemplateId, metadata);

            if (metadata.IsStackable)
            {
                // 拾取路径特有: [waste] 消耗品优先并入快捷栏(3-8)。
                if (isConsumable && !placement.IsPet)
                {
                    var existingQuick = _db.FindItemByTemplateIdInRange(connection, transaction, characterId, InventoryListType.Main, itemTemplateId, QuickSlotStart, QuickSlotEnd);
                    if (existingQuick != null && (metadata.StackLimit <= 0 || existingQuick.StackCount + stackCount <= metadata.StackLimit))
                    {
                        _db.UpdateStackCount(connection, transaction, existingQuick.ItemUid, existingQuick.StackCount + stackCount);
                        assignedSlot = existingQuick.SlotIndex;
                        return true;
                    }
                }

                if (ItemIntake.TryMergeStack(
                        _db, connection, transaction, characterId, placement,
                        itemTemplateId, stackCount, metadata.StackLimit,
                        out var mergedSlot, out _))
                {
                    assignedSlot = mergedSlot;
                    return true;
                }
            }

            // 拾取路径特有: [waste] 消耗品新行优先落快捷栏。
            if (isConsumable && !placement.IsPet)
            {
                var quickSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Main, QuickSlotStart, QuickSlotEnd);
                if (quickSlot >= 0)
                {
                    _db.InsertCharacterItem(
                        connection, transaction, characterId, InventoryListType.Main, (short)quickSlot,
                        itemTemplateId, metadata.ItemKind, stackCount, stackCount,
                        metadata.Durability, 0, 0, 0, 0, 0, "{}");
                    assignedSlot = (short)quickSlot;
                    return true;
                }
            }

            return ItemIntake.TryInsertNewRow(
                _db, connection, transaction, characterId, placement,
                itemTemplateId, metadata, stackCount,
                out assignedSlot, out _, out _);
        }

        internal bool TryPickupQuestItemCore(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId,
            int stackCount,
            out short assignedSlot)
        {
            assignedSlot = -1;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special" || !metadata.IsStackable)
                return false;

            var existing = _db.FindItemByTemplateIdInRange(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                itemTemplateId,
                QuestBagSlotStart,
                QuestBagSlotEnd);
            if (existing != null
                && (metadata.StackLimit <= 0
                    || existing.StackCount + stackCount <= metadata.StackLimit))
            {
                _db.UpdateStackCount(
                    connection,
                    transaction,
                    existing.ItemUid,
                    existing.StackCount + stackCount);
                assignedSlot = existing.SlotIndex;
                return true;
            }

            var placement = ItemIntake.ResolvePlacement(
                itemTemplateId,
                metadata);
            placement.ListType = InventoryListType.Main;
            placement.SlotStart = QuestBagSlotStart;
            placement.SlotEnd = QuestBagSlotEnd;
            placement.IsCreature = false;
            placement.IsPetArtifact = false;
            placement.IsPetConsumable = false;

            return ItemIntake.TryInsertNewRow(
                _db,
                connection,
                transaction,
                characterId,
                placement,
                itemTemplateId,
                metadata,
                stackCount,
                out assignedSlot,
                out _,
                out _);
        }

        public bool TrySellItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _shopStore.TrySellItem(connection, transaction, characterId, accountId, listType, slotIndex, sellCount, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }
    }
}
