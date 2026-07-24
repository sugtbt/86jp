using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    public sealed class ActiveQuest
    {
        public int Slot;
        public ushort QuestId;
        public uint TriggerValue;
    }

    // 任务命令的业务处理。会话层(QuestManager)持有一个实例; 数据访问走 QuestRepository,
    // 物品/金币走在线 InventoryService, 应答包序列化在 QuestAckBuilder。
    public sealed class QuestService
    {
        private const int MaxActiveQuests = 20;

        private readonly string _connStr;
        private readonly QuestRepository _repo;

        public QuestService(string connectionString)
        {
            _connStr = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _repo = new QuestRepository(connectionString);
        }

        public static List<ActiveQuest> LoadActiveQuests(string connStr, int characterId)
        {
            return new QuestRepository(connStr).LoadActiveQuests(characterId);
        }

        public static void SaveActiveQuests(string connStr, int characterId, List<ActiveQuest> quests)
        {
            new QuestRepository(connStr).SaveActiveQuests(characterId, quests);
        }

        public static ActiveQuest FindByQuestId(List<ActiveQuest> active, ushort questId)
        {
            foreach (var q in active)
                if (q.QuestId == questId) return q;
            return null;
        }

        public static int FindFreeSlot(List<ActiveQuest> active)
        {
            var used = new HashSet<int>();
            foreach (var q in active) used.Add(q.Slot);
            for (int i = 0; i < MaxActiveQuests; i++)
                if (!used.Contains(i)) return i;
            return -1;
        }

        public QuestAcceptResult HandleAcceptQuest(int characterId, byte[] body, int accountId = 0)
        {
            if (body == null || body.Length < 2) return QuestAcceptResult.Fail(23);
            ushort questId = BitConverter.ToUInt16(body, 0);

            var active = _repo.LoadActiveQuests(characterId);
            if (FindByQuestId(active, questId) != null) return QuestAcceptResult.Fail(18);

            bool repeatable = GameWorld.QuestData.IsRepeatableQuest(questId);
            if (IsQuestCleared(characterId, questId) && !repeatable)
                return QuestAcceptResult.Fail(18);

            if (!CheckPreRequiredQuests(characterId, questId))
                return QuestAcceptResult.Fail(21);

            var collisions = GameWorld.QuestData.GetCollisionQuests(questId);
            foreach (var colQid in collisions)
            {
                if (colQid > 0 && FindByQuestId(active, (ushort)colQid) != null)
                    return QuestAcceptResult.Fail(21);
            }

            int slot = FindFreeSlot(active);
            if (slot < 0) return QuestAcceptResult.Fail(4);

            uint initTrigger = GameWorld.QuestData.GetInitTrigger(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventSlots = new List<ushort>(eventItems.Count);

            if (eventItems.Count > 0 || seekItems.Count > 0)
            {
                if (!InventoryContext.TryGetLease(characterId, out var lease))
                    return QuestAcceptResult.Fail(4);

                lock (lease.SyncRoot)
                {
                    var inventory = lease.Inventory;
                    var grantRequests = new List<InventoryRewardGrantRequest>();
                    var grantRequestIndexes = new List<int>();

                    for (int i = 0; i < eventItems.Count; i++)
                    {
                        var item = eventItems[i];
                        if (item.ItemId <= 0 || item.Count <= 0)
                        {
                            eventSlots.Add(0);
                            continue;
                        }

                        eventSlots.Add(0);
                        grantRequests.Add(InventoryRewardGrantRequest.Create(
                            item.ItemId,
                            item.Count,
                            ItemCreateReason.QuestReward));
                        grantRequestIndexes.Add(i);
                    }

                    if (grantRequests.Count > 0
                        && !InventoryRewardGrantService.TryPlanBatch(inventory, grantRequests, out _))
                    {
                        return QuestAcceptResult.Fail(4);
                    }

                    using (var conn = new SqliteConnection(_connStr))
                    {
                        conn.Open();
                        using (var tx = conn.BeginTransaction())
                        {
                            if (GameWorld.QuestData.IsQuestClearQuest(questId))
                                initTrigger = ComputeQuestClearTrigger(conn, tx, characterId, questId);

                            // 接取 ACK 会回显事件物；寻物进度需要把本次即将发放的事件物也算进去。
                            if (seekItems.Count > 0)
                                initTrigger = ApplySeekingItemProgress(
                                    initTrigger,
                                    seekItems,
                                    itemId => CountMainItemWithPendingRewards(inventory, itemId, eventItems, 1));

                            QuestRepository.InsertActiveQuest(conn, tx, characterId, slot, questId, initTrigger);
                            if (repeatable)
                                QuestRepository.DeleteClearedFlag(conn, tx, characterId, questId);
                            tx.Commit();
                        }
                    }

                    if (grantRequests.Count > 0)
                    {
                        if (!InventoryRewardGrantService.TryGrantBatch(inventory, grantRequests, out var grantResult))
                        {
                            FileLogger.Log($"[QuestService] ACCEPT inventory grant failed after quest insert: quest={questId} error={grantResult.Error}");
                            return QuestAcceptResult.Fail(4);
                        }

                        for (int i = 0; i < grantResult.Results.Count && i < grantRequestIndexes.Count; i++)
                        {
                            var slotIndex = grantResult.Results[i].SlotIndex;
                            if (slotIndex >= 0)
                                eventSlots[grantRequestIndexes[i]] = (ushort)slotIndex;
                        }
                    }
                }
            }
            else
            {
                using (var conn = new SqliteConnection(_connStr))
                {
                    conn.Open();
                    if (GameWorld.QuestData.IsQuestClearQuest(questId))
                        initTrigger = ComputeQuestClearTrigger(conn, null, characterId, questId);

                    QuestRepository.InsertActiveQuest(conn, null, characterId, slot, questId, initTrigger);
                    if (repeatable)
                    {
                        QuestRepository.DeleteClearedFlag(conn, null, characterId, questId);
                    }
                }

                for (int i = 0; i < eventItems.Count; i++)
                    eventSlots.Add(0);
            }

            var result = new QuestAcceptResult { QuestId = questId, InitTrigger = initTrigger };
            for (int i = 0; i < eventItems.Count; i++)
            {
                result.EventItems.Add(new QuestEventItemGrant
                {
                    SlotIndex = i < eventSlots.Count ? eventSlots[i] : (ushort)0,
                    ItemId = eventItems[i].ItemId,
                    Count = eventItems[i].Count,
                });
            }
            FileLogger.Log($"[QuestService] ACCEPT quest={questId} slot={slot} initTrigger={initTrigger} eventItems={eventItems.Count}");
            return result;
        }

        public QuestGiveupResult HandleGiveupQuest(int characterId, byte[] body)
        {
            if (body == null || body.Length < 2) return QuestGiveupResult.Fail(19);
            ushort questId = BitConverter.ToUInt16(body, 0);

            var active = _repo.LoadActiveQuests(characterId);
            var q = FindByQuestId(active, questId);
            if (q == null) return QuestGiveupResult.Fail(19);
            if (!GameWorld.QuestData.CanGiveup(questId)) return QuestGiveupResult.Fail(20);

            _repo.DeleteActiveQuest(characterId, q.Slot);

            FileLogger.Log($"[QuestService] GIVEUP quest={questId}");
            return new QuestGiveupResult { QuestId = questId };
        }

        public QuestSetTriggerResult HandleSetTrigger(int characterId, byte[] body)
        {
            // Body after 2B wire-type echo strip: u16 questId + u8 triggerType + u8 isIncrement
            // triggerType is a channel bitmask: 0x10=ch0, 0x20=ch1, 0x40=ch2, 0=raw decrement
            // isIncrement: 0=decrement channel, 1=simple ++trigger, nonzero=increment channel
            // Server computes new trigger and echoes back
            if (body == null || body.Length < 3) return QuestSetTriggerResult.Fail(22);
            ushort questId = BitConverter.ToUInt16(body, 0);
            byte triggerType = body[2];
            bool isIncrement = body.Length >= 4 && body[3] != 0;

            var active = _repo.LoadActiveQuests(characterId);
            var q = FindByQuestId(active, questId);
            if (q == null)
            {
                FileLogger.Log($"[QuestService] SET_TRIGGER quest={questId} not in active list, echo back");
                return new QuestSetTriggerResult { QuestId = questId, TriggerValue = 0 };
            }

            uint oldTrigger = q.TriggerValue;
            uint newTrigger;

            if (triggerType == 1)
            {
                newTrigger = oldTrigger + 1;
            }
            else if (isIncrement)
            {
                newTrigger = IncrementTriggerChannel(oldTrigger, triggerType);
            }
            else
            {
                if (oldTrigger == 0) { newTrigger = 0; }
                else { newTrigger = DecrementTriggerChannel(oldTrigger, triggerType); }
            }

            q.TriggerValue = newTrigger;
            _repo.UpdateTriggerValue(characterId, q.Slot, newTrigger);

            FileLogger.Log($"[QuestService] SET_TRIGGER quest={questId} type=0x{triggerType:X2} inc={isIncrement} trigger={oldTrigger}→{newTrigger}");
            return new QuestSetTriggerResult
            {
                QuestId = questId,
                PreviousTriggerValue = oldTrigger,
                TriggerValue = newTrigger,
            };
        }

        public bool SyncMonsterRewardItemProgress(int characterId, int accountId, ICollection<int> itemFilter)
        {
            return SyncItemSeekingQuestProgress(characterId, accountId, itemFilter);
        }

        public bool SyncItemSeekingQuestProgress(
            int characterId,
            int accountId,
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            var active = _repo.LoadActiveQuests(characterId);
            if (active.Count == 0)
                return false;
            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return false;

            var itemCountCache = new Dictionary<int, int>();
            Func<int, int> getHeldCount = itemId =>
            {
                int count;
                if (itemCountCache.TryGetValue(itemId, out count))
                    return count;

                lock (lease.SyncRoot)
                    count = lease.Inventory.CountMainItem(itemId);

                if (temporaryHeldCounts != null
                    && temporaryHeldCounts.TryGetValue(itemId, out var temporaryCount)
                    && temporaryCount > 0)
                {
                    count = count > int.MaxValue - temporaryCount
                        ? int.MaxValue
                        : count + temporaryCount;
                }

                itemCountCache[itemId] = count;
                return count;
            };

            var changed = new List<ActiveQuest>();
            bool matchedQuestItem = false;

            foreach (var q in active)
            {
                var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(q.QuestId);
                if (seekItems.Count == 0)
                    continue;

                var relevantItems = new List<GameWorld.QuestRewardItem>();
                bool matchesFilter = itemFilter == null || itemFilter.Count == 0;
                foreach (var si in seekItems)
                {
                    if (si.ItemId <= 0 || si.Count <= 0)
                        continue;

                    relevantItems.Add(si);
                    if (!matchesFilter && itemFilter.Contains(si.ItemId))
                        matchesFilter = true;
                }

                if (relevantItems.Count == 0 || !matchesFilter)
                    continue;

                matchedQuestItem = true;
                var persistTrigger = ApplySeekingItemProgress(q.TriggerValue, relevantItems, getHeldCount);

                if (q.TriggerValue != persistTrigger)
                {
                    q.TriggerValue = persistTrigger;
                    changed.Add(q);
                }
            }

            if (changed.Count > 0)
                _repo.UpdateTriggerValues(characterId, changed);

            return matchedQuestItem;
        }

        public bool SyncClearMapQuestProgress(int characterId, int dungeonId, int mapId)
        {
            var changed = SyncClearMapQuestProgressCore(
                _connStr,
                characterId,
                dungeonId,
                mapId,
                (questId, targetDungeonId, targetMapId) =>
                    GameWorld.QuestData.MatchesClearMapTarget(questId, targetDungeonId, targetMapId));

            if (changed > 0)
                FileLogger.Log($"[QuestService] CLEAR_MAP progress: cid={characterId} dungeon={dungeonId} map={mapId} changed={changed}");
            return changed > 0;
        }

        internal static int SyncClearMapQuestProgressCore(
            string connStr,
            int characterId,
            int dungeonId,
            int mapId,
            Func<ushort, int, int, bool> matchesClearMapQuest)
        {
            if (string.IsNullOrWhiteSpace(connStr) || characterId <= 0 || matchesClearMapQuest == null)
                return 0;

            var repo = new QuestRepository(connStr);
            var active = repo.LoadActiveQuests(characterId);
            if (active.Count == 0)
                return 0;

            var changed = new List<ActiveQuest>();
            foreach (var q in active)
            {
                if (q.TriggerValue == 0)
                    continue;

                bool matched;
                try
                {
                    matched = matchesClearMapQuest(q.QuestId, dungeonId, mapId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[QuestService] ERROR: clear-map match failed, quest {q.QuestId} skipped: dungeon={dungeonId} map={mapId}: {ex.Message}");
                    matched = false;
                }

                if (!matched)
                    continue;

                q.TriggerValue = 0;
                changed.Add(q);
            }

            if (changed.Count == 0)
                return 0;

            repo.UpdateTriggerValues(characterId, changed);

            return changed.Count;
        }

        private static uint ApplySeekingItemProgress(
            uint trigger,
            List<GameWorld.QuestRewardItem> seekItems,
            Func<int, int> getHeldCount)
        {
            if (seekItems == null || seekItems.Count == 0 || getHeldCount == null)
                return trigger;

            long missingHeld = 0;
            foreach (var si in seekItems)
            {
                if (si.ItemId <= 0 || si.Count <= 0)
                    continue;

                int required = Math.Max(1, si.Count);
                int held = Math.Max(0, getHeldCount(si.ItemId));
                missingHeld += Math.Max(0, required - held);
            }

            return GameWorld.QuestData.ReplaceTriggerChannel(trigger, 0, missingHeld);
        }

        private static void AddMissingCarryForwardEventItemRequests(
            InventoryService inventory,
            List<GameWorld.QuestRewardItem> eventItems,
            List<InventoryRewardGrantRequest> requests)
        {
            if (inventory == null || eventItems == null || eventItems.Count == 0 || requests == null)
                return;

            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;

                int held = Math.Max(0, inventory.CountMainItem(eventItem.ItemId));
                int missing = Math.Max(0, eventItem.Count - held);
                if (missing <= 0)
                    continue;

                requests.Add(InventoryRewardGrantRequest.Create(
                    eventItem.ItemId,
                    missing,
                    ItemCreateReason.QuestReward));
            }
        }

        private static void ConsumeNonCarryForwardEventItems(
            InventoryService inventory,
            List<GameWorld.QuestRewardItem> eventItems,
            List<GameWorld.QuestRewardItem> seekItems,
            List<GameWorld.QuestRewardItem> carryForwardEventItems,
            List<ConsumedItemEntry> consumedEntries)
        {
            if (inventory == null || eventItems == null || eventItems.Count == 0)
                return;

            var seekItemIds = new HashSet<int>();
            if (seekItems != null)
            {
                foreach (var seekItem in seekItems)
                {
                    if (seekItem.ItemId > 0 && seekItem.Count > 0)
                        seekItemIds.Add(GetMainItemIdentityKey(seekItem.ItemId));
                }
            }

            var carryForwardItemIds = new HashSet<int>();
            if (carryForwardEventItems != null)
            {
                foreach (var carryForwardItem in carryForwardEventItems)
                {
                    if (carryForwardItem.ItemId > 0 && carryForwardItem.Count > 0)
                        carryForwardItemIds.Add(GetMainItemIdentityKey(carryForwardItem.ItemId));
                }
            }

            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                var identityKey = GetMainItemIdentityKey(eventItem.ItemId);
                if (seekItemIds.Contains(identityKey) || carryForwardItemIds.Contains(identityKey))
                    continue;

                if (inventory.TryConsumeMainItem(eventItem.ItemId, eventItem.Count, out var consumeResult)
                    && consumeResult.Success)
                {
                    consumedEntries.Add(new ConsumedItemEntry
                    {
                        UpdateType = 0,
                        SlotIndex = (ushort)consumeResult.SlotIndex,
                        ConsumedCount = (uint)consumeResult.ConsumedCount
                    });
                }
            }
        }

        private static bool HasQuestItems(InventoryService inventory, List<GameWorld.QuestRewardItem> items)
        {
            if (inventory == null)
                return false;
            if (items == null || items.Count == 0)
                return true;

            var required = new Dictionary<int, int>();
            var representativeItemIds = new Dictionary<int, int>();
            foreach (var item in items)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                    continue;

                var key = GetMainItemIdentityKey(item.ItemId);
                if (!required.ContainsKey(key))
                {
                    required[key] = 0;
                    representativeItemIds[key] = item.ItemId;
                }

                required[key] = SafeAdd(required[key], item.Count);
            }

            foreach (var pair in required)
            {
                var held = inventory.CountMainItem(representativeItemIds[pair.Key]);
                if (held < pair.Value)
                    return false;
            }

            return true;
        }

        private static bool TryConsumeQuestItems(
            InventoryService inventory,
            List<GameWorld.QuestRewardItem> items,
            List<ConsumedItemEntry> consumedEntries)
        {
            if (items == null || items.Count == 0)
                return true;

            foreach (var item in items)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                    continue;

                if (!inventory.TryConsumeMainItem(item.ItemId, item.Count, out var consumeResult)
                    || !consumeResult.Success)
                    return false;

                consumedEntries.Add(new ConsumedItemEntry
                {
                    UpdateType = 0,
                    SlotIndex = (ushort)consumeResult.SlotIndex,
                    ConsumedCount = (uint)consumeResult.ConsumedCount
                });
            }

            return true;
        }

        private static void AddQuestRewardRequests(
            List<InventoryRewardGrantRequest> requests,
            List<GameWorld.QuestRewardItem> items,
            ushort multiplier,
            bool isTitleRewardQuest,
            ushort questId)
        {
            if (requests == null || items == null || items.Count == 0)
                return;

            foreach (var item in items)
            {
                if (item.ItemId <= 0)
                    continue;
                if (isTitleRewardQuest)
                {
                    FileLogger.Log($"[QuestService] FINISH title reward skipped from inventory: quest={questId} item={item.ItemId}");
                    continue;
                }

                var count = NormalizeQuestItemCount(item.Count, multiplier);
                if (count <= 0)
                    continue;

                requests.Add(InventoryRewardGrantRequest.Create(
                    item.ItemId,
                    count,
                    ItemCreateReason.QuestReward));
            }
        }

        private static bool TryGrantRewardsAndAppendEntries(
            InventoryService inventory,
            List<InventoryRewardGrantRequest> requests,
            List<InsertedItemEntry> insertedEntries)
        {
            if (requests == null || requests.Count == 0)
                return true;

            if (!InventoryRewardGrantService.TryGrantBatch(inventory, requests, out var result)
                || !result.Success)
                return false;

            foreach (var grant in result.Results)
            {
                var entry = ToInsertedItemEntry(grant);
                if (entry != null)
                    insertedEntries.Add(entry);
            }

            return true;
        }

        private static InsertedItemEntry ToInsertedItemEntry(InventoryRewardGrantResult grant)
        {
            if (grant == null || !grant.Success || grant.SlotIndex < 0)
                return null;
            if (grant.Kind == InventoryRewardGrantKind.Premium)
                return null;

            var core = grant.Core;
            var isEquipment = grant.Kind == InventoryRewardGrantKind.InventoryItem
                && core != null
                && !InventoryStackRuleService.IsStackable(core);

            return new InsertedItemEntry
            {
                SlotIndex = (ushort)grant.SlotIndex,
                ItemId = grant.ItemTemplateId,
                IsEquipment = isEquipment,
                CountOrSeed = isEquipment ? (uint)Math.Max(0, core.InstanceValue) : (uint)Math.Max(0, grant.GrantedCount),
                EquipDurability = isEquipment ? core.Durability : (ushort)0
            };
        }

        private static int CountMainItemWithPendingRewards(
            InventoryService inventory,
            int itemId,
            List<GameWorld.QuestRewardItem> pendingRewards,
            ushort multiplier)
        {
            var count = inventory != null ? inventory.CountMainItem(itemId) : 0;
            if (pendingRewards == null || pendingRewards.Count == 0)
                return count;

            foreach (var reward in pendingRewards)
            {
                if (reward.ItemId <= 0 || reward.Count <= 0)
                    continue;
                if (!HasSameMainItemIdentity(itemId, reward.ItemId))
                    continue;

                count = SafeAdd(count, NormalizeQuestItemCount(reward.Count, multiplier));
            }

            return count;
        }

        private static bool HasSameMainItemIdentity(int leftItemId, int rightItemId)
        {
            return GetMainItemIdentityKey(leftItemId) == GetMainItemIdentityKey(rightItemId);
        }

        private static int GetMainItemIdentityKey(int itemId)
        {
            if (InventoryService.TryResolveMainVirtualSlotByItemId(itemId, out var slotIndex, out _))
                return -100000 - slotIndex;

            return itemId;
        }

        private static int NormalizeQuestItemCount(int count, ushort multiplier)
        {
            if (count <= 0)
                return 0;

            var value = (long)count * Math.Max(1, (int)multiplier);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int SafeAdd(int left, int right)
        {
            var value = (long)Math.Max(0, left) + Math.Max(0, right);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private bool CanFinishQuestClearQuest(int characterId, ushort questId)
        {
            if (!GameWorld.QuestData.IsQuestClearQuest(questId))
                return false;

            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                return ComputeQuestClearTrigger(conn, null, characterId, questId) == 0;
            }
        }

        private static void SyncQuestClearParentProgress(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            foreach (var parent in QuestRepository.LoadActiveQuests(conn, tx, characterId))
            {
                if (!GameWorld.QuestData.IsQuestClearQuest(parent.QuestId))
                    continue;

                var nextTrigger = ComputeQuestClearTrigger(conn, tx, characterId, parent.QuestId);
                if (nextTrigger == parent.TriggerValue)
                    continue;

                QuestRepository.UpdateTriggerValue(conn, tx, characterId, parent.Slot, nextTrigger);
                FileLogger.Log($"[QuestService] QUEST_CLEAR sync parent={parent.QuestId} trigger={parent.TriggerValue}->{nextTrigger}");
            }
        }

        private static uint ComputeQuestClearTrigger(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort questId)
        {
            var requiredQuestIds = GameWorld.QuestData.GetQuestClearRequiredQuestIds(questId);
            if (requiredQuestIds.Count == 0)
                return 1;

            var missing = 0;
            foreach (var requiredQuestId in requiredQuestIds)
            {
                if (!QuestRepository.IsQuestCleared(conn, tx, characterId, requiredQuestId))
                    missing++;
            }

            return (uint)missing;
        }

        private static uint DecrementTriggerChannel(uint trigger, byte triggerType)
        {
            if (triggerType == 0) return trigger > 0 ? trigger - 1 : 0;
            if ((triggerType & 0x10) != 0) trigger = AdjustChannel(trigger, 0, -1);
            if ((triggerType & 0x20) != 0) trigger = AdjustChannel(trigger, 9, -1);
            if ((triggerType & 0x40) != 0) trigger = AdjustChannel(trigger, 18, -1);
            return trigger;
        }

        private static uint IncrementTriggerChannel(uint trigger, byte triggerType)
        {
            if (triggerType == 0) return trigger + 1;
            if ((triggerType & 0x10) != 0) trigger = AdjustChannel(trigger, 0, 1);
            if ((triggerType & 0x20) != 0) trigger = AdjustChannel(trigger, 9, 1);
            if ((triggerType & 0x40) != 0) trigger = AdjustChannel(trigger, 18, 1);
            return trigger;
        }

        private static uint AdjustChannel(uint trigger, int shift, int delta)
        {
            uint channel = (trigger >> shift) & 0x1FF;
            int next = (int)channel + delta;
            if (next < 0) next = 0;
            channel = (uint)next & 0x1FF;
            return (trigger & ~(0x1FFu << shift)) | (channel << shift);
        }

        // currentExp: 会话内存中的当前经验。副本杀怪经验只写内存(升级/通关结算才落库),
        // 结算基数必须以会话为准 -- 用库里的陈旧值做基数会把本局杀怪经验覆盖丢失
        // (实测: 副本内直接与NPC交任务, 经验不增反减)。不传时回退读库(自测/无会话场景)。
        public QuestFinishResult HandleFinishQuest(int characterId, byte[] body, uint? currentExp = null)
        {
            if (body == null || body.Length < 2) return QuestFinishResult.Fail(22);
            ushort questId = BitConverter.ToUInt16(body, 0);
            ushort rewardSelectIdx = (body.Length >= 4) ? BitConverter.ToUInt16(body, 2) : (ushort)0;
            bool hasRewardSelectIdx = body.Length >= 4 && rewardSelectIdx != ushort.MaxValue;
            ushort multiplier = (body.Length >= 6) ? BitConverter.ToUInt16(body, 4) : (ushort)1;
            if (multiplier == 0) multiplier = 1;

            var active = _repo.LoadActiveQuests(characterId);
            var q = FindByQuestId(active, questId);

            // 已完成且不在任务栏的任务不能再次交付 -- 否则奖励会重复发放。
            // (可重复任务的完成标记在重新接取时清除, 走不到这里。)
            if (q == null && IsQuestCleared(characterId, questId))
            {
                FileLogger.Log($"[QuestService] FINISH rejected: quest={questId} already cleared and not active, cid={characterId}");
                return QuestFinishResult.Fail(22);
            }

            int clearedFlagValue = 1;
            if (GameWorld.QuestData.IsQuestClearQuest(questId))
            {
                if (!CanFinishQuestClearQuest(characterId, questId))
                    return QuestFinishResult.Fail(22);

                if (q != null)
                    q.TriggerValue = 0;
            }
            else if (GameWorld.QuestData.IsQuestionQuest(questId))
            {
                if (!TryResolveQuestionQuestClearFlagValue(questId, q, hasRewardSelectIdx, rewardSelectIdx, out clearedFlagValue))
                    return QuestFinishResult.Fail(22);
            }
            else if (q != null && q.TriggerValue != 0)
            {
                return QuestFinishResult.Fail(22);
            }

            int playerLevel = GetCharacterLevel(characterId);
            int playerJob = GetCharacterJob(characterId);
            int playerGrowType = GetCharacterGrowType(characterId);
            var reward = GameWorld.QuestData.GetRewardExp(questId, rewardSelectIdx, playerLevel, playerJob, playerGrowType);
            bool isTitleRewardQuest = GameWorld.QuestData.IsTitleRewardQuest(questId);
            var consumedEntries = new List<ConsumedItemEntry>();
            var insertedEntries = new List<InsertedItemEntry>();

            uint goldReward = 0;
            uint expReward = reward.Exp * multiplier;
            uint normalExpReward = expReward;
            uint honorExpReward = 0;
            ulong totalHonorExp = 0;
            uint growthCapsuleExpReward = 0;
            uint totalGrowthCapsuleExp = 0;
            byte newLevel;
            uint newExp;
            var petEvolution = PetCreatureEvolutionResult.Noop;
            int accountId = GetAccountIdByConnStr(characterId);

            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var carryForwardEventItems = GameWorld.QuestData.GetCarryForwardEventItems(questId);

            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return QuestFinishResult.Fail(22);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (!HasQuestItems(inventory, reward.ConsumeItems)
                    || !HasQuestItems(inventory, seekItems))
                    return QuestFinishResult.Fail(22);

                var carryForwardRequests = new List<InventoryRewardGrantRequest>();
                var rewardRequests = new List<InventoryRewardGrantRequest>();
                AddMissingCarryForwardEventItemRequests(inventory, carryForwardEventItems, carryForwardRequests);
                if (reward.ChainType == 0)
                    AddQuestRewardRequests(rewardRequests, reward.Items, multiplier, isTitleRewardQuest, questId);

                var allGrantRequests = new List<InventoryRewardGrantRequest>();
                allGrantRequests.AddRange(carryForwardRequests);
                allGrantRequests.AddRange(rewardRequests);
                if (allGrantRequests.Count > 0
                    && !InventoryRewardGrantService.TryPlanBatch(inventory, allGrantRequests, out _))
                {
                    return QuestFinishResult.Fail(22);
                }

                if (reward.ChainType == 10 || reward.ChainType == 25)
                {
                    petEvolution = PetCreatureEvolutionRuntimeService.TryCompletePetCreatureEvolutionQuest(
                        inventory,
                        reward.CreatureKind,
                        reward.CreatureLevel,
                        reward.GrowNumber);

                    if (!petEvolution.Changed)
                        return QuestFinishResult.Fail(22);
                }

                int goldCarryLimit = int.MaxValue;
                using (var conn = new SqliteConnection(_connStr))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        if (q != null)
                            QuestRepository.DeleteActiveQuest(conn, tx, characterId, q.Slot);

                        goldCarryLimit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(conn, tx, characterId);

                        if (reward.ChainType == 1 || reward.ChainType == 2)
                            UpdateGrowType(conn, tx, characterId, reward.ChainType, reward.GrowNumber);
                        else if (reward.ChainType == 20)
                            UpdateExpertJob(conn, tx, characterId, reward.GrowNumber);
                        else if (reward.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
                            UpdateSlotExpansion(conn, tx, characterId, reward.GrowNumber);

                        if (!GameWorld.QuestData.IsRepeatableQuest(questId))
                            QuestRepository.MarkQuestCleared(conn, tx, characterId, questId, clearedFlagValue);
                        SyncQuestClearParentProgress(conn, tx, characterId);

                        // 经验/等级/战斗属性与奖励同一事务落库:
                        // 崩在中间不会再出现"任务已完成但经验丢失且不可重领"。
                        newLevel = (byte)playerLevel;
                        newExp = currentExp ?? GetCharacterExp(conn, tx, characterId);
                        if (expReward > 0)
                        {
                            var grant = Progression.CharacterExperienceService.GrantInTransaction(
                                conn,
                                tx,
                                characterId,
                                accountId,
                                newLevel,
                                newExp,
                                expReward);
                            newLevel = grant.NewLevel;
                            newExp = grant.NewExp;
                            honorExpReward = grant.HonorExpGain;
                            normalExpReward = grant.NormalExpGain;
                            totalHonorExp = grant.TotalHonorExp;
                            growthCapsuleExpReward = grant.GrowthCapsuleExpGain;
                            totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                        }

                        tx.Commit();
                    }
                }

                if (!TryConsumeQuestItems(inventory, reward.ConsumeItems, consumedEntries)
                    || !TryConsumeQuestItems(inventory, seekItems, consumedEntries))
                {
                    FileLogger.Log($"[QuestService] FINISH inventory consume failed after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }

                ConsumeNonCarryForwardEventItems(
                    inventory,
                    eventItems,
                    seekItems,
                    carryForwardEventItems,
                    consumedEntries);

                if (!TryGrantRewardsAndAppendEntries(inventory, carryForwardRequests, insertedEntries))
                {
                    FileLogger.Log($"[QuestService] FINISH carry-forward grant failed after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }

                var requestedGoldReward = reward.Gold * multiplier;
                if (requestedGoldReward > 0)
                {
                    if (!inventory.TryGrantGold((int)Math.Min(int.MaxValue, requestedGoldReward), goldCarryLimit, out var grantedGold, out _))
                    {
                        FileLogger.Log($"[QuestService] FINISH gold grant failed after quest commit: quest={questId} cid={characterId}");
                        return QuestFinishResult.Fail(22);
                    }

                    goldReward = (uint)Math.Max(0, grantedGold);
                    if (goldReward > 0)
                        insertedEntries.Add(new InsertedItemEntry { SlotIndex = 0, ItemId = 0, CountOrSeed = goldReward });
                }

                if (!TryGrantRewardsAndAppendEntries(inventory, rewardRequests, insertedEntries))
                {
                    FileLogger.Log($"[QuestService] FINISH reward grant failed after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }
            }

            FileLogger.Log($"[QuestService] FINISH quest={questId} rewardIdx={rewardSelectIdx} mult={multiplier} flag={clearedFlagValue} gold={goldReward} consumed={consumedEntries.Count} rewarded={insertedEntries.Count}");
            return new QuestFinishResult
            {
                QuestId = questId,
                Exp = expReward,
                HonorExp = honorExpReward,
                TotalHonorExp = totalHonorExp,
                GrowthCapsuleExp = growthCapsuleExpReward,
                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                Gold = goldReward,
                NewLevel = newLevel,
                NewExp = newExp,
                ChainType = reward.ChainType,
                GrowNumber = reward.GrowNumber,
                PetCreatureEvolution = petEvolution,
                ConsumedEntries = consumedEntries,
                InsertedEntries = insertedEntries,
            };
        }

        private static bool TryResolveQuestionQuestClearFlagValue(
            ushort questId,
            ActiveQuest activeQuest,
            bool hasRewardSelectIdx,
            ushort rewardSelectIdx,
            out int flagValue)
        {
            flagValue = 1;
            int answerCount = GameWorld.QuestData.GetQuestionAnswerCount(questId);
            if (answerCount <= 0)
                return activeQuest == null || activeQuest.TriggerValue == 0;

            if (activeQuest != null && TryResolveQuestionQuestFlagValueFromTrigger(activeQuest.TriggerValue, answerCount, out flagValue))
                return true;

            if (hasRewardSelectIdx && rewardSelectIdx < answerCount)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(rewardSelectIdx);
                return true;
            }

            uint trigger = activeQuest != null ? activeQuest.TriggerValue : uint.MaxValue;
            FileLogger.Log($"[QuestService] Question quest finish rejected: quest={questId} trigger={trigger} answerCount={answerCount}");
            return false;
        }

        private static bool TryResolveQuestionQuestFlagValueFromTrigger(uint trigger, int answerCount, out int flagValue)
        {
            if (trigger == 0)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(0);
                return true;
            }

            if (trigger <= (uint)answerCount)
            {
                flagValue = (int)trigger;
                return true;
            }

            flagValue = 1;
            return false;
        }

        public bool IsQuestCleared(int characterId, ushort questId)
        {
            return _repo.IsQuestCleared(characterId, questId);
        }

        private bool CheckPreRequiredQuests(int characterId, int questId)
        {
            var qst = GameWorld.QuestData.GetQuestFile(questId);
            if (qst == null) return true;

            bool preQuestOk = true;
            if (qst.PreRequiredQuestGroups != null && qst.PreRequiredQuestGroups.Count > 0)
            {
                preQuestOk = false;
                foreach (var group in qst.PreRequiredQuestGroups)
                {
                    var ids = GameWorld.QuestData.ParseIntList(group);
                    bool groupOk = true;
                    foreach (var pq in ids)
                    {
                        if (pq > 0 && !IsQuestCleared(characterId, (ushort)pq))
                        { groupOk = false; break; }
                    }
                    if (groupOk) { preQuestOk = true; break; }
                }
            }
            else
            {
                var preReqs = GameWorld.QuestData.GetPreRequiredQuests(questId);
                if (preReqs.Count > 0)
                {
                    foreach (var preQid in preReqs)
                    {
                        if (preQid > 0 && !IsQuestCleared(characterId, (ushort)preQid))
                        {
                            preQuestOk = false;
                            break;
                        }
                    }
                }
            }

            return preQuestOk && CheckPreRequiredQuestAnswers(characterId, qst);
        }

        private bool CheckPreRequiredQuestAnswers(int characterId, PvfLib.QuestFile qst)
        {
            var preReqAns = GameWorld.QuestData.ParseIntList(qst.PreRequiredQuestAnswer);
            if (preReqAns.Count == 0)
                return true;

            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                for (int i = 0; i + 1 < preReqAns.Count; i += 2)
                {
                    int reqQid = preReqAns[i];
                    int reqAnswer = preReqAns[i + 1];
                    if (reqQid <= 0)
                        continue;

                    int expectedFlag = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(reqAnswer);
                    int actualFlag = QuestRepository.ReadClearedFlagValue(conn, null, characterId, reqQid);
                    if (expectedFlag <= 0 || actualFlag != expectedFlag)
                        return false;
                }
            }

            return true;
        }

        private int GetCharacterLevel(int characterId)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT level FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : 1;
                }
            }
        }

        private static uint GetCharacterExp(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            using (var cmd = new SqliteCommand("SELECT exp FROM characters WHERE character_id=@cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                return result != null ? (uint)Convert.ToInt64(result) : 0u;
            }
        }

        private int GetCharacterJob(int characterId)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT job FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : -1;
                }
            }
        }

        private int GetCharacterGrowType(int characterId)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT grow_type FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : 0;
                }
            }
        }

        private static void UpdateGrowType(SqliteConnection conn, SqliteTransaction tx, int characterId, int chainType, int growNumber)
        {
            byte currentGrowType = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT grow_type FROM characters WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var val = cmd.ExecuteScalar();
                if (val != null) currentGrowType = (byte)Convert.ToInt32(val);
            }

            int firstGrow = currentGrowType & 0xF;
            int secondGrow = (currentGrowType >> 4) & 0xF;

            if (chainType == 1)
                firstGrow = growNumber;
            else if (chainType == 2)
                secondGrow = growNumber;

            byte newGrowType = (byte)((secondGrow << 4) | (firstGrow & 0xF));

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE characters SET grow_type = @grow WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@grow", (int)newGrowType);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateGrowType: cid={characterId} chain={chainType} growNumber={growNumber} old=0x{currentGrowType:X2} new=0x{newGrowType:X2}");

            // 转职(chainType 1)=清空技能+重加创角技+按新growType送技+SP全额重算;
            // 觉醒(chainType 2)=不清技能、只免费植入觉醒技、不动SP。
            byte job = 0;
            byte charLevel = 1;
            uint charExp = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT job, level, exp FROM characters WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var rdr = cmd.ExecuteReader())
                {
                    if (!rdr.Read())
                        throw new InvalidOperationException($"character not found: cid={characterId}");

                    job = (byte)rdr.GetInt32(0);
                    charLevel = (byte)System.Math.Max(1, System.Math.Min(255, rdr.GetInt32(1)));
                    var expValue = rdr.GetInt64(2);
                    charExp = (uint)System.Math.Max(0L, System.Math.Min(uint.MaxValue, expValue));
                }
            }

            var progressRepo = CharacterData.SqliteCharacterProgressRepository.FromConnectionString(conn.ConnectionString);
            if (chainType == 1)
            {
                // 转职: 清空两树 → 重建初始+送技(统一建构器, 设计布局打底)
                var rebuilt = Skills.CharacterSkillProfile.BuildSnapshot(job, firstGrow, 0, charLevel);
                progressRepo.SaveSkillProgress(conn, tx, characterId, rebuilt);
                FileLogger.Log($"[QuestService] GrowTypeChange: rebuilt skills for cid={characterId} job={job} grow={firstGrow}");
            }
            else if (chainType == 2)
            {
                // 觉醒: 在现有技能上植入觉醒技
                var current = progressRepo.LoadSkills(conn, tx, characterId);
                var grants = Skills.CharacterSkillProfile.GetGrowTypeGrants(job, firstGrow, secondGrow);
                Skills.CharacterSkillProfile.MergeGrants(current, grants, job, charLevel);
                progressRepo.SaveSkillProgress(conn, tx, characterId, current);
                FileLogger.Log($"[QuestService] Awakening: planted {grants.Count} awakening skills for cid={characterId}");
            }

            if (!DfoServer.Game.Progression.CharacterProgressService.PersistLevelAndExp(conn, tx, characterId, charLevel, charExp))
                throw new InvalidOperationException($"combat stat refresh failed after grow type update: cid={characterId}");
            FileLogger.Log($"[QuestService] GrowTypeChange: refreshed combat stats for cid={characterId} level={charLevel} grow=0x{newGrowType:X2}");
        }

        private static void UpdateExpertJob(SqliteConnection conn, SqliteTransaction tx, int characterId, int expertJobType)
        {
            byte[] expertJobBlob = BuildExpertJobBlob(1, 1, expertJobType);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO character_subtype0_fields (character_id, expert_job_type) VALUES (@cid, @ejt)
                    ON CONFLICT(character_id) DO UPDATE SET expert_job_type=@ejt;
                    INSERT INTO character_init_flags (character_id, expert_job_blob) VALUES (@cid, @blob)
                    ON CONFLICT(character_id) DO UPDATE SET expert_job_blob=@blob;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@ejt", expertJobType);
                cmd.Parameters.AddWithValue("@blob", expertJobBlob);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateExpertJob: cid={characterId} expertJobType={expertJobType}");
        }

        private static void UpdateSlotExpansion(SqliteConnection conn, SqliteTransaction tx, int characterId, int slotId)
        {
            // Special equipment slots are stored as bits in ex_equip_slot_stat: 21=support, 22=magic stone, 23=earring.
            var flag = ResolveSlotExpansionFlag(slotId);
            if (flag == 0)
            {
                FileLogger.Log($"[QuestService] UpdateSlotExpansion skipped: cid={characterId} unsupported slotId={slotId}");
                return;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE characters
                    SET ex_equip_slot_stat = (ex_equip_slot_stat | @flag),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@flag", flag);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateSlotExpansion: cid={characterId} slotId={slotId} flag=0x{flag:X2}");
        }

        private static int ResolveSlotExpansionFlag(int slotId)
        {
            // Slot ids follow the client equipment index; the persisted flag is zero-based from support equipment.
            if (slotId < 21 || slotId > 23)
                return 0;
            return 1 << (slotId - 21);
        }

        private static byte[] BuildExpertJobBlob(byte state0, byte mode, int expertJobType)
        {
            var list = new List<byte>();
            list.Add(state0);
            list.Add(mode);
            list.AddRange(BitConverter.GetBytes(0));
            list.AddRange(BitConverter.GetBytes(0));
            list.Add((byte)1);
            list.AddRange(BitConverter.GetBytes(expertJobType));
            return list.ToArray();
        }

        private int GetAccountIdByConnStr(int characterId)
        {
            using (var conn = new SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT account_id FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : 1;
                }
            }
        }

    }
}
