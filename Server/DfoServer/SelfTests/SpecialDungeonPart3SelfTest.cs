using DfoServer.Game.Dungeon;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class SpecialDungeonPart3SelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SPECIAL_DUNGEON_PART3 selftest ===");
            var failures = 0;

            TestProtocolBodies(ref failures);
            TestEmptySpecialPassiveObjectItem(ref failures);
            TestAntonPermissionBatch(ref failures);
            TestAntonNormalPvfSequence(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestEmptySpecialPassiveObjectItem(
            ref int failures)
        {
            var parser = typeof(DungeonFile).GetMethod(
                "ParseSpecialPassiveObjectItem",
                BindingFlags.NonPublic | BindingFlags.Static);
            var dungeon = new DungeonFile();
            var passed = parser != null;
            try
            {
                foreach (var data in new string[] { null, string.Empty, "1 2" })
                    parser?.Invoke(null, new object[] { data, dungeon });
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine(
                    $"[INFO] special passive object parser threw: " +
                    $"{ex.InnerException ?? ex}");
                passed = false;
            }

            Check(
                "empty special passive object item rows are ignored",
                passed && dungeon.SpecialPassiveObjectItems.Count == 0,
                ref failures);
        }

        private static void TestProtocolBodies(ref int failures)
        {
            var linked = DungeonNotificationBuilder.BuildLinkedDungeonInfo(
                226,
                2);
            Check(
                "0x0282 uses confirmed int32 dungeon + int32 difficulty",
                BytesEqual(
                    linked,
                    0xE2, 0x00, 0x00, 0x00,
                    0x02, 0x00, 0x00, 0x00),
                ref failures);

            var progress =
                DungeonNotificationBuilder.BuildSequentialDungeonInfo(
                    28,
                    1,
                    0);
            Check(
                "0x025B uses confirmed int32 + byte + int32 body",
                BytesEqual(
                    progress,
                    0x1C, 0x00, 0x00, 0x00,
                    0x01,
                    0x00, 0x00, 0x00, 0x00),
                ref failures);

            var permissionBody = DungeonPermissionBodyBuilder.BuildEntries(
                BuildPermissions((225, 3), (226, 2)));
            Check(
                "0x0005 runtime snapshot reuses init permission layout",
                BytesEqual(
                    permissionBody,
                    0x02, 0x00,
                    0xE1, 0x00, 0x03,
                    0xE2, 0x00, 0x02),
                ref failures);
        }

        private static void TestAntonPermissionBatch(ref int failures)
        {
            const int accountId = 978031;
            const int characterId = 978131;
            const int rollbackCharacterId = 978132;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"anton-permission-batch-{Guid.NewGuid():N}.db");

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
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'anton-permission-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'AntonPermissionMain', 86),
       (@rollbackCid, @aid, 'AntonPermissionRollback', 86);";
                        command.Parameters.AddWithValue("@aid", accountId);
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue(
                            "@rollbackCid",
                            rollbackCharacterId);
                        command.ExecuteNonQuery();
                    }
                }

                var repository = new SqliteCharacterStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var snapshot = repository.ApplyDungeonPermissionBatch(
                    characterId,
                    BuildPermissions((225, 3), (226, 2), (228, 1)),
                    out var changes);
                Check(
                    "Anton permission changes commit as one batch",
                    Format(changes) == "225:3,226:2,228:1"
                        && Format(snapshot) == "225:3,226:2,228:1",
                    ref failures);

                snapshot = repository.ApplyDungeonPermissionBatch(
                    characterId,
                    BuildPermissions((225, 2), (226, 1), (228, 1)),
                    out changes);
                Check(
                    "Anton permission state is monotonic and replay is a no-op",
                    changes.Count == 0
                        && Format(snapshot) == "225:3,226:2,228:1",
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = $@"
CREATE TRIGGER fail_anton_permission_batch
BEFORE INSERT ON character_dungeon_permissions
WHEN NEW.character_id = {rollbackCharacterId}
 AND NEW.dungeon_id = 228
BEGIN
    SELECT RAISE(ABORT, 'injected Anton permission failure');
END;";
                        command.ExecuteNonQuery();
                    }
                }

                var failed = false;
                try
                {
                    repository.ApplyDungeonPermissionBatch(
                        rollbackCharacterId,
                        BuildPermissions((225, 3), (228, 1)),
                        out _);
                }
                catch (SqliteException)
                {
                    failed = true;
                }

                Check(
                    "Anton permission batch rolls back every prior write on failure",
                    failed
                        && repository.LoadDungeonPermissions(
                            rollbackCharacterId).Count == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FAIL] Anton permission batch checks: {ex}");
                failures++;
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void TestAntonNormalPvfSequence(ref int failures)
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(
                    "[SKIP] PVF-backed Anton Normal checks: " +
                    "Script.pvf not found");
                return;
            }

            try
            {
                Check(
                    "Anton Normal main sequence comes from paired WDM entries",
                    AntonNormalConquest.TryGetSequence(
                        225,
                        out var sequence)
                        && sequence.ConfigKey == 28
                        && sequence.Difficulty == 2
                        && string.Join(",", sequence.DungeonIds)
                            == "225,226,228,229,231",
                    ref failures);
                if (sequence == null)
                    return;

                Check(
                    "unpaired Anton auxiliary entry is excluded",
                    !AntonNormalConquest.TryGetSequence(227, out _),
                    ref failures);

                var expectedNext = new[] { 226, 228, 229, 231, 0 };
                var expectedPreview = new[] { 228, 229, 231, 0, 0 };
                var plansValid = true;
                for (var index = 0;
                    index < sequence.DungeonIds.Count;
                    index++)
                {
                    plansValid &= AntonNormalConquest.TryResolveClearPlan(
                        sequence.DungeonIds[index],
                        out var plan)
                        && plan.CurrentIndex == index
                        && plan.NextDungeonId == expectedNext[index]
                        && plan.PreviewDungeonId == expectedPreview[index];
                }
                Check(
                    "each clear advances one entry and previews only one lock",
                    plansValid,
                    ref failures);

                Check(
                    "linked challenge follows the WDM sequence and stops at final",
                    AntonNormalConquest.TryResolveLinkedNext(225, out var next)
                        && next == 226
                        && AntonNormalConquest.TryResolveLinkedNext(
                            229,
                            out next)
                        && next == 231
                        && !AntonNormalConquest.TryResolveLinkedNext(
                            231,
                            out _),
                    ref failures);

                Check(
                    "permission states derive from designated difficulty",
                    AntonNormalConquest.TryResolveUnlockedState(
                        226,
                        sequence.Difficulty,
                        out var unlockedState)
                        && unlockedState == 2
                        && AntonNormalConquest.TryResolveCompletedState(
                            226,
                            sequence.Difficulty,
                            out var completedState)
                        && completedState == 3,
                    ref failures);

                Check(
                    "merely opening the first entry does not restore conquest",
                    !AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions((225, 2)),
                        out _),
                    ref failures);

                Check(
                    "first clear restores progress one and locked preview",
                    AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions((225, 3), (226, 2)),
                        out var firstSync)
                        && firstSync.ProgressIndex == 1
                        && Format(firstSync.PermissionEntries)
                            == "225:3,226:2,228:1",
                    ref failures);

                var fullyOpened = BuildPermissions(
                    (225, 3),
                    (226, 3),
                    (228, 3),
                    (229, 3),
                    (231, 2));
                Check(
                    "persisted later progress wins when an earlier stage is replayed",
                    AntonNormalConquest.TryResolveSyncState(
                        fullyOpened,
                        out var openedSync)
                        && openedSync.ProgressIndex == 4
                        && Format(openedSync.PermissionEntries)
                            == "225:3,226:3,228:3,229:3,231:2",
                    ref failures);

                Check(
                    "final clear restores one-past-end progress five",
                    AntonNormalConquest.TryResolveSyncState(
                        BuildPermissions(
                            (225, 3),
                            (226, 3),
                            (228, 3),
                            (229, 3),
                            (231, 3)),
                        out var finalSync)
                        && finalSync.ProgressIndex
                            == sequence.DungeonIds.Count
                        && BytesEqual(
                            DungeonNotificationBuilder
                                .BuildSequentialDungeonInfo(
                                    finalSync.Sequence.ConfigKey,
                                    finalSync.ProgressIndex,
                                    0),
                            0x1C, 0x00, 0x00, 0x00,
                            0x05,
                            0x00, 0x00, 0x00, 0x00),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Anton Normal PVF checks: {ex}");
                failures++;
            }
        }

        private static List<DungeonPermissionEntrySnapshot> BuildPermissions(
            params (int DungeonId, byte ClearState)[] entries)
        {
            return entries.Select(entry =>
                new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)entry.DungeonId,
                    ClearState = entry.ClearState,
                }).ToList();
        }

        private static string Format(
            IEnumerable<DungeonPermissionEntrySnapshot> entries)
            => string.Join(",", entries.Select(
                entry => $"{entry.DungeonId}:{entry.ClearState}"));

        private static bool BytesEqual(byte[] actual, params byte[] expected)
            => actual != null && actual.SequenceEqual(expected);

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
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

        private static void Check(
            string name,
            bool passed,
            ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
