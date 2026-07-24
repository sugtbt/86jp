using System;
using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryGoldCarryLimitLoader
    {
        internal static int Load(int characterId)
        {
            if (characterId <= 0)
                return int.MaxValue;

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var limit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                            connection,
                            transaction,
                            characterId);
                        transaction.Commit();
                        return limit <= 0 ? int.MaxValue : limit;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryGoldCarryLimit] load failed cid={characterId}: {ex.Message}");
                return int.MaxValue;
            }
        }
    }
}
