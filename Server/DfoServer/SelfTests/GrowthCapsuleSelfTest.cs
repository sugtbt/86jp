using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class GrowthCapsuleSelfTest
    {
        private const int AccountId = 940017;
        private const int OtherAccountId = 940018;
        private const int CharacterId = 940117;
        private const int OtherCharacterId = 940118;
        private const int ForeignCharacterId = 940119;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== GROWTH_CAPSULE selftest ===");

            Check("PVF max gage exp", GrowthCapsuleDataProvider.RequiredExpPerCapsule == 11691495u);
            Check("PVF exp gain rate", GrowthCapsuleDataProvider.ExpGainRatePercent == 100);
            Check("PVF reward item", GrowthCapsuleDataProvider.RewardItemId == 10147584);
            Check("PVF reward count", GrowthCapsuleDataProvider.RewardItemCount == 1);
            Check("capsule gain derives from honor overflow", GrowthCapsuleDataProvider.CalculateExpGain(273) == 273);

            var required = GrowthCapsuleDataProvider.RequiredExpPerCapsule;
            var overfilled = GrowthCapsuleDataProvider.Calculate((ulong)required * 2 + 37);
            Check("capsule progress is capped at one full gage",
                overfilled.TotalExp == required);
            Check("claimable capsule displays full bar",
                GrowthCapsuleDataProvider.GetDisplayProgress(ExpTableProvider.MaxLevel, overfilled) == required);
            Check("non-max character hides capsule progress",
                GrowthCapsuleDataProvider.GetDisplayProgress((byte)(ExpTableProvider.MaxLevel - 1), overfilled) == 0);

            var successAck = GrowthCapsulePacketBuilder.BuildClaimAck(
                true, GrowthCapsuleDataProvider.RewardItemId, GrowthCapsuleDataProvider.RewardItemCount);
            Check("0x025B success ACK is result + three u32",
                successAck.Length == 13
                && successAck[0] == 0
                && BitConverter.ToUInt32(successAck, 1) == 0
                && BitConverter.ToUInt32(successAck, 5) == GrowthCapsuleDataProvider.RewardItemId
                && BitConverter.ToUInt32(successAck, 9) == GrowthCapsuleDataProvider.RewardItemCount);
            var failureAck = GrowthCapsulePacketBuilder.BuildClaimAck(false);
            Check("0x025B failure ACK only carries result", failureAck.Length == 1 && failureAck[0] == 1);

            var databasePath = Path.Combine(
                Path.GetTempPath(), "growth_capsule_selftest_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath, ServerPaths.SchemaFilePath);
                Seed(connectionString);

                var characterRepository = new SqliteCharacterRepository(
                    databasePath, ServerPaths.SchemaFilePath);
                var accountExperience = new AccountExperienceProgressService(
                    characterRepository, databasePath, ServerPaths.SchemaFilePath);
                var progress = accountExperience.AddHonorAndGrowthCapsuleExp(AccountId, 321);
                Check("honor and capsule update together",
                    progress.Honor.TotalHonorExp == 321
                    && progress.GrowthCapsule.TotalExp == 321
                    && progress.GrowthCapsuleExpGain == 321);

                var repository = new GrowthCapsuleProgressRepository(
                    databasePath, ServerPaths.SchemaFilePath);
                Check("capsule progress is shared by account characters",
                    repository.LoadSummary(AccountId).TotalExp == 321
                    && LoadAccountId(connectionString, OtherCharacterId) == AccountId);

                SetGrowthCapsuleExp(connectionString, required - 19);
                var capped = accountExperience.AddHonorAndGrowthCapsuleExp(AccountId, 321);
                Check("capsule gain stops exactly at one full gage",
                    capped.GrowthCapsule.TotalExp == required
                    && capped.GrowthCapsuleExpGain == 19);
                var alreadyFull = accountExperience.AddHonorAndGrowthCapsuleExp(AccountId, 321);
                Check("full capsule does not accumulate more exp",
                    alreadyFull.GrowthCapsule.TotalExp == required
                    && alreadyFull.GrowthCapsuleExpGain == 0);

                var claimService = new GrowthCapsuleClaimService(databasePath, ServerPaths.SchemaFilePath);
                var successInventory = new InventoryService(CharacterId, AccountId);
                var successLease = new InventoryLease(Guid.NewGuid(), CharacterId, successInventory, 1);
                var success = claimService.Claim(successLease);
                Check("successful claim grants configured item",
                    success.Success
                    && successInventory.CountMainItem(GrowthCapsuleDataProvider.RewardItemId) == 1);
                Check("successful claim resets the single gage",
                    success.Summary.TotalExp == 0 && repository.LoadSummary(AccountId).TotalExp == 0);

                SetGrowthCapsuleExp(connectionString, required);
                var foreignInventory = new InventoryService(ForeignCharacterId, AccountId);
                var foreignLease = new InventoryLease(Guid.NewGuid(), ForeignCharacterId, foreignInventory, 2);
                var invalidOwner = claimService.Claim(foreignLease);
                Check("claim rejects character from another account",
                    invalidOwner.Status == GrowthCapsuleClaimStatus.InvalidOwner
                    && repository.LoadSummary(AccountId).TotalExp == required);

                var fullInventory = new InventoryService(CharacterId, AccountId);
                FillMainInventory(fullInventory);
                var fullLease = new InventoryLease(Guid.NewGuid(), CharacterId, fullInventory, 3);
                var blocked = claimService.Claim(fullLease);
                Check("inventory failure reports full", blocked.Status == GrowthCapsuleClaimStatus.InventoryFull);
                Check("inventory failure rolls back gage", repository.LoadSummary(AccountId).TotalExp == required);

                CheckV19Migration();

                Console.WriteLine(_failures == 0 ? "GrowthCapsuleSelfTest OK" : $"GrowthCapsuleSelfTest FAIL: {_failures}");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("GrowthCapsuleSelfTest FAILED: " + ex);
                return 1;
            }
            finally
            {
                DeleteDatabase(databasePath);
            }
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES(@aid, 'growth_capsule_selftest');
INSERT INTO accounts(account_id, m_id) VALUES(@otherAid, 'growth_capsule_other');
INSERT INTO characters(character_id, account_id, name, level) VALUES(@cid, @aid, 'growth_capsule_a', 86);
INSERT INTO characters(character_id, account_id, name, level) VALUES(@other, @aid, 'growth_capsule_b', 86);
INSERT INTO characters(character_id, account_id, name, level) VALUES(@foreign, @otherAid, 'growth_capsule_foreign', 86);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@otherAid", OtherAccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@other", OtherCharacterId);
                    command.Parameters.AddWithValue("@foreign", ForeignCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void CheckV19Migration()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(), "growth_capsule_migration_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.BuildConnectionString(databasePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = File.ReadAllText(ServerPaths.SchemaFilePath);
                        command.ExecuteNonQuery();
                        command.CommandText = @"
ALTER TABLE accounts DROP COLUMN growth_capsule_exp;
PRAGMA user_version=19;";
                        command.ExecuteNonQuery();
                    }
                }

                SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name='growth_capsule_exp';";
                        Check("v19 database migrates account capsule column", Convert.ToInt32(command.ExecuteScalar()) == 1);
                        command.CommandText = "PRAGMA user_version;";
                        Check("growth capsule migration advances schema past v19", Convert.ToInt32(command.ExecuteScalar()) >= 20);
                    }
                }
            }
            finally
            {
                DeleteDatabase(databasePath);
            }
        }

        private static void SetGrowthCapsuleExp(string connectionString, uint totalExp)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    GrowthCapsuleProgressRepository.UpdateTotalExpInTransaction(
                        connection, transaction, AccountId, totalExp);
                    transaction.Commit();
                }
            }
        }

        private static int LoadAccountId(string connectionString, int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT account_id FROM characters WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void FillMainInventory(InventoryService inventory)
        {
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                var filler = ItemCore.Create(ItemCore.KindConsumable, 1000 + slot);
                filler.Count = 1;
                inventory.AttachItem(InventoryListType.Main, slot, filler);
            }
        }

        private static void Check(string name, bool success)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {name}");
            if (!success)
                _failures++;
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

    }
}
