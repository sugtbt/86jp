using System;
using System.IO;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Shop;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 验证 cerashop.etc 的 [regular package] 段被正确解析: 该段商品(如强化成功/增幅成功幸运礼盒)
    // 曾因 CeraShopProductCatalog 漏解析该段而 TryResolve 恒 false, 购买失败并被客户端显示为
    // "物品栏空间不足"。本自测断言这些商品可解析且点券价读自正确的列(col4)。
    // 依赖: 运行目录 Data/Pvf/Script.pvf (与其它需 PVF 的自测一致)。
    public static class CeraShopSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CERASHOP selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            void CheckProduct(string label, int productId, int expectedItemId, int expectedCoinPrice)
            {
                if (!CeraShopProductCatalog.TryResolve(productId, out var entry) || entry == null)
                {
                    Check($"{label} (commodityNo {productId}) resolves", false);
                    return;
                }

                Check($"{label} (commodityNo {productId}) resolves", true);
                Check($"{label} itemTemplateId == {expectedItemId} (got {entry.ItemTemplateId})", entry.ItemTemplateId == expectedItemId);
                Check($"{label} coinPrice == {expectedCoinPrice} (got {entry.CoinPrice})", entry.CoinPrice == expectedCoinPrice);
            }

            // [regular package] 段商品 —— 修复前该段未解析, 这两件礼盒购买必失败。
            CheckProduct("强化成功幸运礼盒", 102661, 10007836, 9800);
            CheckProduct("增幅成功幸运礼盒", 102660, 10007837, 12800);

            // 回归: 已解析的 [regular package] 段样本商品(Lv80~84 专用礼包)也应正确读到 col4 价格。
            CheckProduct("Lv80~84专用礼包", 102290, 2683268, 2860);

            // [community package] 段(stride=11, 价格 col4) —— 修复前未解析, 婚庆/社区礼包购买必失败。
            CheckProduct("社区礼包(结婚戒指-男)", 102317, 2683326, 18888);

            Check("name tag state overwrites same character and keeps absolute expire time", CheckNameTagState());
            Check("happy-token gift box grants account currency atomically without an inventory item", CheckHappyTokenCeraGiftBox());

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }

        private static bool CheckNameTagState()
        {
            const int accountId = 903001;
            const int characterId = 903002;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-name-tag-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'cerashop-name-tag-selftest', '');
INSERT OR IGNORE INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-name-tag');";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        command.Parameters.AddWithValue("@characterId", characterId);
                        command.ExecuteNonQuery();
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            characterId,
                            1111111,
                            now + 3600);
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            characterId,
                            2222222,
                            now + 7200);
                        transaction.Commit();
                    }

                    var state = NameTagStateRepository.Load(connection, characterId);
                    var nowAfterLoad = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return state.ItemId == 2222222
                        && state.ExpireTime > nowAfterLoad
                        && state.ExpireTime <= nowAfterLoad + 7200;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
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
        }

        private static bool CheckHappyTokenCeraGiftBox()
        {
            const int accountId = 903011;
            const int characterId = 903012;
            const short sourceSlot = 40;
            const int giftBoxItemId = 0x0098AAFE;
            const int expectedGrant = 1800;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "cerashop-happy-token-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var voucher = StackableItemProvider.Load(SpecialRewardRouter.HappyTokenCeraVoucherItemId);
                if (voucher == null
                    || voucher.Name?.Trim('`', ' ', '\t', '\r', '\n') != "欢乐代币券"
                    || voucher.StackableType?.IndexOf("[material]", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'cerashop-happy-token-selftest', '');
INSERT INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'cerashop-happy-token');";
                            command.Parameters.AddWithValue("@accountId", accountId);
                            command.Parameters.AddWithValue("@characterId", characterId);
                            command.ExecuteNonQuery();
                        }

                        var source = ItemCore.Create(ItemCore.KindConsumable, giftBoxItemId);
                        source.Count = 1;
                        InventoryItemRepository.UpsertCharacterSlot(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot,
                            source);
                        transaction.Commit();
                    }

                    var inventory = InventoryService.LoadFromDb(connection, characterId, accountId);
                    if (!InventorySpecialConsumableService.TryOpenPackage0207(
                            inventory,
                            sourceSlot,
                            Array.Empty<int>(),
                            RejectingInventoryOverflowRewardSink.Instance,
                            out var result)
                        || result?.Rewards.Count != 1
                        || result.Rewards[0].SpecialOutcome?.Kind != SpecialRewardKind.HappyTokenCera
                        || result.Rewards[0].ItemTemplateId != SpecialRewardRouter.HappyTokenCeraVoucherItemId
                        || result.Rewards[0].GrantedCount != expectedGrant
                        || result.Rewards[0].SlotIndex != -1
                        || inventory.PendingHappyTokenCeraGrant != expectedGrant
                        || inventory.CountMainItem(SpecialRewardRouter.HappyTokenCeraVoucherItemId) != 0
                        || inventory.GetItem(InventoryListType.Main, sourceSlot) != null)
                        return false;

                    var lease = new InventoryLease(Guid.NewGuid(), characterId, inventory, 1);
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease)
                            || CurrencyService.LoadWallet(connection, transaction, characterId).HappyTokenCera != expectedGrant
                            || CountSourceRows(connection, transaction, characterId, sourceSlot) != 0)
                            return false;
                    }

                    if (CurrencyService.LoadWallet(connection, null, characterId).HappyTokenCera != 0
                        || InventoryItemRepository.LoadCharacterSlot(
                            connection,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot) == null)
                        return false;

                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                            return false;
                        transaction.Commit();
                    }

                    return CurrencyService.LoadWallet(connection, null, characterId).HappyTokenCera == expectedGrant
                        && InventoryItemRepository.LoadCharacterSlot(
                            connection,
                            characterId,
                            InventoryListType.Main,
                            sourceSlot) == null;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
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
        }

        private static int CountSourceRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short sourceSlot)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM character_new_items
WHERE owner_scope = 'character'
  AND owner_id = @characterId
  AND list_type = @listType
  AND slot_index = @sourceSlot;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@sourceSlot", sourceSlot);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }
}
