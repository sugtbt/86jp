using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestProgressApplicationService
    {
        private const int MaxCasAttempts = 4;
        private readonly string _connectionString;

        internal QuestProgressApplicationService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal QuestProgressApplicationResult Apply(
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher = null)
        {
            if (request == null || request.CharacterId <= 0)
                return Failed("invalid quest progress request");

            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        if (request.SourceEventId != Guid.Empty
                            && !QuestRepository.TryInsertProgressEvent(
                                connection,
                                transaction,
                                request.CharacterId,
                                request.SourceEventId,
                                request.EventKind))
                        {
                            transaction.Commit();
                            return new QuestProgressApplicationResult
                            {
                                Success = true,
                                DuplicateEvent = true,
                            };
                        }

                        var active = QuestRepository.LoadActiveQuests(
                            connection,
                            transaction,
                            request.CharacterId);
                        var result = new QuestProgressApplicationResult();
                        var eligible = request.EligibleQuestIds == null
                            ? null
                            : new HashSet<ushort>(request.EligibleQuestIds);
                        var retry = false;
                        var foundClientQuest = false;

                        foreach (var quest in active)
                        {
                            if (quest == null
                                || (eligible != null && !eligible.Contains(quest.QuestId)))
                            {
                                continue;
                            }
                            if (request.Operation == QuestProgressOperation.ClientTrigger
                                && quest.QuestId != request.QuestId)
                            {
                                continue;
                            }

                            if (request.Operation == QuestProgressOperation.ClientTrigger)
                                foundClientQuest = true;

                            QuestProgressEvaluation evaluation;
                            try
                            {
                                evaluation = QuestObjectiveEvaluator.Evaluate(
                                    quest,
                                    request,
                                    clearMapMatcher);
                            }
                            catch (Exception ex)
                            {
                                transaction.Rollback();
                                return Failed(
                                    $"objective evaluation failed quest={quest.QuestId}: {ex.Message}");
                            }

                            if (!evaluation.Matched)
                                continue;
                            result.MatchedObjective = true;
                            if (evaluation.Trigger.PackedValue == quest.TriggerValue)
                            {
                                result.AddChanges(evaluation.Changes);
                                continue;
                            }

                            if (!QuestRepository.TryUpdateTriggerValueCas(
                                    connection,
                                    transaction,
                                    request.CharacterId,
                                    quest.QuestId,
                                    quest.Version,
                                    quest.TriggerValue,
                                    evaluation.Trigger.PackedValue))
                            {
                                transaction.Rollback();
                                retry = true;
                                break;
                            }

                            result.AddChanges(evaluation.Changes);
                        }

                        if (retry)
                            continue;

                        if (request.Operation == QuestProgressOperation.ClientTrigger
                            && !foundClientQuest)
                        {
                            result.QuestNotActive = true;
                        }

                        transaction.Commit();
                        result.Success = true;
                        return result;
                    }
                }
            }

            return Failed("quest progress CAS retry exhausted");
        }

        private static QuestProgressApplicationResult Failed(string error)
            => new QuestProgressApplicationResult
            {
                Success = false,
                Error = error ?? string.Empty,
            };
    }

}
