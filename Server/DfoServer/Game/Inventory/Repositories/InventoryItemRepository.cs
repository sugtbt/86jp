using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemRepository
    {
        private const int EquippedListType = (int)InventoryListType.Equipment;

        internal static List<InventoryItem> LoadEquippedItems(SqliteConnection connection, int characterId)
        {
            return LoadCharacterItems(connection, characterId, InventoryListType.Equipment);
        }

        internal static List<InventoryItem> LoadEquippedItemsByAccount(SqliteConnection connection, int accountId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT i.item_uid,
       i.owner_scope,
       i.owner_id,
       i.character_id,
       i.list_type,
       i.slot_index,
       i.item_core,
       i.created_at,
       i.updated_at
FROM character_new_items i
JOIN characters c ON c.character_id = i.character_id
WHERE c.account_id = @accountId
  AND i.owner_scope = 'character'
  AND i.list_type = @listType
ORDER BY i.character_id, i.slot_index;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@listType", EquippedListType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(InvenItemCodec.ReadItem(reader));
                }
            }

            return items;
        }

        internal static List<InventoryItem> LoadCharacterItems(
            SqliteConnection connection,
            int characterId,
            InventoryListType listType)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_scope,
       owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_new_items
WHERE character_id = @characterId
  AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(InvenItemCodec.ReadItem(reader));
                }
            }

            return items;
        }

        internal static List<InventoryItem> LoadCharacterItems(
            SqliteConnection connection,
            int characterId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_scope,
       owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_new_items
WHERE character_id = @characterId
ORDER BY list_type, slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(InvenItemCodec.ReadItem(reader));
                }
            }

            return items;
        }

        internal static List<InventoryItem> LoadItemsByOwner(
            SqliteConnection connection,
            string ownerScope,
            int ownerId,
            InventoryListType listType)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_scope,
       owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_new_items
WHERE owner_scope = @ownerScope
  AND owner_id = @ownerId
  AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@ownerScope", ownerScope);
                command.Parameters.AddWithValue("@ownerId", ownerId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(InvenItemCodec.ReadItem(reader));
                }
            }

            return items;
        }

        internal static List<InventoryItem> LoadAccountCargoItems(
            SqliteConnection connection,
            int accountId)
        {
            var items = new List<InventoryItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       'account' AS owner_scope,
       account_id AS owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM account_cargo_new_items
WHERE account_id = @accountId
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@accountId", accountId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(InvenItemCodec.ReadItem(reader));
                }
            }

            return items;
        }

        internal static InventoryItem LoadCharacterSlot(
            SqliteConnection connection,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_scope,
       owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_new_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? InvenItemCodec.ReadItem(reader) : null;
            }
        }

        internal static InventoryItem LoadAccountCargoSlot(
            SqliteConnection connection,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       'account' AS owner_scope,
       account_id AS owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM account_cargo_new_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? InvenItemCodec.ReadItem(reader) : null;
            }
        }

        internal static long UpsertCharacterSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            ItemCore core)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var itemCore = core.ToBytes();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_new_items
SET character_id = @characterId,
    item_core = @itemCore,
    updated_at = CURRENT_TIMESTAMP
WHERE owner_scope = 'character'
  AND owner_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                if (command.ExecuteNonQuery() > 0)
                    return LoadCharacterSlotUid(connection, transaction, characterId, listType, slotIndex);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_new_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_core, created_at, updated_at
) VALUES (
    'character', @characterId, @characterId, @listType, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                command.ExecuteNonQuery();
            }

            return LoadLastInsertRowId(connection, transaction);
        }

        internal static void DeleteCharacterSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_new_items
WHERE owner_scope = 'character'
  AND owner_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        internal static long UpsertAccountCargoSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            short slotIndex,
            ItemCore core)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var itemCore = core.ToBytes();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE account_cargo_new_items
SET character_id = @characterId,
    list_type = @listType,
    item_core = @itemCore,
    updated_at = CURRENT_TIMESTAMP
WHERE account_id = @accountId
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId <= 0 ? (object)DBNull.Value : characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                if (command.ExecuteNonQuery() > 0)
                    return LoadAccountCargoSlotUid(connection, transaction, accountId, slotIndex);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO account_cargo_new_items (
    account_id, character_id, list_type, slot_index, item_core, created_at, updated_at
) VALUES (
    @accountId, @characterId, @listType, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId <= 0 ? (object)DBNull.Value : characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemCore", itemCore);
                command.ExecuteNonQuery();
            }

            return LoadLastInsertRowId(connection, transaction);
        }

        internal static void DeleteAccountCargoSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM account_cargo_new_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        internal static Dictionary<short, InventoryItem> LoadSlotMapByOwner(
            SqliteConnection connection,
            string ownerScope,
            int ownerId,
            InventoryListType listType,
            IEnumerable<short> slotIndexes)
        {
            var result = new Dictionary<short, InventoryItem>();
            if (slotIndexes == null)
                return result;

            foreach (var slotIndex in slotIndexes)
            {
                var item = LoadSlotByOwner(connection, ownerScope, ownerId, listType, slotIndex);
                if (item != null)
                    result[slotIndex] = item;
            }

            return result;
        }

        internal static InventoryItem LoadSlotByOwner(
            SqliteConnection connection,
            string ownerScope,
            int ownerId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_uid,
       owner_scope,
       owner_id,
       character_id,
       list_type,
       slot_index,
       item_core,
       created_at,
       updated_at
FROM character_new_items
WHERE owner_scope = @ownerScope
  AND owner_id = @ownerId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@ownerScope", ownerScope);
                command.Parameters.AddWithValue("@ownerId", ownerId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? InvenItemCodec.ReadItem(reader) : null;
            }
        }

        internal static Dictionary<int, InventoryItem> LoadEquippedItemMap(SqliteConnection connection, int characterId)
        {
            var result = new Dictionary<int, InventoryItem>();
            foreach (var item in LoadEquippedItems(connection, characterId))
                result[item.SlotIndex] = item;
            return result;
        }

        private static long LoadCharacterSlotUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid
FROM character_new_items
WHERE owner_scope = 'character'
  AND owner_id = @characterId
  AND list_type = @listType
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static long LoadAccountCargoSlotUid(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid
FROM account_cargo_new_items
WHERE account_id = @accountId
  AND slot_index = @slotIndex
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static long LoadLastInsertRowId(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }
}
