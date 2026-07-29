using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestCompletionApplicationService
    {
        private readonly string _connectionString;
        private readonly QuestRepository _repository;

        internal QuestCompletionApplicationService(
            string connectionString,
            QuestRepository repository)
        {
            _connectionString = connectionString;
            _repository = repository;
        }

        // currentExp is the authoritative in-session value while a character is
        // in a dungeon. Falling back to the database preserves headless selftests.
        internal QuestFinishResult Apply(
            int characterId,
            QuestFinishCommand command,
            uint? currentExp = null)
        {
            var questId = command.QuestId;
            var rewardSelectionIndex = command.RewardSelectionIndex;
            var hasRewardSelection = command.HasRewardSelection;
            var multiplier = command.Multiplier;
            var active = _repository.LoadActiveQuests(characterId);
            var activeQuest = QuestActiveListRules.FindByQuestId(active, questId);

            if (activeQuest == null && _repository.IsQuestCleared(characterId, questId))
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] FINISH rejected: " +
                    $"quest={questId} already cleared and not active, cid={characterId}");
                return QuestFinishResult.Fail(22);
            }

            var clearedFlagValue = 1;
            if (GameWorld.QuestData.IsQuestClearQuest(questId))
            {
                if (!QuestClearProgressRules.CanFinish(
                        _connectionString,
                        characterId,
                        questId))
                {
                    return QuestFinishResult.Fail(22);
                }

                if (activeQuest != null)
                    activeQuest.TriggerValue = 0;
            }
            else if (GameWorld.QuestData.IsQuestionQuest(questId))
            {
                if (!TryResolveQuestionQuestClearFlagValue(
                        questId,
                        activeQuest,
                        hasRewardSelection,
                        rewardSelectionIndex,
                        out clearedFlagValue))
                {
                    return QuestFinishResult.Fail(22);
                }
            }
            else if (activeQuest != null && activeQuest.TriggerValue != 0)
            {
                return QuestFinishResult.Fail(22);
            }

            var playerLevel = GetCharacterScalar(characterId, "level", 1);
            var playerJob = GetCharacterScalar(characterId, "job", -1);
            var playerGrowType = GetCharacterScalar(characterId, "grow_type", 0);
            var rewardResolution = GameWorld.QuestData.ResolveReward(
                questId,
                hasRewardSelection ? rewardSelectionIndex : -1,
                playerLevel,
                playerJob,
                playerGrowType);
            if (!rewardResolution.IsValid)
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] FINISH rejected invalid reward " +
                    $"definition: quest={questId} cid={characterId} " +
                    $"error={rewardResolution.Error}");
                return QuestFinishResult.Fail(22);
            }

            var reward = rewardResolution.Reward;
            var isTitleRewardQuest = GameWorld.QuestData.IsTitleRewardQuest(questId);
            var consumedEntries = new List<ConsumedItemEntry>();
            var insertedEntries = new List<InsertedItemEntry>();
            uint goldReward = 0;
            var expReward = reward.Exp * multiplier;
            uint honorExpReward = 0;
            ulong totalHonorExp = 0;
            uint growthCapsuleExpReward = 0;
            uint totalGrowthCapsuleExp = 0;
            byte newLevel;
            uint newExp;
            var petEvolution = PetCreatureEvolutionResult.Noop;
            var accountId = GetCharacterScalar(characterId, "account_id", 1);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var carryForwardEventItems = GameWorld.QuestData
                .GetCarryForwardEventItems(questId);

            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return QuestFinishResult.Fail(22);

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (!HasQuestItems(inventory, reward.ConsumeItems)
                    || !HasQuestItems(inventory, seekItems))
                {
                    return QuestFinishResult.Fail(22);
                }

                var carryForwardRequests = new List<InventoryRewardGrantRequest>();
                var rewardRequests = new List<InventoryRewardGrantRequest>();
                AddMissingCarryForwardEventItemRequests(
                    inventory,
                    carryForwardEventItems,
                    carryForwardRequests);
                if (reward.ChainType == 0)
                {
                    AddQuestRewardRequests(
                        rewardRequests,
                        reward.Items,
                        multiplier,
                        isTitleRewardQuest,
                        questId);
                }

                var allGrantRequests = new List<InventoryRewardGrantRequest>();
                allGrantRequests.AddRange(carryForwardRequests);
                allGrantRequests.AddRange(rewardRequests);
                if (allGrantRequests.Count > 0
                    && !InventoryRewardGrantService.TryPlanBatch(
                        inventory,
                        allGrantRequests,
                        out _))
                {
                    return QuestFinishResult.Fail(22);
                }

                if (reward.ChainType == 10 || reward.ChainType == 25)
                {
                    petEvolution = PetCreatureEvolutionRuntimeService
                        .TryCompletePetCreatureEvolutionQuest(
                            inventory,
                            reward.CreatureKind,
                            reward.CreatureLevel,
                            reward.GrowNumber);
                    if (!petEvolution.Changed)
                        return QuestFinishResult.Fail(22);
                }

                var goldCarryLimit = int.MaxValue;
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (activeQuest != null)
                        {
                            QuestRepository.DeleteActiveQuest(
                                connection,
                                transaction,
                                characterId,
                                activeQuest.Slot);
                        }

                        goldCarryLimit = CharacterGoldLimitRepository
                            .LoadEffectiveGoldCarryLimit(
                                connection,
                                transaction,
                                characterId);

                        if (reward.ChainType == 1 || reward.ChainType == 2)
                        {
                            UpdateGrowType(
                                connection,
                                transaction,
                                characterId,
                                reward.ChainType,
                                reward.GrowNumber);
                        }
                        else if (reward.ChainType == 20)
                        {
                            UpdateExpertJob(
                                connection,
                                transaction,
                                characterId,
                                reward.GrowNumber);
                        }
                        else if (reward.ChainType
                                 == GameWorld.QuestData.ChainTypeSlotExpansion)
                        {
                            UpdateSlotExpansion(
                                connection,
                                transaction,
                                characterId,
                                reward.GrowNumber);
                        }

                        if (!GameWorld.QuestData.IsRepeatableQuest(questId))
                        {
                            QuestRepository.MarkQuestCleared(
                                connection,
                                transaction,
                                characterId,
                                questId,
                                clearedFlagValue);
                        }
                        QuestClearProgressRules.SynchronizeActiveParents(
                            connection,
                            transaction,
                            characterId);

                        newLevel = (byte)playerLevel;
                        newExp = currentExp
                            ?? GetCharacterExp(
                                connection,
                                transaction,
                                characterId);
                        if (expReward > 0)
                        {
                            var grant = Progression.CharacterExperienceService
                                .GrantInTransaction(
                                    connection,
                                    transaction,
                                    characterId,
                                    accountId,
                                    newLevel,
                                    newExp,
                                    expReward);
                            newLevel = grant.NewLevel;
                            newExp = grant.NewExp;
                            honorExpReward = grant.HonorExpGain;
                            totalHonorExp = grant.TotalHonorExp;
                            growthCapsuleExpReward = grant.GrowthCapsuleExpGain;
                            totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                        }
                        transaction.Commit();
                    }
                }

                if (!TryConsumeQuestItems(
                        inventory,
                        reward.ConsumeItems,
                        consumedEntries)
                    || !TryConsumeQuestItems(
                        inventory,
                        seekItems,
                        consumedEntries))
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH inventory consume " +
                        $"failed after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }

                ConsumeNonCarryForwardEventItems(
                    inventory,
                    eventItems,
                    seekItems,
                    carryForwardEventItems,
                    consumedEntries);
                if (!TryGrantRewardsAndAppendEntries(
                        inventory,
                        carryForwardRequests,
                        insertedEntries))
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH carry-forward grant " +
                        $"failed after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }

                var requestedGoldReward = reward.Gold * multiplier;
                if (requestedGoldReward > 0)
                {
                    if (!inventory.TryGrantGold(
                            (int)Math.Min(int.MaxValue, requestedGoldReward),
                            goldCarryLimit,
                            out var grantedGold,
                            out _))
                    {
                        FileLogger.Log(
                            $"[QuestCompletionApplicationService] FINISH gold grant failed " +
                            $"after quest commit: quest={questId} cid={characterId}");
                        return QuestFinishResult.Fail(22);
                    }

                    goldReward = (uint)Math.Max(0, grantedGold);
                    if (goldReward > 0)
                    {
                        insertedEntries.Add(new InsertedItemEntry
                        {
                            SlotIndex = 0,
                            ItemId = 0,
                            CountOrSeed = goldReward,
                        });
                    }
                }

                if (!TryGrantRewardsAndAppendEntries(
                        inventory,
                        rewardRequests,
                        insertedEntries))
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH reward grant failed " +
                        $"after quest commit: quest={questId} cid={characterId}");
                    return QuestFinishResult.Fail(22);
                }
            }

            FileLogger.Log(
                $"[QuestCompletionApplicationService] FINISH quest={questId} " +
                $"rewardIdx={rewardSelectionIndex} mult={multiplier} " +
                $"flag={clearedFlagValue} gold={goldReward} " +
                $"consumed={consumedEntries.Count} rewarded={insertedEntries.Count}");
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

        private static void AddMissingCarryForwardEventItemRequests(
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            ICollection<InventoryRewardGrantRequest> requests)
        {
            if (inventory == null || eventItems == null || requests == null)
                return;

            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                var held = Math.Max(0, inventory.CountMainItem(eventItem.ItemId));
                var missing = Math.Max(0, eventItem.Count - held);
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
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> seekItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> carryForwardEventItems,
            ICollection<ConsumedItemEntry> consumedEntries)
        {
            if (inventory == null || eventItems == null)
                return;

            var seekItemIds = ToItemIdentitySet(seekItems);
            var carryForwardItemIds = ToItemIdentitySet(carryForwardEventItems);
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                var identityKey = GetMainItemIdentityKey(eventItem.ItemId);
                if (seekItemIds.Contains(identityKey)
                    || carryForwardItemIds.Contains(identityKey))
                {
                    continue;
                }

                if (inventory.TryConsumeMainItem(
                        eventItem.ItemId,
                        eventItem.Count,
                        out var consumeResult)
                    && consumeResult.Success)
                {
                    consumedEntries.Add(new ConsumedItemEntry
                    {
                        UpdateType = 0,
                        SlotIndex = (ushort)consumeResult.SlotIndex,
                        ConsumedCount = (uint)consumeResult.ConsumedCount,
                    });
                }
            }
        }

        private static HashSet<int> ToItemIdentitySet(
            IReadOnlyCollection<GameWorld.QuestRewardItem> items)
        {
            var identities = new HashSet<int>();
            if (items == null)
                return identities;
            foreach (var item in items)
            {
                if (item.ItemId > 0 && item.Count > 0)
                    identities.Add(GetMainItemIdentityKey(item.ItemId));
            }
            return identities;
        }

        private static bool HasQuestItems(
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items)
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

            foreach (var requirement in required)
            {
                if (inventory.CountMainItem(
                        representativeItemIds[requirement.Key])
                    < requirement.Value)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryConsumeQuestItems(
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items,
            ICollection<ConsumedItemEntry> consumedEntries)
        {
            if (items == null || items.Count == 0)
                return true;
            foreach (var item in items)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                    continue;
                if (!inventory.TryConsumeMainItem(
                        item.ItemId,
                        item.Count,
                        out var consumeResult)
                    || !consumeResult.Success)
                {
                    return false;
                }
                consumedEntries.Add(new ConsumedItemEntry
                {
                    UpdateType = 0,
                    SlotIndex = (ushort)consumeResult.SlotIndex,
                    ConsumedCount = (uint)consumeResult.ConsumedCount,
                });
            }
            return true;
        }

        private static void AddQuestRewardRequests(
            ICollection<InventoryRewardGrantRequest> requests,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items,
            ushort multiplier,
            bool isTitleRewardQuest,
            ushort questId)
        {
            if (requests == null || items == null)
                return;
            foreach (var item in items)
            {
                if (item.ItemId <= 0)
                    continue;
                if (isTitleRewardQuest)
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH title reward " +
                        $"skipped from inventory: quest={questId} item={item.ItemId}");
                    continue;
                }
                var count = NormalizeQuestItemCount(item.Count, multiplier);
                if (count > 0)
                {
                    requests.Add(InventoryRewardGrantRequest.Create(
                        item.ItemId,
                        count,
                        ItemCreateReason.QuestReward));
                }
            }
        }

        private static bool TryGrantRewardsAndAppendEntries(
            InventoryService inventory,
            List<InventoryRewardGrantRequest> requests,
            ICollection<InsertedItemEntry> insertedEntries)
        {
            if (requests == null || requests.Count == 0)
                return true;
            if (!InventoryRewardGrantService.TryGrantBatch(
                    inventory,
                    requests,
                    out var result)
                || !result.Success)
            {
                return false;
            }
            foreach (var grant in result.Results)
            {
                var entry = ToInsertedItemEntry(grant);
                if (entry != null)
                    insertedEntries.Add(entry);
            }
            return true;
        }

        private static InsertedItemEntry ToInsertedItemEntry(
            InventoryRewardGrantResult grant)
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
                CountOrSeed = isEquipment
                    ? (uint)Math.Max(0, core.InstanceValue)
                    : (uint)Math.Max(0, grant.GrantedCount),
                EquipDurability = isEquipment ? core.Durability : (ushort)0,
            };
        }

        private static bool TryResolveQuestionQuestClearFlagValue(
            ushort questId,
            ActiveQuest activeQuest,
            bool hasRewardSelection,
            ushort rewardSelectionIndex,
            out int flagValue)
        {
            flagValue = 1;
            var answerCount = GameWorld.QuestData.GetQuestionAnswerCount(questId);
            if (answerCount <= 0)
                return activeQuest == null || activeQuest.TriggerValue == 0;
            if (activeQuest != null
                && TryResolveQuestionQuestFlagValueFromTrigger(
                    activeQuest.TriggerValue,
                    answerCount,
                    out flagValue))
            {
                return true;
            }
            if (hasRewardSelection && rewardSelectionIndex < answerCount)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(
                    rewardSelectionIndex);
                return true;
            }

            var trigger = activeQuest != null
                ? activeQuest.TriggerValue
                : uint.MaxValue;
            FileLogger.Log(
                $"[QuestCompletionApplicationService] Question quest finish rejected: " +
                $"quest={questId} trigger={trigger} answerCount={answerCount}");
            return false;
        }

        private static bool TryResolveQuestionQuestFlagValueFromTrigger(
            uint trigger,
            int answerCount,
            out int flagValue)
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

        private int GetCharacterScalar(
            int characterId,
            string column,
            int fallback)
        {
            // Column is selected only from this class's fixed call sites.
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand(
                           $"SELECT {column} FROM characters WHERE character_id=@cid",
                           connection))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    var result = command.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : fallback;
                }
            }
        }

        private static uint GetCharacterExp(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = new SqliteCommand(
                       "SELECT exp FROM characters WHERE character_id=@cid",
                       connection,
                       transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                var result = command.ExecuteScalar();
                return result != null ? (uint)Convert.ToInt64(result) : 0u;
            }
        }

        private static void UpdateGrowType(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int chainType,
            int growNumber)
        {
            byte currentGrowType = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT grow_type FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                if (value != null)
                    currentGrowType = (byte)Convert.ToInt32(value);
            }

            var firstGrow = currentGrowType & 0xF;
            var secondGrow = (currentGrowType >> 4) & 0xF;
            if (chainType == 1)
                firstGrow = growNumber;
            else if (chainType == 2)
                secondGrow = growNumber;
            var newGrowType = (byte)((secondGrow << 4) | (firstGrow & 0xF));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE characters SET grow_type = @grow WHERE character_id = @cid";
                command.Parameters.AddWithValue("@grow", (int)newGrowType);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
            FileLogger.Log(
                $"[QuestCompletionApplicationService] UpdateGrowType: cid={characterId} " +
                $"chain={chainType} growNumber={growNumber} " +
                $"old=0x{currentGrowType:X2} new=0x{newGrowType:X2}");

            byte job;
            byte characterLevel;
            uint characterExp;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT job, level, exp FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"character not found: cid={characterId}");
                    }
                    job = (byte)reader.GetInt32(0);
                    characterLevel = (byte)Math.Max(1, Math.Min(255, reader.GetInt32(1)));
                    var expValue = reader.GetInt64(2);
                    characterExp = (uint)Math.Max(
                        0L,
                        Math.Min(uint.MaxValue, expValue));
                }
            }

            var progressRepository = CharacterData.SqliteCharacterProgressRepository
                .FromConnectionString(connection.ConnectionString);
            if (chainType == 1)
            {
                var rebuilt = Skills.CharacterSkillProfile.BuildSnapshot(
                    job,
                    firstGrow,
                    0,
                    characterLevel);
                progressRepository.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    rebuilt);
            }
            else if (chainType == 2)
            {
                var current = progressRepository.LoadSkills(
                    connection,
                    transaction,
                    characterId);
                var grants = Skills.CharacterSkillProfile.GetGrowTypeGrants(
                    job,
                    firstGrow,
                    secondGrow);
                Skills.CharacterSkillProfile.MergeGrants(
                    current,
                    grants,
                    job,
                    characterLevel);
                progressRepository.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    current);
            }

            if (!Progression.CharacterProgressService.PersistLevelAndExp(
                    connection,
                    transaction,
                    characterId,
                    characterLevel,
                    characterExp))
            {
                throw new InvalidOperationException(
                    $"combat stat refresh failed after grow type update: " +
                    $"cid={characterId}");
            }
        }

        private static void UpdateExpertJob(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int expertJobType)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO character_subtype0_fields (character_id, expert_job_type) VALUES (@cid, @ejt)
                    ON CONFLICT(character_id) DO UPDATE SET expert_job_type=@ejt;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@ejt", expertJobType);
                command.ExecuteNonQuery();
            }

            SqliteExpertJobStateRepository.InitializeInTransaction(
                connection,
                transaction,
                characterId,
                expertJobType);
        }

        private static void UpdateSlotExpansion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int slotId)
        {
            var flag = ResolveSlotExpansionFlag(slotId);
            if (flag == 0)
                return;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE characters
                    SET ex_equip_slot_stat = (ex_equip_slot_stat | @flag),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@flag", flag);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static int ResolveSlotExpansionFlag(int slotId)
            => slotId < 21 || slotId > 23 ? 0 : 1 << (slotId - 21);

        private static int GetMainItemIdentityKey(int itemId)
        {
            return InventoryService.TryResolveMainVirtualSlotByItemId(
                itemId,
                out var slotIndex,
                out _)
                ? -100000 - slotIndex
                : itemId;
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
    }
}
