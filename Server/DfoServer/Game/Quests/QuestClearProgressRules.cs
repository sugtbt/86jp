using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal static class QuestClearProgressRules
    {
        internal static bool CanFinish(
            string connectionString,
            int characterId,
            ushort questId)
        {
            if (!GameWorld.QuestData.IsQuestClearQuest(questId))
                return false;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return Compute(connection, null, characterId, questId) == 0;
            }
        }

        internal static void SynchronizeActiveParents(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            foreach (var parent in QuestRepository.LoadActiveQuests(
                         connection,
                         transaction,
                         characterId))
            {
                if (!GameWorld.QuestData.IsQuestClearQuest(parent.QuestId))
                    continue;

                var nextTrigger = Compute(
                    connection,
                    transaction,
                    characterId,
                    parent.QuestId);
                if (nextTrigger == parent.TriggerValue)
                    continue;

                QuestRepository.UpdateTriggerValue(
                    connection,
                    transaction,
                    characterId,
                    parent.Slot,
                    nextTrigger);
                FileLogger.Log(
                    $"[QuestClearProgressRules] sync parent={parent.QuestId} " +
                    $"trigger={parent.TriggerValue}->{nextTrigger}");
            }
        }

        internal static uint Compute(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort questId)
        {
            var requiredQuestIds = GameWorld.QuestData
                .GetQuestClearRequiredQuestIds(questId);
            if (requiredQuestIds.Count == 0)
                return 1;

            var missing = 0;
            foreach (var requiredQuestId in requiredQuestIds)
            {
                if (!QuestRepository.IsQuestCleared(
                        connection,
                        transaction,
                        characterId,
                        requiredQuestId))
                {
                    missing++;
                }
            }
            return (uint)missing;
        }
    }
}
