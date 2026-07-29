using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.ExpertJob
{
    public interface IExpertJobStateRepository
    {
        ExpertJobState Load(int characterId, int expertJobType);
    }

    public interface IDisjointMachineStateRepository
    {
        DisjointMachineState Resolve(int characterId);

        bool SaveInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DisjointMachineState state,
            int experienceGain);
    }

    public sealed class SqliteExpertJobStateRepository
        : IExpertJobStateRepository, IDisjointMachineStateRepository
    {
        private readonly string _connectionString;

        public SqliteExpertJobStateRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public ExpertJobState Load(int characterId, int expertJobType)
        {
            var state = CreateInitialState(expertJobType);
            if (characterId <= 0 || expertJobType <= 0)
                return state;

            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT giveup_count, disjoint_machine_grade, disjoint_machine_endurance
FROM character_expert_job
WHERE character_id=@cid;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                state.GiveUpCount = Math.Max(0, reader.GetInt32(0));
                                if (expertJobType == ExpertJobStateCodec.DisjointerType)
                                {
                                    var grade = reader.GetInt32(1);
                                    var endurance = reader.GetInt32(2);
                                    if (IsValidDisjointMachineState(grade, endurance))
                                    {
                                        state.DisjointMachine.MachineGrade = (byte)grade;
                                        state.DisjointMachine.Endurance = endurance;
                                    }
                                }
                            }
                        }
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT recipe_id
FROM character_expert_job_recipes
WHERE character_id=@cid
ORDER BY recipe_id;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                                state.LearnedRecipeIds.Add(reader.GetInt32(0));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobState] load failed cid={characterId}: {ex.Message}");
            }

            return state;
        }

        public DisjointMachineState Resolve(int characterId)
            => Load(characterId, ExpertJobStateCodec.DisjointerType).DisjointMachine;

        public bool SaveInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DisjointMachineState state,
            int experienceGain)
        {
            if (characterId <= 0
                || state == null
                || !IsValidDisjointMachineState(state.MachineGrade, state.Endurance)
                || connection == null
                || transaction == null)
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, disjoint_machine_grade, disjoint_machine_endurance, updated_at)
VALUES (@cid, @grade, @endurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    disjoint_machine_grade=excluded.disjoint_machine_grade,
    disjoint_machine_endurance=excluded.disjoint_machine_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@grade", state.MachineGrade);
                command.Parameters.AddWithValue("@endurance", state.Endurance);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            var normalizedExperienceGain = Math.Max(0, experienceGain);
            if (normalizedExperienceGain == 0)
                return true;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_subtype0_fields
SET expert_job_exp = MIN(4294967295, expert_job_exp + @exp)
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@exp", normalizedExperienceGain);
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal static void InitializeInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int expertJobType)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            var initialGrade = 0;
            var initialEndurance = 0;
            if (expertJobType == ExpertJobStateCodec.DisjointerType)
            {
                initialGrade = 1;
                initialEndurance = DisjointMachineConfigProvider.InitialEndurance;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, giveup_count,
    disjoint_machine_grade, disjoint_machine_endurance, updated_at)
VALUES (@cid, 0, @grade, @endurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    giveup_count=0,
    disjoint_machine_grade=excluded.disjoint_machine_grade,
    disjoint_machine_endurance=excluded.disjoint_machine_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@grade", initialGrade);
                command.Parameters.AddWithValue("@endurance", initialEndurance);
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_expert_job_recipes
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static ExpertJobState CreateInitialState(int expertJobType)
        {
            var state = new ExpertJobState();
            if (expertJobType == ExpertJobStateCodec.DisjointerType)
            {
                state.DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = DisjointMachineConfigProvider.InitialEndurance,
                };
            }

            return state;
        }

        private static bool IsValidDisjointMachineState(int grade, int endurance)
            => grade > 0
                && grade <= DisjointMachineConfigProvider.Config.RepairRules.Count
                && endurance >= 0;
    }
}
