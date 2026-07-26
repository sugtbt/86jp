using System;
using System.IO;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestTriggerCountSelfTest
    {
        private const int CharacterId = 284001;
        private const int AccountId = 284001;
        private const ushort RescueSilmaQuestId = 1791;
        private const ushort AnnoyingAntQuestId = 1821;
        private const ushort SadBellQuestId = 1835;
        private const ushort SurvivorQuestId = 1836;
        private const ushort HelpVoiceQuestId = 2021;
        private const ushort SeekAndMeetQuestId = 2043;
        private const ushort DragonObstacleQuestId = 20722;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_TRIGGER_COUNT selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-trigger-count.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-trigger-count-test"),
                Job = 0,
                GrowType = 0,
                Level = 50,
            });

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            var questService = new QuestService(connStr);
            var failures = 0;

            Check("1791 hunt-enemy single target starts at 1",
                GameWorld.QuestData.GetInitTrigger(RescueSilmaQuestId) == 1,
                ref failures);
            Check("2021 hunt-enemy single target starts at 1",
                GameWorld.QuestData.GetInitTrigger(HelpVoiceQuestId) == 1,
                ref failures);
            Check("1836 hunt-enemy two targets pack both channels",
                GameWorld.QuestData.GetInitTrigger(SurvivorQuestId) == 513,
                ref failures);
            Check("1821 hunt-monster keeps four-field packing",
                GameWorld.QuestData.GetInitTrigger(AnnoyingAntQuestId) == 517,
                ref failures);

            var dragonTargets =
                GameWorld.QuestData.GetHuntMonsterTargets(
                    DragonObstacleQuestId);
            Check("20722 parses dungeon, minimum difficulty and monster",
                dragonTargets.Count == 1
                    && dragonTargets[0].DungeonId == 3536
                    && dragonTargets[0].MinimumDifficulty == 2
                    && dragonTargets[0].MonsterCode == 100003
                    && dragonTargets[0].RequiredCount == 3,
                ref failures);

            Check("hunt-enemy is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(SurvivorQuestId).Count == 0,
                ref failures);
            Check("hunt-monster is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(AnnoyingAntQuestId).Count == 0,
                ref failures);
            Check("use-item quest is not treated as a seeking item quest",
                GameWorld.QuestData.GetSeekingConsumeItems(SadBellQuestId).Count == 0,
                ref failures);
            Check("seek-and-meet npc quest still exposes its item requirement",
                GameWorld.QuestData.GetSeekingConsumeItems(SeekAndMeetQuestId).Count == 1,
                ref failures);

            MarkQuestCleared(connStr, SadBellQuestId);
            var acceptSurvivor = questService.HandleAcceptQuest(CharacterId,
                BuildQuestBody(SurvivorQuestId), AccountId);
            Check("accepting 1836 succeeds", IsSuccessAck(acceptSurvivor), ref failures);
            Check("accepting 1836 stores packed hunt-enemy counts",
                TryReadAcceptTrigger(acceptSurvivor, out var acceptTrigger) && acceptTrigger == 513,
                ref failures);

            QuestService.SaveActiveQuests(
                connStr,
                CharacterId,
                new System.Collections.Generic.List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = DragonObstacleQuestId,
                        TriggerValue = 3,
                    },
                });
            var belowDifficulty = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 1,
                monsterCode: 100003);
            Check("20722 ignores kills below required difficulty",
                belowDifficulty.Count == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 3,
                ref failures);

            var firstKill = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 2,
                monsterCode: 100003);
            Check("20722 matching kill decrements one trigger",
                firstKill.Count == 1
                    && firstKill[0].PreviousTriggerValue == 3
                    && firstKill[0].TriggerValue == 2
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 2,
                ref failures);

            var wrongMonster = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100004);
            Check("20722 ignores another monster",
                wrongMonster.Count == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 2,
                ref failures);

            questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100003);
            var finalKill = questService.SyncHuntMonsterQuestProgress(
                CharacterId,
                dungeonId: 3536,
                difficulty: 4,
                monsterCode: 100003);
            Check("20722 completes after three qualified kills",
                finalKill.Count == 1
                    && finalKill[0].PreviousTriggerValue == 1
                    && finalKill[0].TriggerValue == 0
                    && LoadTrigger(connStr, DragonObstacleQuestId) == 0,
                ref failures);

            var run = new DungeonRun(3536, 2);
            run.MarkServerDrivenHuntMonsterTrigger(DragonObstacleQuestId);
            Check("server-driven hunt trigger consumes one client echo",
                run.TryConsumeServerDrivenHuntMonsterTrigger(
                    DragonObstacleQuestId)
                    && !run.TryConsumeServerDrivenHuntMonsterTrigger(
                        DragonObstacleQuestId),
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildQuestBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static bool IsSuccessAck(QuestAcceptResult result)
        {
            return result != null && result.Success;
        }

        private static bool TryReadAcceptTrigger(QuestAcceptResult result, out uint trigger)
        {
            trigger = result != null ? result.InitTrigger : 0;
            return result != null && result.Success;
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "quest-trigger-count-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO character_invisible_falgs (character_id, slot_index, flag_value)
VALUES (@cid, @qid, 1);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest?.TriggerValue ?? uint.MaxValue;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
