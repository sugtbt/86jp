using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
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
            var finishOk = QuestAckBuilder.BuildFinish(questService.HandleFinishQuest(CharacterId, BuildFinishBody(LetterQuestId)));
            CheckBytes("finish success ack bytes",
                "01-FA-07-00-AB-B4-00-00-A8-0C-00-00-00-00-01-00-00-00-00-00-00-A8-0C-00-00-00-00-00-00-00-00-00-00",
                finishOk,
                ref failures);

            // --- 完成: 任务已完成且不在身上, 再次请求被拒绝(不能重复领奖励) ---
            var finishAgain = QuestAckBuilder.BuildFinish(questService.HandleFinishQuest(CharacterId, BuildFinishBody(LetterQuestId)));
            CheckBytes("finish repeated rejected",
                "00-16", finishAgain, ref failures);

            // --- 放弃: 重新接取后放弃 -> 成功 ACK ---
            DeleteQuestCleared(connStr, LetterQuestId);
            questService.HandleAcceptQuest(CharacterId, BuildAcceptBody(LetterQuestId), AccountId);
            var giveup = QuestAckBuilder.BuildGiveup(questService.HandleGiveupQuest(CharacterId, BuildGiveupBody(LetterQuestId)));
            CheckBytes("giveup success ack bytes",
                "01-FA-07", giveup, ref failures);

            // --- 放弃: 不在身上 -> 失败 ACK ---
            var giveupFail = QuestAckBuilder.BuildGiveup(questService.HandleGiveupQuest(CharacterId, BuildGiveupBody(LetterQuestId)));
            CheckBytes("giveup missing quest rejected",
                "00-13", giveupFail, ref failures);

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

        private static byte[] BuildFinishBody(ushort questId)
        {
            var body = new byte[6];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 2); // 无奖励选择
            BitConverter.GetBytes((ushort)1).CopyTo(body, 4);       // multiplier=1
            return body;
        }

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
    }
}
