using System;
using System.IO;
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
    }
}
