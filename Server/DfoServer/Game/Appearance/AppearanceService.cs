using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Appearance
{
    public static class AppearanceService
    {
        private const byte TitleAppearanceSlot = 12;

        public static byte[] UpdateAndBroadcast(
            PlayerContext player,
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            int characterId, int accountId)
        {
            var updated = LoadOnlineAppearanceFromInventory(characterId);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(characterId, updated);

            return BuildNoti2Body(player);
        }

        public static byte[] RefreshPlayerAppearance(
            PlayerContext player,
            ICharacterRepository characterRepository)
        {
            if (player == null)
                return Array.Empty<byte>();

            var updated = LoadOnlineAppearanceFromInventory(player.CharacterId);
            player.AppearanceEntries = updated;
            if (characterRepository != null && player.CharacterId > 0)
                characterRepository.UpdateAppearance(player.CharacterId, updated);

            return BuildNoti2Body(player);
        }

        public static byte[] SetCloneTitleAndBroadcast(
            PlayerContext player,
            ICharacterRepository characterRepository,
            int characterId,
            int cloneTitleItemId)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId);
            var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
            player.Subtype0Tail = tail;
            var updated = LoadOnlineAppearanceFromInventory(characterId);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(characterId, updated);

            return BuildNoti2Body(player);
        }

        public static void PersistCloneTitle(int characterId, int cloneTitleItemId)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId);
        }

        public static byte[] SetRuntimeCloneTitleAndBuildNoti2(PlayerContext player, int cloneTitleItemId)
        {
            if (player == null)
                return Array.Empty<byte>();

            if (player.CharacterId > 0)
            {
                SaveCloneTitleItemId(player.CharacterId, cloneTitleItemId);
                var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
                tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                player.Subtype0Tail = tail;
                player.AppearanceEntries = LoadOnlineAppearanceFromInventory(player.CharacterId);
            }

            return BuildNoti2Body(player);
        }

        public static byte[] BuildCloneTitleAckBody(int cloneTitleItemId, byte state = 0, byte suppressMessage = 0)
        {
            var ack = new byte[5];
            ack[0] = 0x01;
            BitConverter.GetBytes(cloneTitleItemId).CopyTo(ack, 1);
            return ack;
        }

        public static CharacterAppearanceEntry[] LoadAppearanceFromEquipEntries(int characterId)
        {
            return LoadOnlineAppearanceFromInventory(characterId);
        }

        public static CharacterAppearanceEntry[] LoadOnlineAppearanceFromInventory(int characterId)
        {
            var projectionBuilder = new Noti2InventoryProjectionBuilder();
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    return projectionBuilder.BuildAppearanceEntries(lease.Inventory);
            }

            return Array.Empty<CharacterAppearanceEntry>();
        }

        public static CharacterAppearanceEntry[] LoadCharacterAppearanceFromDb(int characterId)
        {
            if (characterId <= 0)
                return Array.Empty<CharacterAppearanceEntry>();

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var equippedItems = InventoryItemRepository.LoadEquippedItems(connection, characterId);
                var avatarDetails = AvatarDetailRepository.LoadForCharacter(connection, characterId);
                return new Noti2InventoryProjectionBuilder()
                    .BuildAppearanceEntries(equippedItems, avatarDetails);
            }
        }

        public static Dictionary<int, CharacterAppearanceEntry[]> LoadRosterAppearancesFromDb(int accountId)
        {
            var result = new Dictionary<int, CharacterAppearanceEntry[]>();
            if (accountId <= 0)
                return result;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                var equippedItems = InventoryItemRepository.LoadEquippedItemsByAccount(connection, accountId);
                var avatarDetails = AvatarDetailRepository.LoadForAccount(connection, accountId);
                var grouped = new Dictionary<int, List<InventoryItem>>();
                foreach (var item in equippedItems)
                {
                    if (item == null || !item.CharacterId.HasValue || item.CharacterId.Value <= 0)
                        continue;

                    if (!grouped.TryGetValue(item.CharacterId.Value, out var list))
                    {
                        list = new List<InventoryItem>();
                        grouped[item.CharacterId.Value] = list;
                    }

                    list.Add(item);
                }

                var projectionBuilder = new Noti2InventoryProjectionBuilder();
                foreach (var pair in grouped)
                    result[pair.Key] = projectionBuilder.BuildAppearanceEntries(pair.Value, avatarDetails);
            }

            return result;
        }

        internal static CharacterAppearanceEntry[] BuildFromEquippedEntries(
            IEnumerable<EquippedEntrySnapshot> equippedEntries)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (equippedEntries == null)
                return result.ToArray();

            foreach (var entry in equippedEntries)
            {
                if (entry == null)
                    continue;

                // 外观列表的 itemId 保持真实穿戴模板；替换称号动画还会由 subtype0 tail 首字段刷新。
                // slot23 是客户端原生副武器外观槽；守护者主盾也通过同一标准装备槽进入快照。
                if (entry.Slot > TitleAppearanceSlot
                    && entry.Slot != (byte)EquipmentType.SupportWeapon)
                    continue;
                var core = entry.Core;
                if (core == null || core.ItemId == 0)
                    continue;

                int displayItemId = core.ItemId;

                result.Add(new CharacterAppearanceEntry(
                    (byte)entry.Slot,
                    displayItemId,
                    4,
                    BuildAppearanceExpansionData(core),
                    BuildAppearanceState(core),
                    0,
                    0u,
                    core.EnchantUpgradeCount));
            }

            return result.ToArray();
        }

        internal static byte BuildAppearanceState(ItemCore core)
        {
            if (core == null)
                return 0;

            var upgrade = core.Attr & 0x1F;
            return unchecked((byte)(upgrade * 2 + (core.AmplifyType != 0 ? 1 : 0)));
        }

        private static byte[] BuildAppearanceExpansionData(ItemCore core)
        {
            return new byte[4];
        }

        public static int LoadCloneTitleItemId(int characterId)
        {
            if (characterId <= 0)
                return 0;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT clone_title_item_id FROM characters WHERE character_id = @cid LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        public static void SaveCloneTitleItemId(int characterId, int cloneTitleItemId)
        {
            if (characterId <= 0)
                return;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE characters
SET clone_title_item_id = @cloneTitleItemId,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cloneTitleItemId", cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        public static byte[] BuildNoti2Body(PlayerContext player)
        {
            var record = new CharacterRecord
            {
                CharacterId = player.CharacterId,
                Name = player.Name,
                Job = player.Job,
                GrowType = player.GrowType,
                Level = player.Level,
                UserState = player.UserState,
                Appearance = player.AppearanceEntries,
                Subtype0Tail = player.Subtype0Tail,
            };

            if (record.Subtype0Tail == null && player.CharacterId > 0)
            {
                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                    .Load(player.CharacterId);
                player.Subtype0Tail = record.Subtype0Tail;
            }

            if (record.Subtype0Tail == null)
                record.Subtype0Tail = new UserInfoMinimumTailSnapshot();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteUInt16(0x0001);
            writer.WriteUInt16(player.UserId);
            writer.WriteDstr(player.Name);
            writer.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
            return writer.ToArray();
        }
    }
}
