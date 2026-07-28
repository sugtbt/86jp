using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestGiveupApplicationService
    {
        private readonly QuestRepository _repository;

        internal QuestGiveupApplicationService(QuestRepository repository)
        {
            _repository = repository;
        }

        internal QuestGiveupResult Apply(
            int characterId,
            QuestGiveupCommand command,
            InventoryLease lease)
        {
            var questId = command.QuestId;
            var active = _repository.LoadActiveQuests(characterId);
            var quest = QuestActiveListRules.FindByQuestId(active, questId);
            if (quest == null)
                return QuestGiveupResult.Fail(19);
            if (!GameWorld.QuestData.CanGiveup(questId))
                return QuestGiveupResult.Fail(20);

            var recoveryPlan = QuestGiveupItemRecoveryPolicy.Build(
                active,
                questId);
            if (recoveryPlan.Count == 0)
            {
                _repository.DeleteActiveQuest(characterId, quest.Slot);
                FileLogger.Log(
                    $"[QuestGiveupApplicationService] GIVEUP quest={questId} " +
                    "reclaimed=0");
                return new QuestGiveupResult { QuestId = questId };
            }

            if (lease == null || lease.CharacterId != characterId)
            {
                FileLogger.Log(
                    $"[QuestGiveupApplicationService] GIVEUP rejected: " +
                    $"online inventory missing quest={questId} cid={characterId}");
                return QuestGiveupResult.Fail(0x17);
            }

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var snapshots = CaptureTargetSlots(inventory, recoveryPlan);
                var changes = new InventoryMutationSet();
                foreach (var entry in recoveryPlan)
                {
                    var current = inventory.CountMainItem(entry.ItemId);
                    var deleteCount = Math.Max(0, current - entry.RetainCount);
                    if (deleteCount <= 0)
                        continue;

                    if (!TryDeleteMainItem(
                            inventory,
                            entry.ItemId,
                            deleteCount,
                            changes))
                    {
                        RestoreTargetSlots(inventory, snapshots);
                        FileLogger.Log(
                            $"[QuestGiveupApplicationService] GIVEUP rejected: " +
                            $"item reclaim failed quest={questId} item={entry.ItemId} " +
                            $"held={current} retain={entry.RetainCount}");
                        return QuestGiveupResult.Fail(0x17);
                    }
                }

                try
                {
                    _repository.DeleteActiveQuest(characterId, quest.Slot);
                }
                catch (Exception ex)
                {
                    RestoreTargetSlots(inventory, snapshots);
                    FileLogger.Log(
                        $"[QuestGiveupApplicationService] GIVEUP repository failure " +
                        $"quest={questId}: {ex.Message}");
                    return QuestGiveupResult.Fail(19);
                }

                var result = new QuestGiveupResult { QuestId = questId };
                result.InventoryChanges.AddRange(changes);
                FileLogger.Log(
                    $"[QuestGiveupApplicationService] GIVEUP quest={questId} " +
                    $"reclaimedSlots={changes.Slots.Count}");
                return result;
            }
        }

        private static Dictionary<short, ItemCore> CaptureTargetSlots(
            InventoryService inventory,
            IReadOnlyCollection<QuestGiveupItemRecoveryEntry> recoveryPlan)
        {
            var itemIds = new HashSet<int>();
            foreach (var entry in recoveryPlan)
                itemIds.Add(entry.ItemId);

            var snapshots = new Dictionary<short, ItemCore>();
            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                if (pair.Value != null && itemIds.Contains(pair.Value.ItemId))
                    snapshots[pair.Key] = pair.Value.Copy();
            }
            return snapshots;
        }

        private static bool TryDeleteMainItem(
            InventoryService inventory,
            int itemId,
            int deleteCount,
            InventoryMutationSet changes)
        {
            var remaining = deleteCount;
            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                var item = pair.Value;
                if (remaining <= 0)
                    break;
                if (item == null || item.ItemId != itemId)
                    continue;

                var available = InventoryStackRuleService.IsStackable(item)
                    ? Math.Max(0, item.Count)
                    : 1;
                var count = Math.Min(remaining, available);
                if (count <= 0
                    || !InventoryDeleteService.TryDecreaseStack(
                        inventory,
                        InventoryListType.Main,
                        pair.Key,
                        count,
                        out var deleted)
                    || !deleted.Success)
                {
                    return false;
                }

                changes.AddRange(deleted.Changes);
                remaining -= deleted.DeletedCount;
            }
            return remaining == 0;
        }

        private static void RestoreTargetSlots(
            InventoryService inventory,
            IReadOnlyDictionary<short, ItemCore> snapshots)
        {
            if (inventory == null || snapshots == null)
                return;

            foreach (var pair in snapshots)
                inventory.SetItem(
                    InventoryListType.Main,
                    pair.Key,
                    pair.Value.Copy());
        }
    }
}
