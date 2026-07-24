using System;
using System.IO;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class SlotExpansionQuestSelfTest
    {
        private const int CharacterId = 169001;
        private const int AccountId = 169001;
        private const ushort SupportSlotQuestId = 649;
        private const ushort MagicStoneQuestId = 650;

        public static int Run()
        {
            Console.WriteLine("=== SLOT_EXPANSION_QUEST selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "slot-expansion-quest.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("slot-expansion-test"),
                Job = 0,
                GrowType = 0,
                Level = 65,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);

            var failures = 0;

            var supportAck = questService.HandleFinishQuest(CharacterId, BuildFinishBody(SupportSlotQuestId));
            Check("support quest success", IsSuccessAck(supportAck), ref failures);
            Check("support quest chainType is slot expansion",
                TryReadChain(supportAck, out var supportChainType, out var supportSlotId)
                    && supportChainType == GameWorld.QuestData.ChainTypeSlotExpansion
                    && supportSlotId == 21,
                ref failures);
            Check("support slot flag persisted", LoadExEquipSlotStat(dbPath) == 0x01, ref failures);

            var magicAck = questService.HandleFinishQuest(CharacterId, BuildFinishBody(MagicStoneQuestId));
            Check("magic stone quest success", IsSuccessAck(magicAck), ref failures);
            Check("magic stone quest chainType is slot expansion",
                TryReadChain(magicAck, out var magicChainType, out var magicSlotId)
                    && magicChainType == GameWorld.QuestData.ChainTypeSlotExpansion
                    && magicSlotId == 22,
                ref failures);
            Check("support + magic stone flags persisted", LoadExEquipSlotStat(dbPath) == 0x03, ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildFinishBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static bool IsSuccessAck(QuestFinishResult result)
        {
            return result != null && result.Success;
        }

        private static bool TryReadChain(QuestFinishResult result, out int chainType, out int rewardValue)
        {
            chainType = result != null ? result.ChainType : -1;
            rewardValue = result != null ? result.GrowNumber : -1;
            return result != null && result.Success;
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "slot-expansion-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static int LoadExEquipSlotStat(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT ex_equip_slot_stat FROM characters WHERE character_id=@cid;";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
