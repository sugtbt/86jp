using DfoServer.Game.CharacterData;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Settings
{
    internal sealed class CharacterVisibilitySettingsPersistence
    {
        private readonly string _connectionString;

        public CharacterVisibilitySettingsPersistence(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public void Save(int accountId, int characterId, byte[] mainGameOption, byte userStateBits)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    AccountSettingsRepository.SaveMainOption(
                        connection,
                        transaction,
                        accountId,
                        mainGameOption);
                    SqliteSubtype0FieldsRepository.SaveUserStateBits(
                        connection,
                        transaction,
                        characterId,
                        userStateBits);
                    transaction.Commit();
                }
            }
        }
    }
}
