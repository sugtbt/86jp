using System;
using System.IO;
using System.Linq;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class FirstAwakeningClearSelfTest
    {
        private const int CharacterId = 296003;
        private const int AccountId = 296003;

        public static int Run()
        {
            Console.WriteLine("=== FIRST_AWAKENING_CLEAR selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "first-awakening-clear.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var repository = new SqliteCharacterRepository(dbPath, ServerPaths.SchemaFilePath);
            SeedAccount(dbPath);
            repository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("first-awaken-test"),
                Job = 12,
                GrowType = 1,
                Level = 53,
            });
            SeedSubtype1(dbPath);

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var service = new QuestService(connStr);
            var failures = 0;
            var expectedQuestIds = GameWorld.QuestData.GetFirstAwakeningQuestIds(12, 1);

            Check("PVF has matching first-awakening quests", expectedQuestIds.Count > 0, ref failures);
            Check("PVF chain contains awakening reward",
                expectedQuestIds.Any(id =>
                {
                    var reward = GameWorld.QuestData.GetRewardExp(
                        id, playerLevel: 53, playerJob: 12, playerGrowType: 1);
                    return reward.ChainType == 2 && reward.GrowNumber == 1;
                }),
                ref failures);

            var result = service.TryClearFirstAwakeningQuests(CharacterId);
            Check("ticket action succeeds", result.Success, ref failures);
            Check("all matching quests are marked clear",
                result.Success && expectedQuestIds.All(id => new QuestRepository(connStr).IsQuestCleared(CharacterId, id)),
                ref failures);
            Check("grow type advances to first awakening", LoadGrowType(connStr) == 0x11, ref failures);

            var repeated = service.TryClearFirstAwakeningQuests(CharacterId);
            Check("already-awakened character is rejected", !repeated.Success, ref failures);

            Console.WriteLine($"matched quests: {string.Join(",", expectedQuestIds)}");
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedAccount(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(@aid,@mid,'');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "first-awaken-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static int LoadGrowType(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT grow_type FROM characters WHERE character_id=@cid";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private static void SeedSubtype1(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT OR IGNORE INTO character_subtype1_fields(character_id) VALUES(@cid);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
            if (!condition)
                failures++;
        }
    }
}
