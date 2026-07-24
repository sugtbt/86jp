using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Accounts
{
    public enum GrowthCapsuleClaimStatus
    {
        Success,
        InsufficientExp,
        InventoryFull,
        InvalidOwner,
    }

    public sealed class GrowthCapsuleClaimResult
    {
        public GrowthCapsuleClaimStatus Status { get; set; }
        public short AssignedSlot { get; set; } = -1;
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public GrowthCapsuleSummary Summary { get; set; }
        public bool Success => Status == GrowthCapsuleClaimStatus.Success;
    }

    public sealed class GrowthCapsuleClaimService
    {
        private readonly string _connectionString;

        public GrowthCapsuleClaimService(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        internal GrowthCapsuleClaimResult Claim(InventoryLease lease)
        {
            var characterId = lease != null ? lease.CharacterId : 0;
            var accountId = lease != null ? lease.AccountId : 0;
            if (characterId <= 0 || accountId <= 0)
            {
                return new GrowthCapsuleClaimResult
                {
                    Status = GrowthCapsuleClaimStatus.InvalidOwner,
                    Summary = GrowthCapsuleDataProvider.Calculate(0),
                };
            }

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!IsCharacterOwnedByAccount(connection, transaction, characterId, accountId))
                    {
                        return new GrowthCapsuleClaimResult
                        {
                            Status = GrowthCapsuleClaimStatus.InvalidOwner,
                            Summary = GrowthCapsuleDataProvider.Calculate(0),
                        };
                    }

                    var totalExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                        connection, transaction, accountId);
                    var summary = GrowthCapsuleDataProvider.Calculate(totalExp);
                    if (totalExp < summary.RequiredExp)
                    {
                        return new GrowthCapsuleClaimResult
                        {
                            Status = GrowthCapsuleClaimStatus.InsufficientExp,
                            Summary = summary,
                        };
                    }

                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            lease,
                            GrowthCapsuleDataProvider.RewardItemId,
                            ItemCreateReason.AdminGrant,
                            GrowthCapsuleDataProvider.RewardItemCount,
                            out var grant))
                    {
                        return new GrowthCapsuleClaimResult
                        {
                            Status = GrowthCapsuleClaimStatus.InventoryFull,
                            Summary = summary,
                        };
                    }

                    GrowthCapsuleProgressRepository.UpdateTotalExpInTransaction(
                        connection, transaction, accountId, 0);
                    transaction.Commit();
                    return new GrowthCapsuleClaimResult
                    {
                        Status = GrowthCapsuleClaimStatus.Success,
                        AssignedSlot = grant.SlotIndex,
                        ItemId = GrowthCapsuleDataProvider.RewardItemId,
                        ItemCount = GrowthCapsuleDataProvider.RewardItemCount,
                        Summary = GrowthCapsuleDataProvider.Calculate(0),
                    };
                }
            }
        }

        private static bool IsCharacterOwnedByAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM characters
WHERE character_id=@cid AND account_id=@aid AND delete_flag=0;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                return command.ExecuteScalar() != null;
            }
        }
    }
}
