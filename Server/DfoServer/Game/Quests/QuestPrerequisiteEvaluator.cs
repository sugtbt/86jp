using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestPrerequisiteEvaluator
    {
        private readonly string _connectionString;
        private readonly QuestRepository _repository;

        internal QuestPrerequisiteEvaluator(
            string connectionString,
            QuestRepository repository)
        {
            _connectionString = connectionString;
            _repository = repository;
        }

        internal bool IsSatisfied(int characterId, int questId)
        {
            var quest = GameWorld.QuestData.GetQuestFile(questId);
            if (quest == null)
                return true;

            var prerequisiteQuestsSatisfied = true;
            if (quest.PreRequiredQuestGroups != null
                && quest.PreRequiredQuestGroups.Count > 0)
            {
                prerequisiteQuestsSatisfied = false;
                foreach (var group in quest.PreRequiredQuestGroups)
                {
                    var groupSatisfied = true;
                    foreach (var prerequisiteId in GameWorld.QuestData.ParseIntList(group))
                    {
                        if (prerequisiteId > 0
                            && !_repository.IsQuestCleared(characterId, prerequisiteId))
                        {
                            groupSatisfied = false;
                            break;
                        }
                    }

                    if (groupSatisfied)
                    {
                        prerequisiteQuestsSatisfied = true;
                        break;
                    }
                }
            }
            else
            {
                foreach (var prerequisiteId in GameWorld.QuestData
                             .GetPreRequiredQuests(questId))
                {
                    if (prerequisiteId > 0
                        && !_repository.IsQuestCleared(characterId, prerequisiteId))
                    {
                        prerequisiteQuestsSatisfied = false;
                        break;
                    }
                }
            }

            return prerequisiteQuestsSatisfied
                && AreRequiredAnswersSatisfied(characterId, quest);
        }

        private bool AreRequiredAnswersSatisfied(
            int characterId,
            PvfLib.QuestFile quest)
        {
            var requiredAnswers = GameWorld.QuestData.ParseIntList(
                quest.PreRequiredQuestAnswer);
            if (requiredAnswers.Count == 0)
                return true;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                for (var index = 0; index + 1 < requiredAnswers.Count; index += 2)
                {
                    var requiredQuestId = requiredAnswers[index];
                    var requiredAnswer = requiredAnswers[index + 1];
                    if (requiredQuestId <= 0)
                        continue;

                    var expectedFlag = GameWorld.QuestData
                        .GetRequiredQuestAnswerFlagValue(requiredAnswer);
                    var actualFlag = QuestRepository.ReadClearedFlagValue(
                        connection,
                        null,
                        characterId,
                        requiredQuestId);
                    if (expectedFlag <= 0 || actualFlag != expectedFlag)
                        return false;
                }
            }
            return true;
        }
    }
}
