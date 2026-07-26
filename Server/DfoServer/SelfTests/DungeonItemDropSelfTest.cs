using DfoServer.Game.Dungeon;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    internal static class DungeonItemDropSelfTest
    {
        private const int AccountId = 930019;
        private const int CharacterId = 930119;
        private const short EquipmentSlot = 9;
        private const short StackableSlot = 86;
        private const short TradeSlot = 87;
        private const short HighRaritySlot = 88;
        private const short TitleSlot = 89;
        private const short LockedSlot = 90;
        private const short TradeRestrictedSlot = 91;
        private const short ExpiringStackableSlot = 92;
        private const int EquipmentItemId = 27600;
        private const int StackableItemId = 1004;
        private const int TradeItemId = 100300096;
        private const int HighRarityItemId = 100300001;
        private const int TitleItemId = 100330003;
        private const int StartingGold = 5000;
        private const int EquipmentInstanceValue = 7654321;
        private const int EquipmentPacketValue = 345678901;
        private const byte EquipmentStaleLockId = 7;
        private static int _failures;

        internal static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== DUNGEON_ITEM_DROP selftest ===");

            VerifyRequestParser();
            VerifyNpcItemDropPvfData();

            var tempDb = Path.Combine(Path.GetTempPath(), "dungeon_item_drop_selftest.db");
            DeleteTempDatabase(tempDb);
            SeedInventory(tempDb);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assets = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var drops = new DropService(assets, store);
            var run = new DungeonRun { SceneSlotCounter = 5 };

            VerifyGoldDrop(drops, assets, store, run);

            var expectedStackSceneSlot = unchecked((ushort)(run.SceneSlotCounter + 1));
            var stackableDrop = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, StackableSlot, 2);
            Check("stackable drop succeeds", stackableDrop.Success);
            Check("stackable drop registers next scene slot",
                stackableDrop.Drop.SceneSlot == expectedStackSceneSlot
                && run.Drops.ContainsKey(expectedStackSceneSlot));
            Check("stackable drop preserves template and applied count",
                stackableDrop.Drop.TemplateId == StackableItemId && stackableDrop.Drop.StackCount == 2);
            Check("stackable drop persists remaining count", stackableDrop.RemainingStackCount == 3);

            var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            var remainingStack = snapshot.MainItems.Find(x => x.SlotIndex == StackableSlot);
            Check("database stack is decremented",
                remainingStack != null && remainingStack.CountOrInstanceValue == 3);

            var body = DropItemBuilder.BuildDrop(0x03F1, 209, 279, stackableDrop.Drop, 0x03F1);
            Check("DROP_ITEM NOTI body is 48 bytes", body.Length == 48);
            Check("DROP_ITEM NOTI writes actor and position",
                BitConverter.ToUInt16(body, 0) == 0x03F1
                && BitConverter.ToUInt16(body, 2) == 209
                && BitConverter.ToUInt16(body, 4) == 279);
            Check("DROP_ITEM NOTI writes scene item fields",
                BitConverter.ToUInt16(body, 6) == stackableDrop.Drop.SceneSlot
                && BitConverter.ToUInt32(body, 8) == StackableItemId
                && BitConverter.ToUInt32(body, 13) == 2
                && BitConverter.ToUInt16(body, 46) == 0x03F1);

            var pickup = drops.TryPickup(run, stackableDrop.Drop.SceneSlot, CharacterId, AccountId);
            Check("dropped stack can be picked up again", pickup.Success && !pickup.IsGold);
            snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            remainingStack = snapshot.MainItems.Find(x => x.SlotIndex == StackableSlot);
            Check("pickup restores stack and clears scene drop",
                remainingStack != null
                && remainingStack.CountOrInstanceValue == 5
                && !run.Drops.ContainsKey(stackableDrop.Drop.SceneSlot));

            var expiringDrop = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, ExpiringStackableSlot, 2);
            Check("time-limited stackable persisted as special remains stack-counted",
                expiringDrop.Success
                && expiringDrop.Drop.StackCount == 2
                && expiringDrop.RemainingStackCount == 3);
            var expiringPickup = drops.TryPickup(
                run, expiringDrop.Drop.SceneSlot, CharacterId, AccountId);
            var restoredExpiring = store.LoadCharacterItemListSnapshot(CharacterId, AccountId)
                .MainItems.Find(x => x.SlotIndex == expiringPickup.InventorySlot);
            Check("time-limited stackable pickup preserves count and expiry",
                expiringPickup.Success
                && restoredExpiring != null
                && restoredExpiring.CountOrInstanceValue == 5
                && restoredExpiring.ExpireTime == 2000000000);

            var successAck = DropItemBuilder.BuildDropSuccessAck(
                (byte)InventoryListType.Main,
                unchecked((ushort)StackableSlot),
                40000);
            Check("DROP_ITEM success ACK uses official 8-byte layout",
                successAck.Length == 8
                && successAck[0] == 1
                && successAck[1] == (byte)InventoryListType.Main
                && BitConverter.ToUInt16(successAck, 2) == StackableSlot
                && BitConverter.ToInt32(successAck, 4) == 40000);

            var originalEquipment = store.LoadCharacterItemListSnapshot(CharacterId, AccountId)
                .MainItems.Find(x => x.SlotIndex == EquipmentSlot);
            var equipmentDrop = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, EquipmentSlot, 1);
            Check("equipment drop succeeds", equipmentDrop.Success);
            Check("non-stackable drop reports zero remaining", equipmentDrop.RemainingStackCount == 0);
            Check("equipment drop carries durability",
                equipmentDrop.Drop.TemplateId == EquipmentItemId
                && equipmentDrop.Drop.StackCount == 1
                && equipmentDrop.Drop.Endurance == 45);
            Check("inactive equipment lock id does not block drop and is retained",
                equipmentDrop.Drop.InventoryPayload?.EquipmentLockId == EquipmentStaleLockId
                && equipmentDrop.Drop.InventoryPayload.PacketItem?.EquipmentLockId == EquipmentStaleLockId);
            var equipmentBody = DropItemBuilder.BuildDrop(0x03F1, 201, 324, equipmentDrop.Drop, 0x03F1);
            Check("equipment DROP_ITEM NOTI writes quality seed instead of count",
                equipmentBody.Length == 48
                && equipmentBody[12] == 12
                && BitConverter.ToUInt32(equipmentBody, 13) == EquipmentPacketValue
                && BitConverter.ToUInt32(equipmentBody, 13) != ItemQuality.TopQualitySeed
                && BitConverter.ToUInt16(equipmentBody, 17) == 45);
            snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            Check("equipment is removed from its inventory slot",
                snapshot.MainItems.Find(x => x.SlotIndex == EquipmentSlot) == null);

            var rejected = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, EquipmentSlot, 1);
            Check("missing source item does not create another scene drop",
                !rejected.Success && run.Drops.Count == 1);

            var equipmentPickup = drops.TryPickup(
                run, equipmentDrop.Drop.SceneSlot, CharacterId, AccountId);
            Check("dropped equipment can be picked up again",
                equipmentPickup.Success && equipmentPickup.RestoredItem != null);
            var restoredEquipment = store.LoadCharacterItemListSnapshot(CharacterId, AccountId)
                .MainItems.Find(x => x.SlotIndex == equipmentPickup.InventorySlot);
            Check("equipment pickup preserves complete protocol instance",
                CommonItemsEqual(originalEquipment, restoredEquipment));
            var restoredEquipmentUpdate = ItemListUpdateBuilder.BuildCommonUpdates(new[] { restoredEquipment });
            Check("equipment pickup UPDATE_ITEM_LIST preserves non-top quality seed",
                restoredEquipmentUpdate.Length == 87
                && BitConverter.ToUInt32(restoredEquipmentUpdate, 9) == EquipmentPacketValue
                && BitConverter.ToUInt32(restoredEquipmentUpdate, 9) != ItemQuality.TopQualitySeed);
            Check("equipment pickup preserves database instance value and extra JSON",
                LoadItemInstanceValue(tempDb, equipmentPickup.InventorySlot) == EquipmentInstanceValue
                && string.Equals(
                    LoadItemExtraJson(tempDb, equipmentPickup.InventorySlot),
                    InventoryItemCodec.SerializeCommon(originalEquipment),
                    StringComparison.Ordinal));

            VerifyCurrentPvfRejections(drops, run);
            VerifyTemplateSceneDrop(drops);

            DeleteTempDatabase(tempDb);
            Console.WriteLine(_failures == 0 ? "PASS" : $"FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void VerifyNpcItemDropPvfData()
        {
            var action = ActFile.Parse(@"
[BEHAVIOR]
[NPC ITEM DROP]
`particle.ptl`
[/BEHAVIOR]");
            Check("ACT parser detects nested NPC ITEM DROP behavior",
                action.HasNpcItemDrop);

            Check("non-item ACT does not report NPC ITEM DROP",
                !ActFile.Parse("[BEHAVIOR]\n[DIALOG]\n`test`\n[/BEHAVIOR]")
                    .HasNpcItemDrop);

            var resolved = DungeonNpcItemDropData.TryResolve(
                19006,
                out var scene,
                out var rejectReason);
            Check("time illusion room resolves one PVF NPC item drop action",
                resolved
                && scene != null
                && scene.MapId == 19006
                && scene.ObjectCode == 48548
                && scene.X == 448
                && scene.Y == 248
                && scene.ActionPath.EndsWith(
                    "action/bossunique_siran.act",
                    StringComparison.OrdinalIgnoreCase));
            Check("resolved NPC item drop has no ambiguity diagnostic",
                resolved && string.IsNullOrEmpty(rejectReason));

            Check("get-item-check quest parses explicit dungeon scope and items",
                QuestData.TryGetNpcItemDropQuestTarget(
                    2358,
                    3066,
                    0,
                    out var target)
                && target.DungeonId == 3066
                && target.Difficulty == -1
                && target.ItemIds.Count == 30);
            Check("get-item-check quest rejects an unrelated dungeon",
                !QuestData.TryGetNpcItemDropQuestTarget(
                    2358,
                    3065,
                    0,
                    out _));

            var active = new List<ActiveQuest>
            {
                new ActiveQuest
                {
                    QuestId = 2358,
                    TriggerValue = 1,
                },
            };
            VerifyNpcDropJobCandidates(active, 0, 5, "swordman");
            VerifyNpcDropJobCandidates(active, 1, 6, "fighter");
            VerifyNpcDropJobCandidates(active, 2, 5, "gunner");
            VerifyNpcDropJobCandidates(active, 3, 5, "mage");
            VerifyNpcDropJobCandidates(active, 4, 4, "priest");
            VerifyNpcDropJobCandidates(active, 6, 5, "thief");

            active[0].TriggerValue = 0;
            Check("completed get-item-check quest no longer matches NPC drop",
                DungeonNpcItemDropCoordinator.ResolveQuestMatches(
                    active,
                    3066,
                    0,
                    0).Count == 0);

            var run = new DungeonRun();
            Check("NPC item drop run marker accepts a quest once",
                run.TryMarkNpcItemDropGenerated(2358));
            Check("NPC item drop run marker rejects a duplicate command",
                !run.TryMarkNpcItemDropGenerated(2358));
            run.UnmarkNpcItemDropGenerated(2358);
            Check("failed NPC item registration can release its run marker",
                run.TryMarkNpcItemDropGenerated(2358));

            Check("EVENT_NPC_DROP_ITEM command keeps extracted packet id",
                (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_ == 0x0253);
            var success = CommonPacketBodyBuilder.BuildSuccessAck();
            Check("EVENT_NPC_DROP_ITEM success ACK is one byte 01",
                success.Length == 1 && success[0] == 1);
        }

        private static void VerifyNpcDropJobCandidates(
            IReadOnlyList<ActiveQuest> active,
            byte job,
            int expectedCount,
            string label)
        {
            var matches = DungeonNpcItemDropCoordinator.ResolveQuestMatches(
                active,
                3066,
                0,
                job);
            Check($"NPC item drop filters exact {label} usable-job candidates",
                matches.Count == 1
                && matches[0].QuestId == 2358
                && matches[0].ItemIds.Count == expectedCount);
        }

        private static void VerifyTemplateSceneDrop(DropService drops)
        {
            var run = new DungeonRun();
            var registered = drops.TryRegisterTemplateDrop(
                run,
                101030189,
                1,
                out var drop);
            Check("fixed-template scene drop registers in the dungeon run",
                registered
                && drop.SceneSlot == 1
                && drop.TemplateId == 101030189
                && drop.StackCount == 1
                && run.Drops.TryGetValue(drop.SceneSlot, out var registeredDrop)
                && registeredDrop.TemplateId == drop.TemplateId);

            var body = DropItemBuilder.BuildDrop(
                0x03F1,
                448,
                248,
                drop,
                0x03F1);
            Check("NPC scene drop notification writes actor, PVF position and item",
                body.Length == 48
                && BitConverter.ToUInt16(body, 0) == 0x03F1
                && BitConverter.ToUInt16(body, 2) == 448
                && BitConverter.ToUInt16(body, 4) == 248
                && BitConverter.ToUInt32(body, 8) == 101030189
                && BitConverter.ToUInt16(body, 46) == 0x03F1);

            var beforeSlot = run.SceneSlotCounter;
            Check("invalid fixed-template drop is rejected without consuming a slot",
                !drops.TryRegisterTemplateDrop(run, int.MaxValue, 1, out _)
                && run.SceneSlotCounter == beforeSlot);
        }

        private static void VerifyRequestParser()
        {
            var equipmentRequest = DropItemRequest.Parse(new byte[]
            {
                0xD1, 0x00, 0x17, 0x01, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            });
            Check("captured equipment request decodes position",
                equipmentRequest.PositionX == 209 && equipmentRequest.PositionY == 279);
            Check("captured equipment request decodes inventory target",
                equipmentRequest.ListType == InventoryListType.Main
                && equipmentRequest.SlotIndex == 9
                && equipmentRequest.Count == 1);

            var secondRequest = DropItemRequest.Parse(new byte[]
            {
                0x46, 0x01, 0x44, 0x01, 0x00, 0x0B, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            });
            Check("second captured request decodes consistently",
                secondRequest.PositionX == 326
                && secondRequest.PositionY == 324
                && secondRequest.SlotIndex == 11);

            var goldRequest = DropItemRequest.Parse(new byte[]
            {
                1, 0, 2, 0, 0, 0, 0, 0xE8, 0x03, 0, 0, 0,
            });
            Check("gold request accepts main-list slot zero",
                goldRequest.SlotIndex == 0 && goldRequest.Count == 1000);

            var largeCountBody = new byte[]
            {
                1, 0, 2, 0, 0, 86, 0, 0, 0, 0, 0, 0,
            };
            BitConverter.GetBytes(40000).CopyTo(largeCountBody, 7);
            Check("ordinary item request keeps signed int32 count",
                DropItemRequest.Parse(largeCountBody).Count == 40000);

            Check("zero-count request is rejected", ThrowsArgumentException(new byte[12]));
            Check("non-main inventory request is rejected", ThrowsArgumentException(new byte[]
            {
                1, 0, 1, 0, 1, 9, 0, 1, 0, 0, 0, 0,
            }));
            Check("wrong-length request is rejected", ThrowsArgumentException(new byte[11]));
        }

        private static void VerifyGoldDrop(
            DropService drops,
            IAssetService assets,
            SqliteInventoryStore store,
            DungeonRun run)
        {
            var overLimit = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, 0, 1001);
            Check("gold drop rejects more than 1000", !overLimit.Success);
            Check("rejected gold drop leaves wallet unchanged", LoadGold(store) == StartingGold);

            var goldDrop = drops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, 0, 1000);
            Check("gold drop accepts official maximum 1000",
                goldDrop.Success
                && goldDrop.Drop.IsGold
                && goldDrop.Drop.IsPlayerDropped
                && goldDrop.Drop.StackCount == 1000
                && goldDrop.RemainingStackCount == StartingGold - 1000);
            Check("accepted gold drop decrements wallet", LoadGold(store) == StartingGold - 1000);

            var pickup = drops.TryPickup(run, goldDrop.Drop.SceneSlot, CharacterId, AccountId);
            Check("player-dropped gold pickup bypasses monster gold bonus",
                pickup.Success
                && pickup.IsGold
                && pickup.GoldAmount == 1000
                && pickup.ExtraGold == 0
                && LoadGold(store) == StartingGold);

            var failedPickupDrops = new DropService(new ThrowingGoldAssetService(assets), store);
            var failedPickupDrop = failedPickupDrops.TryDropInventoryItem(
                run, CharacterId, InventoryListType.Main, 0, 1);
            var failedPickup = failedPickupDrops.TryPickup(
                run, failedPickupDrop.Drop.SceneSlot, CharacterId, AccountId);
            Check("gold pickup persistence failure keeps scene drop",
                failedPickupDrop.Success
                && !failedPickup.Success
                && failedPickup.FailReason == PickupFailReason.PersistenceFailed
                && run.Drops.ContainsKey(failedPickupDrop.Drop.SceneSlot)
                && LoadGold(store) == StartingGold - 1);
            var recoveredPickup = drops.TryPickup(
                run, failedPickupDrop.Drop.SceneSlot, CharacterId, AccountId);
            Check("gold scene drop remains recoverable after persistence failure",
                recoveredPickup.Success
                && !run.Drops.ContainsKey(failedPickupDrop.Drop.SceneSlot)
                && LoadGold(store) == StartingGold);
        }

        private static void VerifyCurrentPvfRejections(DropService drops, DungeonRun run)
        {
            var initialDropCount = run.Drops.Count;
            Check("current-PVF [trade] item is rejected",
                !drops.TryDropInventoryItem(
                    run, CharacterId, InventoryListType.Main, TradeSlot, 1).Success);
            Check("current-PVF rarity above 2 is rejected",
                !drops.TryDropInventoryItem(
                    run, CharacterId, InventoryListType.Main, HighRaritySlot, 1).Success);
            Check("current-PVF title equipment is rejected",
                !drops.TryDropInventoryItem(
                    run, CharacterId, InventoryListType.Main, TitleSlot, 1).Success);
            Check("locked equipment is rejected",
                !drops.TryDropInventoryItem(
                    run, CharacterId, InventoryListType.Main, LockedSlot, 1).Success);
            Check("instance trade restriction is rejected",
                !drops.TryDropInventoryItem(
                    run, CharacterId, InventoryListType.Main, TradeRestrictedSlot, 1).Success);
            Check("rejected items do not create scene drops", run.Drops.Count == initialDropCount);
        }

        private static bool ThrowsArgumentException(byte[] body)
        {
            try
            {
                DropItemRequest.Parse(body);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static void SeedInventory(string databasePath)
        {
            var equipment = CreateEquipmentProtocolItem(EquipmentSlot, false);
            var tradeRestricted = CreateEquipmentProtocolItem(TradeRestrictedSlot, true);
            using var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'dungeon-item-drop-selftest', '');

INSERT INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'dungeon-item-drop-selftest');

INSERT INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, equipment_lock_id, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, 0, 0, 'special',
     @startingGold, @startingGold, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @equipmentSlot, @equipmentItemId, 'equipment',
     @equipmentPacketValue, @equipmentInstanceValue, @equipmentDurability, @equipmentSealFlag, 0, @equipmentStaleLockId,
     @equipmentExpireTime, @equipmentMarker16, 0, @equipmentExtraJson),
    ('character', @characterId, @characterId, 0, @stackableSlot, @stackableItemId, 'stackable',
     5, 5, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @tradeSlot, @tradeItemId, 'equipment',
     999999998, 0, 1, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @highRaritySlot, @highRarityItemId, 'equipment',
     999999998, 0, 1, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @titleSlot, @titleItemId, 'equipment',
     999999998, 0, 1, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @lockedSlot, @equipmentItemId, 'equipment',
     999999998, 0, 1, 0, 0, 5, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @tradeRestrictedSlot, @equipmentItemId, 'equipment',
     999999998, 0, 1, 0, 0, 0, 0, -1, 0, @tradeRestrictedExtraJson),
    ('character', @characterId, @characterId, 0, @expiringStackableSlot, @stackableItemId, 'special',
     5, 5, 0, 0, 0, 0, 2000000000, 0, 0, '{}');

INSERT INTO character_item_locks (
    character_id, equipment_lock_id, inventory_list_type, slot, state, remaining_seconds)
VALUES (@characterId, 5, 0, @lockedSlot, 1, NULL);

INSERT INTO character_equipped_entries (
    character_id, slot, item_id, expire_time, equipment_lock_id, raw_entry)
VALUES (@characterId, 0, 26626, 0, 0, X'00');";
            command.Parameters.AddWithValue("@accountId", AccountId);
            command.Parameters.AddWithValue("@characterId", CharacterId);
            command.Parameters.AddWithValue("@startingGold", StartingGold);
            command.Parameters.AddWithValue("@equipmentSlot", EquipmentSlot);
            command.Parameters.AddWithValue("@equipmentItemId", EquipmentItemId);
            command.Parameters.AddWithValue("@equipmentPacketValue", EquipmentPacketValue);
            command.Parameters.AddWithValue("@equipmentInstanceValue", EquipmentInstanceValue);
            command.Parameters.AddWithValue("@equipmentDurability", equipment.Durability);
            command.Parameters.AddWithValue("@equipmentSealFlag", equipment.SealFlag);
            command.Parameters.AddWithValue("@equipmentExpireTime", equipment.ExpireTime);
            command.Parameters.AddWithValue("@equipmentMarker16", equipment.Marker16);
            command.Parameters.AddWithValue("@equipmentStaleLockId", EquipmentStaleLockId);
            command.Parameters.AddWithValue("@equipmentExtraJson", InventoryItemCodec.SerializeCommon(equipment));
            command.Parameters.AddWithValue("@stackableSlot", StackableSlot);
            command.Parameters.AddWithValue("@stackableItemId", StackableItemId);
            command.Parameters.AddWithValue("@tradeSlot", TradeSlot);
            command.Parameters.AddWithValue("@tradeItemId", TradeItemId);
            command.Parameters.AddWithValue("@highRaritySlot", HighRaritySlot);
            command.Parameters.AddWithValue("@highRarityItemId", HighRarityItemId);
            command.Parameters.AddWithValue("@titleSlot", TitleSlot);
            command.Parameters.AddWithValue("@titleItemId", TitleItemId);
            command.Parameters.AddWithValue("@lockedSlot", LockedSlot);
            command.Parameters.AddWithValue("@tradeRestrictedSlot", TradeRestrictedSlot);
            command.Parameters.AddWithValue("@tradeRestrictedExtraJson", InventoryItemCodec.SerializeCommon(tradeRestricted));
            command.Parameters.AddWithValue("@expiringStackableSlot", ExpiringStackableSlot);
            command.ExecuteNonQuery();
        }

        private static CommonInventoryItem CreateEquipmentProtocolItem(short slot, bool tradeRestricted)
        {
            var prefix = new byte[] { 0x78, 0x56, 0x34, 0x12, 4, 2, 0x41, 0x01 };
            var middle = new byte[17];
            var tail = new byte[37];
            var jewel = new byte[30];
            for (var index = 0; index < middle.Length; index++)
                middle[index] = (byte)(0x20 + index);
            for (var index = 0; index < tail.Length; index++)
                tail[index] = (byte)(0x40 + index);
            for (var index = 0; index < jewel.Length; index++)
                jewel[index] = (byte)(0x70 + index);
            tail[29] = tradeRestricted ? (byte)1 : (byte)0;

            return new CommonInventoryItem
            {
                SlotIndex = slot,
                ItemTemplateId = EquipmentItemId,
                CountOrInstanceValue = EquipmentPacketValue,
                ExtData0 = 12,
                Durability = 45,
                SealFlag = 3,
                PrefixData0E = prefix,
                Marker16 = -1,
                MiddleData1A = middle,
                ExpireTime = 123456789,
                TailData2F = tail,
                JewelSocket = jewel,
            };
        }

        private static bool CommonItemsEqual(CommonInventoryItem expected, CommonInventoryItem actual)
        {
            return expected != null
                && actual != null
                && expected.ItemTemplateId == actual.ItemTemplateId
                && expected.CountOrInstanceValue == actual.CountOrInstanceValue
                && expected.ExtData0 == actual.ExtData0
                && expected.Durability == actual.Durability
                && expected.SealFlag == actual.SealFlag
                && expected.Marker16 == actual.Marker16
                && expected.ExpireTime == actual.ExpireTime
                && expected.EquipmentLockId == actual.EquipmentLockId
                && ByteArraysEqual(expected.PrefixData0E, actual.PrefixData0E)
                && ByteArraysEqual(expected.MiddleData1A, actual.MiddleData1A)
                && ByteArraysEqual(expected.TailData2F, actual.TailData2F)
                && ByteArraysEqual(expected.JewelSocket, actual.JewelSocket);
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        private static int LoadGold(SqliteInventoryStore store)
        {
            return store.LoadCharacterItemListSnapshot(CharacterId, AccountId)
                .MainItems.Find(x => x.SlotIndex == 0)?.CountOrInstanceValue ?? -1;
        }

        private static int LoadItemInstanceValue(string databasePath, short slotIndex)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT instance_value
FROM character_items
WHERE character_id = @characterId AND list_type = 0 AND slot_index = @slotIndex;";
            command.Parameters.AddWithValue("@characterId", CharacterId);
            command.Parameters.AddWithValue("@slotIndex", slotIndex);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static string LoadItemExtraJson(string databasePath, short slotIndex)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = 0 AND slot_index = @slotIndex;";
            command.Parameters.AddWithValue("@characterId", CharacterId);
            command.Parameters.AddWithValue("@slotIndex", slotIndex);
            return Convert.ToString(command.ExecuteScalar());
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                _failures++;
        }

        private sealed class ThrowingGoldAssetService : IAssetService
        {
            private readonly IAssetService _inner;

            internal ThrowingGoldAssetService(IAssetService inner)
            {
                _inner = inner;
            }

            public DbScope OpenScope(int characterId, int accountId)
                => _inner.OpenScope(characterId, accountId);

            public bool TryAddItem(DbScope scope, int itemTemplateId, int count, out short assignedSlot)
                => _inner.TryAddItem(scope, itemTemplateId, count, out assignedSlot);

            public bool TryRemoveItem(DbScope scope, int itemTemplateId, int count, out short slot, out int remaining)
                => _inner.TryRemoveItem(scope, itemTemplateId, count, out slot, out remaining);

            public int CountItem(DbScope scope, int itemTemplateId)
                => _inner.CountItem(scope, itemTemplateId);

            public WalletSnapshot LoadWallet(DbScope scope)
                => _inner.LoadWallet(scope);

            public int GrantGold(DbScope scope, int amount)
                => throw new InvalidOperationException("injected gold persistence failure");

            public bool TrySpendGold(DbScope scope, int amount)
                => _inner.TrySpendGold(scope, amount);

            public void GrantLuckyStar(DbScope scope, int amount)
                => _inner.GrantLuckyStar(scope, amount);

            public bool TrySpendLuckyStar(DbScope scope, int amount)
                => _inner.TrySpendLuckyStar(scope, amount);

            public CharacterItemListSnapshot LoadSnapshot(DbScope scope)
                => _inner.LoadSnapshot(scope);
        }
    }
}
