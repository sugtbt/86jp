using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemOpenService
    {
        private readonly string _connectionString;
        private readonly LotteryItemDefinitionProvider _definitions;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;

        public LotteryItemOpenService(
            string connectionString,
            LotteryItemDefinitionProvider definitions,
            LotteryDoubleRewardPolicy doubleRewardPolicy)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            _definitions = definitions
                ?? throw new ArgumentNullException(nameof(definitions));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
        }

        internal bool CanOpen(
            InventoryService inventory,
            short slotIndex,
            out LotterySourceContext sourceContext)
        {
            sourceContext = null;
            if (!TryResolveSource(
                    inventory,
                    slotIndex,
                    out var source,
                    out var definition))
            {
                return false;
            }

            if (inventory.CountMainItem(0) < definition.GoldCost)
                return false;
            if (HasRequiredItemCost(definition)
                && inventory.CountMainItem(definition.RequiredItemTemplateId) < definition.RequiredItemCount)
                return false;

            sourceContext = new LotterySourceContext
            {
                SlotIndex = source.SlotIndex,
                ItemTemplateId = source.Core.ItemId,
                StackCount = source.Core.Count,
            };
            return true;
        }

        internal bool TryOpen(
            InventoryService inventory,
            short slotIndex,
            bool useDoubleReward,
            IInventoryOverflowRewardSink overflowSink,
            out LotteryOpenResult result)
        {
            result = null;
            if (inventory == null)
                return false;

            if (!TryResolveSource(
                    inventory,
                    slotIndex,
                    out var source,
                    out var definition))
            {
                return false;
            }

            var currentGold = inventory.CountMainItem(0);
            if (currentGold < definition.GoldCost)
                return false;

            var selectedRewards = RollRewards(definition.RewardPool);
            if (selectedRewards.Count == 0)
                return false;

            var regularRequests = InventorySpecialConsumableService.BuildRewardRequests(
                AggregateRewardEntries(selectedRewards, 1));
            if (!TryPlanOnlineOpen(
                    inventory,
                    source,
                    definition.GoldCost,
                    definition.RequiredItemTemplateId,
                    definition.RequiredItemCount,
                    regularRequests,
                    overflowSink,
                    out var regularPlan))
                return false;

            var appliedDoubleReward = false;
            var appliedPlan = regularPlan;
            if (useDoubleReward && CanAttemptDoubleReward(inventory.CharacterId, inventory.AccountId))
            {
                var doubleRequests = InventorySpecialConsumableService.BuildRewardRequests(
                    AggregateRewardEntries(selectedRewards, 2));
                if (!TryPlanOnlineOpen(
                        inventory,
                        source,
                        definition.GoldCost,
                        definition.RequiredItemTemplateId,
                        definition.RequiredItemCount,
                        doubleRequests,
                        overflowSink,
                        out var doublePlan))
                    return false;

                if (TryConsumeDoubleReward(inventory.CharacterId, inventory.AccountId))
                {
                    appliedDoubleReward = true;
                    appliedPlan = doublePlan;
                }
            }

            var sourceItemTemplateId = source.Core.ItemId;
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    sourceItemTemplateId,
                    1,
                    out var sourceDelete))
                return false;

            var updatedGold = currentGold;
            if (definition.GoldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(0, definition.GoldCost, out var goldConsume)
                    || !goldConsume.Success)
                    return false;

                updatedGold = goldConsume.RemainingCount;
            }

            InventoryMainItemConsumeResult requiredItemConsume = null;
            if (HasRequiredItemCost(definition))
            {
                if (!inventory.TryConsumeMainItem(
                        definition.RequiredItemTemplateId,
                        definition.RequiredItemCount,
                        out requiredItemConsume)
                    || !requiredItemConsume.Success)
                    return false;
            }

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, appliedPlan, out var grantBatch)
                || !grantBatch.Success)
                return false;

            var openResult = new LotteryOpenResult
            {
                SourceSlotIndex = source.SlotIndex,
                SourceItemTemplateId = sourceItemTemplateId,
                SourceRemainingStackCount = sourceDelete.RemainingCount,
                ConsumedGold = definition.GoldCost,
                UpdatedGold = updatedGold,
                ConsumedRequiredItemTemplateId = definition.RequiredItemTemplateId,
                ConsumedRequiredItemCount = HasRequiredItemCost(definition)
                    ? definition.RequiredItemCount
                    : 0,
                UsedDoubleReward = appliedDoubleReward,
            };
            if (requiredItemConsume != null)
            {
                foreach (var change in requiredItemConsume.Changes.Slots)
                {
                    if (change.ListType == InventoryListType.Main
                        && !openResult.RequiredItemChangedSlots.Contains(change.SlotIndex))
                        openResult.RequiredItemChangedSlots.Add(change.SlotIndex);
                }
            }
            AddOnlineGrantResults(inventory, grantBatch, openResult.Rewards);
            result = openResult;
            return true;
        }

        private bool TryResolveSource(
            InventoryService inventory,
            short slotIndex,
            out OnlineLotterySource source,
            out LotteryItemDefinition definition)
        {
            source = null;
            definition = null;
            if (inventory == null || slotIndex < 0)
                return false;

            var core = inventory.GetItem(InventoryListType.Main, slotIndex);
            if (core == null || core.ItemId <= 0 || core.Count <= 0)
                return false;

            if (core.ExpireTime > 0 && core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                return false;

            if (!_definitions.TryGet(core.ItemId, out definition))
                return false;

            source = new OnlineLotterySource
            {
                SlotIndex = slotIndex,
                Core = core,
            };
            return true;
        }

        private bool CanAttemptDoubleReward(int characterId, int accountId)
        {
            return characterId > 0
                && accountId > 0
                && _doubleRewardPolicy.GetUsedCount(characterId) < LotteryDoubleRewardPolicy.DailyLimit
                && _doubleRewardPolicy.HasActiveBenefit(accountId);
        }

        private bool TryConsumeDoubleReward(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!_doubleRewardPolicy.TryConsume(
                            connection,
                            transaction,
                            characterId,
                            accountId))
                        return false;

                    transaction.Commit();
                }
                return true;
            }
        }

        private static bool TryPlanOnlineOpen(
            InventoryService inventory,
            OnlineLotterySource source,
            int goldCost,
            int requiredItemTemplateId,
            int requiredItemCount,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            IInventoryOverflowRewardSink overflowSink,
            out InventoryRewardGrantBatchPlan plan)
        {
            plan = null;
            if (inventory == null || source == null || source.Core == null)
                return false;

            var planningInventory = InventorySpecialConsumableService.CreatePlanningInventory(inventory);
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    planningInventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    source.Core.ItemId,
                    1,
                    out _))
                return false;

            if (goldCost > 0
                && (!planningInventory.TryConsumeMainItem(0, goldCost, out var goldConsume)
                    || !goldConsume.Success))
                return false;

            if (requiredItemTemplateId > 0
                && requiredItemCount > 0
                && (!planningInventory.TryConsumeMainItem(
                        requiredItemTemplateId,
                        requiredItemCount,
                        out var requiredItemConsume)
                    || !requiredItemConsume.Success))
                return false;

            if (InventoryRewardGrantService.TryPlanBatch(planningInventory, requests, out plan))
                return true;

            overflowSink = overflowSink ?? RejectingInventoryOverflowRewardSink.Instance;
            overflowSink.TryDeliver(inventory, requests ?? Array.Empty<InventoryRewardGrantRequest>(), out _);
            return false;
        }

        private static bool HasRequiredItemCost(LotteryItemDefinition definition)
        {
            return definition != null
                && definition.RequiredItemTemplateId > 0
                && definition.RequiredItemCount > 0;
        }

        internal static List<PvfLib.BoosterRewardEntry> RollRewards(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            var selected = new List<PvfLib.BoosterRewardEntry>();
            if (rewards == null)
                return selected;

            foreach (var group in rewards.GroupBy(reward => reward.Group))
            {
                var totalWeight = group.Sum(reward => Math.Max(0, reward.Weight));
                if (totalWeight <= 0)
                    continue;

                var drawCount = Math.Max(1, group.Max(reward => reward.DrawCount));
                for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
                {
                    var roll = ServerRandom.Next(totalWeight);
                    var cumulative = 0;
                    foreach (var reward in group)
                    {
                        cumulative += Math.Max(0, reward.Weight);
                        if (roll >= cumulative)
                            continue;

                        selected.Add(reward);
                        break;
                    }
                }
            }

            return selected;
        }

        private static IEnumerable<PvfLib.BoosterRewardEntry> AggregateRewardEntries(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            int multiplier)
        {
            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => new { reward.ItemId, reward.UsablePeriodDays })
                .Select(group => new PvfLib.BoosterRewardEntry
                {
                    ItemId = group.Key.ItemId,
                    Count = group.Sum(reward => Math.Max(1, reward.Count)) * Math.Max(1, multiplier),
                    UsablePeriodDays = group.Key.UsablePeriodDays,
                });
        }

        private static void AddOnlineGrantResults(
            InventoryService inventory,
            InventoryRewardGrantBatchResult batch,
            List<LotteryRewardGrant> rewards)
        {
            if (batch == null || rewards == null)
                return;

            foreach (var grant in batch.Results)
            {
                var reward = ToLotteryRewardGrant(inventory, grant);
                if (reward != null)
                    rewards.Add(reward);
            }
        }

        private static LotteryRewardGrant ToLotteryRewardGrant(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            if (grant == null || !grant.Success)
                return null;
            if (grant.Kind == InventoryRewardGrantKind.Premium)
                return null;

            var core = grant.Kind == InventoryRewardGrantKind.InventoryItem
                ? inventory?.GetItem(grant.ListType, grant.SlotIndex)
                : null;

            return new LotteryRewardGrant
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                StackCount = ResolveStackCount(core, grant),
                GrantedCount = grant.GrantedCount,
            };
        }

        private static int ResolveStackCount(ItemCore core, InventoryRewardGrantResult grant)
        {
            if (grant == null)
                return 0;
            if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
                return grant.FinalCount;
            if (core == null)
                return 0;

            return InventoryStackRuleService.IsStackable(core)
                ? core.Count
                : core.InstanceValue;
        }

        private sealed class OnlineLotterySource
        {
            public short SlotIndex { get; set; }

            public ItemCore Core { get; set; }
        }
    }
}
