using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    internal static class ExpertContractSkillReconciler
    {
        internal static bool ReconcileExpiredContractSkills(
            SqliteCharacterProgressRepository repository,
            int characterId,
            int accountId)
        {
            if (repository == null || characterId <= 0 || accountId <= 0)
                return false;

            // Load PVF-backed premium metadata before taking the SQLite transaction.
            var premiumCatalog = Game.Premium.PremiumCatalog.Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using (var connection = new SqliteConnection(repository.ConnectionString))
            {
                connection.Open();
                var previewExpiredPremiumTypes = FindExpiredOverSkillPremiumTypes(
                    connection,
                    null,
                    accountId,
                    now,
                    premiumCatalog,
                    out var previewHasActiveReplacement);
                if (previewHasActiveReplacement || previewExpiredPremiumTypes.Count == 0)
                    return false;

                // Avoid parsing character/skill PVF data while holding the account-wide
                // write transaction. State is re-read below before any mutation.
                CharacterSkillProfile.Warmup();
                WarmSkillDataForAccount(connection, accountId);

                using (var transaction = connection.BeginTransaction())
                {
                    var expiredPremiumTypes = FindExpiredOverSkillPremiumTypes(
                        connection,
                        transaction,
                        accountId,
                        now,
                        premiumCatalog,
                        out var hasActiveReplacement);
                    if (hasActiveReplacement || expiredPremiumTypes.Count == 0)
                    {
                        transaction.Commit();
                        return false;
                    }

                    var characters = LoadAccountCharacters(connection, transaction, accountId);
                    if (!characters.Exists(x => x.CharacterId == characterId))
                    {
                        transaction.Commit();
                        return false;
                    }

                    var changedCharacters = 0;
                    var changedEntries = 0;
                    foreach (var character in characters)
                    {
                        Characters.CharacterStatComputer.DecodeGrowType(
                            character.RawGrowType,
                            out var growType,
                            out var secondGrowType);
                        var skills = repository.LoadSkills(
                            connection,
                            transaction,
                            character.CharacterId);
                        var baseline = SkillPointLedger.BuildFreeBaseline(
                            character.Job,
                            growType,
                            secondGrowType);
                        var characterChangedEntries = ReconcileCharacterSkills(
                            skills,
                            character.Job,
                            character.Level,
                            growType,
                            secondGrowType,
                            baseline);
                        if (characterChangedEntries == 0)
                            continue;

                        repository.SaveSkillProgress(
                            connection,
                            transaction,
                            character.CharacterId,
                            skills);

                        changedCharacters++;
                        changedEntries += characterChangedEntries;
                    }

                    var deletedPremiumRows = DeleteExpiredPremiumRows(
                        connection,
                        transaction,
                        accountId,
                        expiredPremiumTypes,
                        now);
                    transaction.Commit();

                    FileLogger.Log(
                        $"[ExpertContractSkillReconciler] reconciled aid={accountId} " +
                        $"characters={characters.Count} changedCharacters={changedCharacters} " +
                        $"entries={changedEntries} premiumRows={deletedPremiumRows}");
                    return true;
                }
            }
        }

        private static int ReconcileCharacterSkills(
            SkillInfoSnapshot skills,
            byte job,
            byte level,
            int growType,
            int secondGrowType,
            IReadOnlyDictionary<ushort, byte> baseline)
        {
            var changedEntries = 0;
            foreach (var page in skills.Pages)
            {
                for (var entryIndex = page.Entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    var entry = page.Entries[entryIndex];
                    var skill = SkillDataProvider.GetSkill(job, entry.SkillId);
                    if (skill == null || skill.IsFixedLevelSkill)
                        continue;

                    // A skill unavailable to this advancement was not created by the
                    // expert contract, so leave quest/task/free-grant ownership intact.
                    if (skill.GetMaxLevelFor(growType, secondGrowType) <= 0)
                        continue;

                    var maximum = skill.GetMaxLearnableLevel(level, growType, secondGrowType);
                    if (baseline.TryGetValue(entry.SkillId, out var freeLevel)
                        && freeLevel > maximum)
                    {
                        maximum = freeLevel;
                    }

                    if (entry.Level <= maximum)
                        continue;

                    if (maximum <= 0)
                        page.Entries.RemoveAt(entryIndex);
                    else
                        entry.Level = (byte)Math.Min(maximum, byte.MaxValue);
                    changedEntries++;
                }
            }

            return changedEntries;
        }

        private static void WarmSkillDataForAccount(
            SqliteConnection connection,
            int accountId)
        {
            var skillKeys = new List<(byte Job, int SkillId)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT DISTINCT c.job, s.skill_id
FROM characters c
JOIN character_skills s ON s.character_id=c.character_id
WHERE c.account_id=@aid
  AND c.delete_flag=0;";
                command.Parameters.AddWithValue("@aid", accountId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        skillKeys.Add((
                            (byte)Math.Max(0, Math.Min(byte.MaxValue, reader.GetInt32(0))),
                            reader.GetInt32(1)));
                    }
                }
            }

            foreach (var skillKey in skillKeys)
                SkillDataProvider.GetSkill(skillKey.Job, skillKey.SkillId);
        }

        private static HashSet<int> FindExpiredOverSkillPremiumTypes(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            long now,
            Game.Premium.PremiumCatalog premiumCatalog,
            out bool hasActiveReplacement)
        {
            var expiredPremiumTypes = new HashSet<int>();
            hasActiveReplacement = false;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT premium_type, end_time
FROM account_premiums
WHERE account_id=@aid;";
                command.Parameters.AddWithValue("@aid", accountId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var premiumType = reader.GetInt32(0);
                        var effects = premiumCatalog.GetEffects(premiumType);
                        if (effects == null || effects.OverSkillLevel <= 0)
                            continue;

                        if (reader.GetInt64(1) > now)
                        {
                            hasActiveReplacement = true;
                            expiredPremiumTypes.Clear();
                            return expiredPremiumTypes;
                        }

                        expiredPremiumTypes.Add(premiumType);
                    }
                }
            }

            return expiredPremiumTypes;
        }

        private static List<AccountCharacter> LoadAccountCharacters(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            var characters = new List<AccountCharacter>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT character_id, job, level, grow_type
FROM characters
WHERE account_id=@aid
  AND delete_flag=0
ORDER BY character_id;";
                command.Parameters.AddWithValue("@aid", accountId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        characters.Add(new AccountCharacter
                        {
                            CharacterId = reader.GetInt32(0),
                            Job = (byte)Math.Max(0, Math.Min(byte.MaxValue, reader.GetInt32(1))),
                            Level = (byte)Math.Max(0, Math.Min(byte.MaxValue, reader.GetInt32(2))),
                            RawGrowType = (byte)Math.Max(0, Math.Min(byte.MaxValue, reader.GetInt32(3))),
                        });
                    }
                }
            }

            return characters;
        }

        private static int DeleteExpiredPremiumRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            IEnumerable<int> premiumTypes,
            long now)
        {
            var deletedRows = 0;
            foreach (var premiumType in premiumTypes)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
DELETE FROM account_premiums
WHERE account_id=@aid
  AND premium_type=@type
  AND end_time<=@now;";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@type", premiumType);
                    command.Parameters.AddWithValue("@now", now);
                    deletedRows += command.ExecuteNonQuery();
                }
            }

            return deletedRows;
        }

        private sealed class AccountCharacter
        {
            public int CharacterId { get; set; }

            public byte Job { get; set; }

            public byte Level { get; set; }

            public byte RawGrowType { get; set; }
        }
    }
}
