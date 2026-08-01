using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestChainAvailabilitySelfTest
    {
        private const ushort FirstQuestId = 101;
        private const ushort SecondQuestId = 1776;
        private const ushort ThirdQuestId = 1777;
        private const int AccountId = 986027;
        private const int CharacterId = 986127;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_CHAIN_AVAILABILITY selftest ===");

            var failures = 0;
            var noneCleared = BuildQuestIds();
            Check("second quest is unavailable before its prerequisite",
                !noneCleared.Contains(SecondQuestId),
                ref failures);

            var firstCleared = BuildQuestIds(FirstQuestId);
            Check("clearing quest 101 exposes quest 1776",
                firstCleared.Contains(SecondQuestId),
                ref failures);
            Check("quest 1777 remains unavailable before quest 1776 is cleared",
                !firstCleared.Contains(ThirdQuestId),
                ref failures);

            var secondCleared = BuildQuestIds(FirstQuestId, SecondQuestId);
            Check("clearing quest 1776 exposes quest 1777",
                secondCleared.Contains(ThirdQuestId),
                ref failures);

            CheckCompletionRefresh(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckCompletionRefresh(ref int failures)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "quest-chain-availability.db");
            DeleteDatabase(databasePath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            SeedCharacter(databasePath);
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = FirstQuestId,
                        TriggerValue = 0,
                    },
                });

            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                new InventoryService(CharacterId, AccountId));
            try
            {
                var sender = new RecordingSender();
                var manager = new QuestManager(sender, connectionString);
                manager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(FirstQuestId),
                        sessionId)
                    .GetAwaiter()
                    .GetResult();

                Check("quest finish ACK is emitted first",
                    sender.Calls.Count > 0
                    && sender.Calls[0] == "ACK:0022"
                    && sender.LastAckBody != null
                    && sender.LastAckBody.Length > 0
                    && sender.LastAckBody[0] == 1,
                    ref failures);
                Check("quest finish does not rebuild the active list with 0x023F",
                    !sender.Calls.Contains("NOTI:023F"),
                    ref failures);
                Check("quest finish refreshes acceptable quests with 0x0015",
                    sender.Calls.Count > 1
                    && sender.Calls[sender.Calls.Count - 1] == "NOTI:0015"
                    && ParseQuestIds(sender.LastAcceptableQuestBody).Contains(SecondQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        private static HashSet<ushort> BuildQuestIds(params ushort[] clearedQuestIds)
        {
            var clearedFlags = new Dictionary<int, int>();
            foreach (var questId in clearedQuestIds)
                clearedFlags[questId] = 1;

            var body = QuestListBodyBuilder.BuildBody(
                level: 86,
                job: 0,
                growType: 0,
                clearedFlags);
            if (body == null || body.Length < 3)
                throw new InvalidOperationException("Quest list body is truncated.");

            return ParseQuestIds(body);
        }

        private static HashSet<ushort> ParseQuestIds(byte[] body)
        {
            if (body == null || body.Length < 3)
                throw new InvalidOperationException("Quest list body is truncated.");

            var count = BitConverter.ToUInt16(body, 1);
            if (body.Length != 3 + count * 2)
                throw new InvalidOperationException("Quest list body count does not match its payload length.");

            var result = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
                result.Add(BitConverter.ToUInt16(body, 3 + index * 2));
            return result;
        }

        private static byte[] BuildWireFinishBody(ushort questId)
        {
            var body = new byte[10];
            BitConverter.GetBytes((ushort)0x0022).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(body, 6);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 8);
            return body;
        }

        private static void SeedCharacter(string databasePath)
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
VALUES (@aid, 'quest-chain-selftest', '');";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.ExecuteNonQuery();
                }
            }

            var repository = new SqliteCharacterRepository(
                databasePath,
                ServerPaths.SchemaFilePath);
            repository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-chain-selftest"),
                Job = 0,
                GrowType = 0,
                Level = 86,
            });
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal List<string> Calls { get; } = new List<string>();
            internal byte[] LastAckBody { get; private set; }
            internal byte[] LastAcceptableQuestBody { get; private set; }

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = QuestChainAvailabilitySelfTest.CharacterId,
                Job = 0,
                GrowType = 0,
                Level = 86,
            };

            public int CharacterId => QuestChainAvailabilitySelfTest.CharacterId;
            public int AccountId => QuestChainAvailabilitySelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Calls.Add($"NOTI:{notiType:X4}");
                if (notiType == 0x0015)
                    LastAcceptableQuestBody = body;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Calls.Add($"ACK:{cmdType:X4}");
                LastAckBody = body;
                return Task.CompletedTask;
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
