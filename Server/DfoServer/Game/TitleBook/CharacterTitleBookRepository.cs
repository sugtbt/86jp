using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.TitleBook
{
    public sealed class CharacterTitleBookRepository
    {
        private readonly string _connectionString;

        public CharacterTitleBookRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public List<TitleBookCategorySnapshot> LoadSnapshots(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadModel(connection, characterId).BuildSnapshots();
            }
        }

        public TitleBookCategorySnapshot LoadSnapshot(int characterId, int category)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadSnapshot(connection, null, characterId, category);
            }
        }

        public TitleBookCategorySnapshot LoadSnapshot(SqliteConnection connection, SqliteTransaction transaction, int characterId, int category)
        {
            return LoadModel(connection, transaction, characterId).BuildSnapshot(category);
        }

        internal static TitleBookModel LoadModel(SqliteConnection connection, int characterId)
        {
            return LoadModel(connection, null, characterId);
        }

        internal static TitleBookModel LoadModel(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var model = new TitleBookModel();
            LoadNewItems(connection, transaction, characterId, model);
            return model;
        }

        internal static void SaveSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int category,
            int slotIndex,
            ItemCore core)
        {
            if (category < 0 || category >= TitleBookStaticDataProvider.CategoryCapacities.Count)
                return;
            if (slotIndex < 0 || slotIndex >= TitleBookStaticDataProvider.CategoryCapacities[category])
                return;

            if (core == null || core.IsEmpty)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
DELETE FROM character_new_titlebook
WHERE character_id = @cid AND category = @category AND slot_index = @slot;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@category", category);
                    command.Parameters.AddWithValue("@slot", slotIndex);
                    command.ExecuteNonQuery();
                }
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_new_titlebook (
    character_id, category, slot_index, item_core, updated_at
) VALUES (
    @cid, @category, @slot, @itemCore, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id, category, slot_index)
DO UPDATE SET
    item_core = excluded.item_core,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@category", category);
                command.Parameters.AddWithValue("@slot", slotIndex);
                command.Parameters.AddWithValue("@itemCore", core.ToBytes());
                command.ExecuteNonQuery();
            }
        }

        internal static void MigrateLegacyToNewTable(SqliteConnection connection)
        {
            EnsureNewTable(connection);
            using (var transaction = connection.BeginTransaction())
            {
                MigrateLegacyTitleBookBlobs(connection, transaction);
                MigrateLegacyAchievementChunks(connection, transaction);
                transaction.Commit();
            }
        }

        private static int LoadNewItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            TitleBookModel model)
        {
            if (!TableExists(connection, "character_new_titlebook"))
                return 0;

            var count = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT category, slot_index, item_core
FROM character_new_titlebook
WHERE character_id = @cid
ORDER BY category, slot_index;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = reader.IsDBNull(2) ? null : (byte[])reader[2];
                        if (data == null || data.Length < ItemCore.Size)
                            continue;

                        model.AttachItem(reader.GetInt32(0), reader.GetInt32(1), ItemCore.FromBytes(data));
                        count++;
                    }
                }
            }

            return count;
        }

        private static void MigrateLegacyTitleBookBlobs(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (!TableExists(connection, "character_titlebook"))
                return;

            var pending = new List<Tuple<int, int, int, ItemCore>>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT character_id, general, specific, pvp, despair, event
FROM character_titlebook;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var characterId = reader.GetInt32(0);
                        for (var category = 0; category < TitleBookStaticDataProvider.CategoryCapacities.Count; category++)
                        {
                            if (reader.IsDBNull(category + 1))
                                continue;

                            var blob = (byte[])reader[category + 1];
                            foreach (var item in LoadCategoryItems(category, blob))
                                pending.Add(Tuple.Create(characterId, category, item.Key, item.Value));
                        }
                    }
                }
            }

            foreach (var item in pending)
                InsertMigratedItem(connection, transaction, item.Item1, item.Item2, item.Item3, item.Item4);
        }

        private static void MigrateLegacyAchievementChunks(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (!TableExists(connection, "character_achievement_chunks"))
                return;

            var pending = new List<Tuple<int, int, int, ItemCore>>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT character_id, chunk_index, entries_blob
FROM character_achievement_chunks;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(2))
                            continue;

                        var characterId = reader.GetInt32(0);
                        var category = reader.GetInt32(1);
                        if (category < 0 || category >= TitleBookStaticDataProvider.CategoryCapacities.Count)
                            continue;

                        var blob = (byte[])reader[2];
                        foreach (var item in ParseLegacyChunkItems(category, blob))
                            pending.Add(Tuple.Create(characterId, category, item.Key, item.Value));
                    }
                }
            }

            foreach (var item in pending)
                InsertMigratedItem(connection, transaction, item.Item1, item.Item2, item.Item3, item.Item4);
        }

        private static IEnumerable<KeyValuePair<int, ItemCore>> ParseLegacyChunkItems(int category, byte[] blob)
        {
            if (blob == null)
                yield break;

            for (var off = 0; off + LegacyTitleBookItemCodec.TitleBookListEntrySize <= blob.Length; off += LegacyTitleBookItemCodec.TitleBookListEntrySize)
            {
                if (!LegacyTitleBookItemCodec.TryDecodeListEntry(blob, off, out var bookIndex, out var core)
                    || core == null
                    || core.IsEmpty)
                    continue;

                yield return new KeyValuePair<int, ItemCore>(bookIndex, core);
            }
        }

        private static void InsertMigratedItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int category,
            int slotIndex,
            ItemCore core)
        {
            if (core == null || core.IsEmpty)
                return;

            if (category < 0 || category >= TitleBookStaticDataProvider.CategoryCapacities.Count)
                return;
            if (slotIndex >= TitleBookStaticDataProvider.CategoryCapacities[category])
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO character_new_titlebook (
    character_id, category, slot_index, item_core, updated_at
) VALUES (
    @cid, @category, @slot, @itemCore, CURRENT_TIMESTAMP
);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@category", category);
                command.Parameters.AddWithValue("@slot", (int)slotIndex);
                command.Parameters.AddWithValue("@itemCore", core.ToBytes());
                command.ExecuteNonQuery();
            }
        }

        private static void EnsureNewTable(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS character_new_titlebook (
    character_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 82),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, category, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);";
                command.ExecuteNonQuery();
            }
        }

        private static List<KeyValuePair<int, ItemCore>> LoadCategoryItems(int category, byte[] blob)
        {
            var capacity = GetCapacity(category);
            blob = NormalizeCategoryBlob(blob, capacity);
            var items = new List<KeyValuePair<int, ItemCore>>(capacity);
            for (var index = 0; index < capacity; index++)
            {
                var record = new byte[LegacyTitleBookItemCodec.PersistedRecordSize];
                Buffer.BlockCopy(blob, index * LegacyTitleBookItemCodec.PersistedRecordSize, record, 0, record.Length);
                var core = LegacyTitleBookItemCodec.DecodePersistedRecord(record);
                if (core == null || core.IsEmpty)
                    continue;

                items.Add(new KeyValuePair<int, ItemCore>(index, core));
            }
            return items;
        }

        private static byte[] NormalizeCategoryBlob(byte[] blob, int capacity)
        {
            var expected = capacity * LegacyTitleBookItemCodec.PersistedRecordSize;
            var result = new byte[expected];
            if (blob == null)
                return result;

            var legacySize = LegacyTitleBookItemCodec.CommonNetworkSize;
            if (LegacyTitleBookItemCodec.PersistedRecordSize != legacySize
                && blob.Length == capacity * legacySize)
            {
                for (var index = 0; index < capacity; index++)
                {
                    Buffer.BlockCopy(
                        blob,
                        index * legacySize,
                        result,
                        index * LegacyTitleBookItemCodec.PersistedRecordSize,
                        legacySize);
                }
                return result;
            }

            Buffer.BlockCopy(blob, 0, result, 0, Math.Min(blob.Length, result.Length));
            return result;
        }

        private static int GetCapacity(int category)
        {
            if (category < 0 || category >= TitleBookStaticDataProvider.CategoryCapacities.Count)
                throw new ArgumentOutOfRangeException(nameof(category));

            return TitleBookStaticDataProvider.CategoryCapacities[category];
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
        }

    }
}
