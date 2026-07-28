using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestAcceptanceApplicationService
    {
        private readonly string _connectionString;
        private readonly QuestRepository _repository;
        private readonly QuestPrerequisiteEvaluator _prerequisites;

        internal QuestAcceptanceApplicationService(
            string connectionString,
            QuestRepository repository)
        {
            _connectionString = connectionString;
            _repository = repository;
            _prerequisites = new QuestPrerequisiteEvaluator(
                connectionString,
                repository);
        }

        internal QuestAcceptResult Apply(
            int characterId,
            QuestAcceptCommand command,
            int accountId = 0)
        {
            var questId = command.QuestId;
            var active = _repository.LoadActiveQuests(characterId);
            if (QuestActiveListRules.FindByQuestId(active, questId) != null)
                return QuestAcceptResult.Fail(18);

            var repeatable = GameWorld.QuestData.IsRepeatableQuest(questId);
            if (_repository.IsQuestCleared(characterId, questId) && !repeatable)
                return QuestAcceptResult.Fail(18);
            if (!_prerequisites.IsSatisfied(characterId, questId))
                return QuestAcceptResult.Fail(21);

            foreach (var collisionQuestId in GameWorld.QuestData.GetCollisionQuests(questId))
            {
                if (collisionQuestId > 0
                    && QuestActiveListRules.FindByQuestId(
                        active,
                        (ushort)collisionQuestId) != null)
                {
                    return QuestAcceptResult.Fail(21);
                }
            }

            var slot = QuestActiveListRules.FindFreeSlot(active);
            if (slot < 0)
                return QuestAcceptResult.Fail(QuestSlotLayout.ActiveListFullFallbackError);

            var initialTrigger = GameWorld.QuestData.GetInitTrigger(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventSlots = new List<ushort>(eventItems.Count);

            if (eventItems.Count > 0 || seekItems.Count > 0)
            {
                if (!InventoryContext.TryGetLease(characterId, out var lease))
                    return QuestAcceptResult.Fail(0x17);

                lock (lease.SyncRoot)
                {
                    var inventory = lease.Inventory;
                    var grantRequests = new List<InventoryRewardGrantRequest>();
                    var grantRequestIndexes = new List<int>();
                    for (var index = 0; index < eventItems.Count; index++)
                    {
                        var item = eventItems[index];
                        eventSlots.Add(0);
                        if (item.ItemId <= 0 || item.Count <= 0)
                            continue;

                        grantRequests.Add(InventoryRewardGrantRequest.Create(
                            item.ItemId,
                            item.Count,
                            ItemCreateReason.QuestReward));
                        grantRequestIndexes.Add(index);
                    }

                    if (grantRequests.Count > 0
                        && !InventoryRewardGrantService.TryPlanBatch(
                            inventory,
                            grantRequests,
                            out _))
                    {
                        return QuestAcceptResult.Fail(0x11);
                    }

                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (GameWorld.QuestData.IsQuestClearQuest(questId))
                            {
                                initialTrigger = QuestClearProgressRules.Compute(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }

                            if (seekItems.Count > 0)
                            {
                                initialTrigger = QuestProgressReducer.ApplySeekingItems(
                                    new QuestTrigger(initialTrigger),
                                    seekItems,
                                    itemId => CountMainItemWithPendingRewards(
                                        inventory,
                                        itemId,
                                        eventItems)).PackedValue;
                            }

                            QuestRepository.InsertActiveQuest(
                                connection,
                                transaction,
                                characterId,
                                slot,
                                questId,
                                initialTrigger);
                            if (repeatable)
                            {
                                QuestRepository.DeleteClearedFlag(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }
                            transaction.Commit();
                        }
                    }

                    if (grantRequests.Count > 0)
                    {
                        if (!InventoryRewardGrantService.TryGrantBatch(
                                inventory,
                                grantRequests,
                                out var grantResult))
                        {
                            FileLogger.Log(
                                $"[QuestAcceptanceApplicationService] inventory grant failed " +
                                $"after quest insert: quest={questId} error={grantResult.Error}");
                            return QuestAcceptResult.Fail(0x17);
                        }

                        for (var index = 0;
                             index < grantResult.Results.Count
                             && index < grantRequestIndexes.Count;
                             index++)
                        {
                            var slotIndex = grantResult.Results[index].SlotIndex;
                            if (slotIndex >= 0)
                                eventSlots[grantRequestIndexes[index]] = (ushort)slotIndex;
                        }
                    }
                }
            }
            else
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    if (GameWorld.QuestData.IsQuestClearQuest(questId))
                    {
                        initialTrigger = QuestClearProgressRules.Compute(
                            connection,
                            null,
                            characterId,
                            questId);
                    }

                    QuestRepository.InsertActiveQuest(
                        connection,
                        null,
                        characterId,
                        slot,
                        questId,
                        initialTrigger);
                    if (repeatable)
                    {
                        QuestRepository.DeleteClearedFlag(
                            connection,
                            null,
                            characterId,
                            questId);
                    }
                }
            }

            var result = new QuestAcceptResult
            {
                QuestId = questId,
                InitTrigger = initialTrigger,
            };
            for (var index = 0; index < eventItems.Count; index++)
            {
                result.EventItems.Add(new QuestEventItemGrant
                {
                    SlotIndex = index < eventSlots.Count ? eventSlots[index] : (ushort)0,
                    ItemId = eventItems[index].ItemId,
                    Count = eventItems[index].Count,
                });
            }
            FileLogger.Log(
                $"[QuestAcceptanceApplicationService] ACCEPT quest={questId} " +
                $"slot={slot} initTrigger={initialTrigger} eventItems={eventItems.Count}");
            return result;
        }

        private static int CountMainItemWithPendingRewards(
            InventoryService inventory,
            int itemId,
            IReadOnlyCollection<GameWorld.QuestRewardItem> pendingRewards)
        {
            var count = inventory != null ? inventory.CountMainItem(itemId) : 0;
            if (pendingRewards == null)
                return count;

            foreach (var reward in pendingRewards)
            {
                if (reward.ItemId <= 0 || reward.Count <= 0)
                    continue;
                if (GetMainItemIdentityKey(itemId)
                    != GetMainItemIdentityKey(reward.ItemId))
                {
                    continue;
                }

                var value = (long)Math.Max(0, count) + reward.Count;
                count = value > int.MaxValue ? int.MaxValue : (int)value;
            }
            return count;
        }

        private static int GetMainItemIdentityKey(int itemId)
        {
            return InventoryService.TryResolveMainVirtualSlotByItemId(
                itemId,
                out var slotIndex,
                out _)
                ? -100000 - slotIndex
                : itemId;
        }
    }
}
