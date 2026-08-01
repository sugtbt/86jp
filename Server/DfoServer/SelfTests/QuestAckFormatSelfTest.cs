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

namespace DfoServer.SelfTests
{
    // 任务四个命令(接取/放弃/触发器/完成)的应答包字节格式冻结测试。
    // 固定的角色/任务/背包状态下, 应答包的每个字节都应该逐次运行完全一致。
    // 期望值是在当前实现上采集的实际输出 -- 之后任何改动导致字节变化,
    // 这里会第一时间报出差异(打印期望/实际的完整十六进制)。
    public static class QuestAckFormatSelfTest
    {
        private const int CharacterId = 136001;
        private const int AccountId = 136001;

        // 使用固定任务样本:
        // 2042(交信任务, 完成发放事件道具 10089292), 前置 2041。
        private const ushort LetterQuestId = 2042;
        private const ushort PrerequisiteQuestId = 2041;
        private const ushort ThievesCityQuestId = 2066;
        private const int ThievesCityQuestItemId = 10089306;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_ACK_FORMAT selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-ack-format.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-ack-format-test"),
                Job = 0,
                GrowType = 0,
                Level = 49,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var failures = 0;
            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(
                sessionId,
                inventory);

            // --- 接取: 前置未完成 -> 失败 ACK ---
            var acceptFail = QuestAckBuilder.BuildAccept(questService.HandleAcceptQuest(CharacterId, BuildAcceptBody(LetterQuestId), AccountId));
            CheckBytes("accept fails while prerequisite missing",
                "00-15", acceptFail, ref failures);

            // --- 接取: 前置补齐 -> 成功 ACK (含初始触发器 + 事件道具发放) ---
            MarkQuestCleared(connStr, PrerequisiteQuestId);
            var acceptOk = QuestAckBuilder.BuildAccept(questService.HandleAcceptQuest(CharacterId, BuildAcceptBody(LetterQuestId), AccountId));
            CheckBytes("accept success ack bytes",
                "01-FA-07-01-00-00-00-01-B1-00-4C-F3-99-00-01-00-00-00", acceptOk, ref failures);

            // --- 重复接取 -> 失败 ACK ---
            var acceptDup = QuestAckBuilder.BuildAccept(questService.HandleAcceptQuest(CharacterId, BuildAcceptBody(LetterQuestId), AccountId));
            CheckBytes("duplicate accept rejected",
                "00-12", acceptDup, ref failures);

            // --- 触发器: 对无触发器任务设置 -> 按现实现返回 ---
            var trigger = QuestAckBuilder.BuildSetTrigger(questService.HandleSetTrigger(CharacterId, BuildSetTriggerBody(LetterQuestId, 0, false)));
            CheckBytes("set trigger ack bytes",
                "01-FA-07-00-00-00-00", trigger, ref failures);

            // --- 完成: 触发器归零 -> 成功 ACK (经验/金币/消耗/奖励段) ---
            var finishOk = QuestAckBuilder.BuildFinish(
                QuestSelfTestCommandAdapter.HandleFinish(
                    questService,
                    CharacterId,
                    BuildFinishBody(LetterQuestId)));
            CheckBytes("finish success ack bytes",
                "01-FA-07-00-AB-B4-00-00-A8-0C-00-00-00-00-01-00-00-00-00-00-00-A8-0C-00-00-00-00-00-00-00-00-00-00",
                finishOk,
                ref failures);

            // --- 完成: 任务已完成且不在身上, 再次请求被拒绝(不能重复领奖励) ---
            var finishAgain = QuestAckBuilder.BuildFinish(
                QuestSelfTestCommandAdapter.HandleFinish(
                    questService,
                    CharacterId,
                    BuildFinishBody(LetterQuestId)));
            CheckBytes("finish repeated rejected",
                "00-16", finishAgain, ref failures);

            // --- 放弃: 重新接取后放弃 -> 成功 ACK ---
            DeleteQuestCleared(connStr, LetterQuestId);
            questService.HandleAcceptQuest(CharacterId, BuildAcceptBody(LetterQuestId), AccountId);
            Check(
                "depend-give quest item exists before giveup",
                inventory.CountMainItem(10089292) > 0,
                ref failures);
            var giveupResult = questService.HandleGiveupQuest(
                CharacterId,
                BuildGiveupBody(LetterQuestId));
            var giveup = QuestAckBuilder.BuildGiveup(giveupResult);
            CheckBytes("giveup success ack bytes",
                "01-FA-07", giveup, ref failures);
            Check(
                "depend-give quest item is reclaimed through inventory mutation",
                inventory.CountMainItem(10089292) == 0
                    && giveupResult.InventoryChanges.HasChanges,
                ref failures);

            // --- 放弃: 不在身上 -> 失败 ACK ---
            var giveupFail = QuestAckBuilder.BuildGiveup(questService.HandleGiveupQuest(CharacterId, BuildGiveupBody(LetterQuestId)));
            CheckBytes("giveup missing quest rejected",
                "00-13", giveupFail, ref failures);

            VerifyGiveupRecoveryPolicy(ref failures);
            VerifySeekingGiveupProjection(
                connStr,
                sessionId,
                inventory,
                ref failures);

            InventoryContext.Unregister(sessionId, CharacterId);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildAcceptBody(ushort questId)
            => BitConverter.GetBytes(questId);

        private static byte[] BuildGiveupBody(ushort questId)
            => BitConverter.GetBytes(questId);

        private static byte[] BuildSetTriggerBody(ushort questId, byte triggerType, bool increment)
        {
            var body = new byte[4];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            body[2] = triggerType;
            body[3] = (byte)(increment ? 1 : 0);
            return body;
        }

        private static byte[] BuildFinishBody(ushort questId) =>
            QuestSelfTestCommandAdapter.BuildFinishBody(questId);

        private static void SeedAccount(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, @mid, '');";
            cmd.Parameters.AddWithValue("@aid", AccountId);
            cmd.Parameters.AddWithValue("@mid", "quest-ack-format");
            cmd.ExecuteNonQuery();
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO character_invisible_falgs (character_id, slot_index, flag_value)
VALUES (@cid, @slot, 1)
ON CONFLICT(character_id, slot_index) DO UPDATE SET flag_value = 1;";
            cmd.Parameters.AddWithValue("@cid", CharacterId);
            cmd.Parameters.AddWithValue("@slot", questId);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteQuestCleared(string connStr, int questId)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM character_invisible_falgs WHERE character_id=@cid AND slot_index=@slot;";
            cmd.Parameters.AddWithValue("@cid", CharacterId);
            cmd.Parameters.AddWithValue("@slot", questId);
            cmd.ExecuteNonQuery();
        }

        private static void VerifyGiveupRecoveryPolicy(ref int failures)
        {
            const ushort abandonedQuestId = 1;
            const ushort sharedQuestA = 2;
            const ushort sharedQuestB = 3;
            const int questItemId = 500001;
            const int ordinaryMaterialId = 500002;
            var active = new List<ActiveQuest>
            {
                new ActiveQuest { QuestId = abandonedQuestId },
                new ActiveQuest { QuestId = sharedQuestA },
                new ActiveQuest { QuestId = sharedQuestB },
            };
            var events = new Dictionary<ushort, IReadOnlyCollection<GameWorld.QuestRewardItem>>
            {
                [abandonedQuestId] = new[]
                {
                    new GameWorld.QuestRewardItem { ItemId = questItemId, Count = 4 },
                    new GameWorld.QuestRewardItem { ItemId = ordinaryMaterialId, Count = 3 },
                },
                [sharedQuestA] = new[]
                {
                    new GameWorld.QuestRewardItem { ItemId = questItemId, Count = 10 },
                },
            };
            var seeking = new Dictionary<ushort, IReadOnlyCollection<GameWorld.QuestRewardItem>>
            {
                [abandonedQuestId] = new[]
                {
                    new GameWorld.QuestRewardItem { ItemId = questItemId, Count = 20 },
                    new GameWorld.QuestRewardItem { ItemId = ordinaryMaterialId, Count = 8 },
                },
                [sharedQuestA] = new[]
                {
                    new GameWorld.QuestRewardItem { ItemId = questItemId, Count = 7 },
                },
                [sharedQuestB] = new[]
                {
                    new GameWorld.QuestRewardItem { ItemId = questItemId, Count = 12 },
                },
            };
            var plan = QuestGiveupItemRecoveryPolicy.Build(
                active,
                abandonedQuestId,
                questId => events.TryGetValue(questId, out var values)
                    ? values
                    : Array.Empty<GameWorld.QuestRewardItem>(),
                questId => seeking.TryGetValue(questId, out var values)
                    ? values
                    : Array.Empty<GameWorld.QuestRewardItem>(),
                itemId => itemId == questItemId);
            Check(
                "giveup policy keeps the largest shared quest requirement",
                plan.Count == 1
                    && plan[0].ItemId == questItemId
                    && plan[0].RetainCount == 12,
                ref failures);
            Check(
                "giveup policy excludes ordinary materials",
                !ContainsPlanItem(plan, ordinaryMaterialId),
                ref failures);
        }

        private static void VerifySeekingGiveupProjection(
            string connectionString,
            Guid sessionId,
            InventoryService inventory,
            ref int failures)
        {
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = ThievesCityQuestId,
                        TriggerValue = 0,
                    },
                });
            var granted = InventoryRewardGrantService.TryCreateAndInsert(
                inventory,
                ThievesCityQuestItemId,
                ItemCreateReason.QuestReward,
                24,
                out var grant);
            Check(
                "seeking quest item fixture is inserted",
                granted
                    && grant.Success
                    && inventory.CountMainItem(ThievesCityQuestItemId) == 24,
                ref failures);

            var sender = new RecordingSender(CharacterId, AccountId);
            var manager = new QuestManager(sender, connectionString);
            var body = new byte[4];
            BitConverter.GetBytes(ThievesCityQuestId).CopyTo(body, 2);
            manager.HandleGiveupQuestAsync(0x0020, body, sessionId)
                .GetAwaiter()
                .GetResult();

            Check(
                "seeking quest items are reclaimed on giveup",
                inventory.CountMainItem(ThievesCityQuestItemId) == 0,
                ref failures);
            Check(
                "giveup projects item slots before command ack",
                sender.Events.Count == 2
                    && sender.Events[0] == "noti:000E"
                    && sender.Events[1] == "cmd:0020",
                ref failures);
            CheckBytes(
                "giveup manager preserves success ack bytes",
                "01-12-08",
                sender.LastCommandBody,
                ref failures);
        }

        private static bool ContainsPlanItem(
            IReadOnlyList<QuestGiveupItemRecoveryEntry> plan,
            int itemId)
        {
            if (plan == null)
                return false;
            foreach (var entry in plan)
            {
                if (entry.ItemId == itemId)
                    return true;
            }
            return false;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static void CheckBytes(string name, string expectedHex, byte[] actual, ref int failures)
        {
            var actualHex = actual == null ? "<null>" : BitConverter.ToString(actual);
            var ok = actualHex == expectedHex;
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
            {
                Console.WriteLine($"    expected: {expectedHex}");
                Console.WriteLine($"    actual:   {actualHex}");
                failures++;
            }
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal RecordingSender(int characterId, int accountId)
            {
                CharacterId = characterId;
                AccountId = accountId;
                Player.CharacterId = characterId;
            }

            public List<string> Events { get; } = new List<string>();
            public byte[] LastCommandBody { get; private set; }
            public PlayerContext Player { get; } = new PlayerContext();
            public int CharacterId { get; }
            public int AccountId { get; }

            public Task SendPacketAsync(byte[] rawPacket)
                => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Events.Add($"noti:{notiType:X4}");
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Events.Add($"cmd:{cmdType:X4}");
                LastCommandBody = body;
                return Task.CompletedTask;
            }
        }
    }
}
