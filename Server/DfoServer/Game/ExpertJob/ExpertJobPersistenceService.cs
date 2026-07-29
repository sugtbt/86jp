using System;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobPersistenceService
    {
        private readonly string _connectionString;

        internal ExpertJobPersistenceService(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        internal bool Save(
            InventoryLease requesterLease,
            InventoryLease ownerLease,
            Func<SqliteConnection, SqliteTransaction, bool> saveExpertJobState)
        {
            if (requesterLease == null
                || ownerLease == null
                || saveExpertJobState == null)
                return false;

            try
            {
                if (ReferenceEquals(requesterLease, ownerLease))
                {
                    lock (requesterLease.SyncRoot)
                        return SaveLocked(requesterLease, null, saveExpertJobState);
                }

                var first = requesterLease.CharacterId < ownerLease.CharacterId
                    ? requesterLease
                    : ownerLease;
                var second = ReferenceEquals(first, requesterLease)
                    ? ownerLease
                    : requesterLease;
                lock (first.SyncRoot)
                lock (second.SyncRoot)
                    return SaveLocked(requesterLease, ownerLease, saveExpertJobState);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobPersistence] save failed requester={requesterLease.CharacterId} " +
                    $"owner={ownerLease.CharacterId}: {ex.Message}");
                return false;
            }
        }

        private bool SaveLocked(
            InventoryLease requesterLease,
            InventoryLease ownerLease,
            Func<SqliteConnection, SqliteTransaction, bool> saveExpertJobState)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            requesterLease)
                        || (ownerLease != null
                            && !InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                ownerLease))
                        || !saveExpertJobState(connection, transaction))
                    {
                        return false;
                    }

                    transaction.Commit();
                }
            }

            requesterLease.Inventory.ClearDirtyState();
            ownerLease?.Inventory.ClearDirtyState();
            return true;
        }
    }
}
