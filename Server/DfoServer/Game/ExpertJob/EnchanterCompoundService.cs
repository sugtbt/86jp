using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.ExpertJob
{
    internal static class EnchanterCompoundService
    {
        internal const byte ErrorInventoryFull = 4;
        internal const byte ErrorLevelTooLow = 14;
        internal const byte ErrorInvalidState = 19;
        internal const byte ErrorInsufficientMaterials = 21;

        internal static bool TryCraft(
            InventoryService inventory,
            EnchanterCompoundCommand command,
            uint currentExperience,
            ExpertJobState state,
            out EnchanterCompoundResult result)
        {
            result = new EnchanterCompoundResult { ErrorCode = ErrorInvalidState };
            if (inventory == null
                || command == null
                || command.RecipeItemId <= 0
                || command.RequestedCount == 0)
            {
                return false;
            }

            return command.IsProductCraft
                ? TryCraftProduct(inventory, command, currentExperience, state, result)
                : TryCraftBead(inventory, command, currentExperience, result);
        }

        private static bool TryCraftProduct(
            InventoryService inventory,
            EnchanterCompoundCommand command,
            uint currentExperience,
            ExpertJobState state,
            EnchanterCompoundResult result)
        {
            var config = EnchanterConfigProvider.Config;
            if (state == null
                || !config.RecipesByItemId.TryGetValue(command.RecipeItemId, out var recipe)
                || !state.LearnedRecipeIds.Contains(recipe.RecipeItemId))
            {
                return false;
            }

            if (config.GetLevel(currentExperience) < recipe.RequiredLevel)
            {
                result.ErrorCode = ErrorLevelTooLow;
                return false;
            }

            if (!InventoryCompoundItemRecipeService.TryParseCompoundRecipe(
                    command.RecipeItemId,
                    out var compoundRecipe)
                || compoundRecipe.Outputs.Count != 1
                || compoundRecipe.Outputs[0].ItemTemplateId != recipe.ProductItemId)
            {
                return false;
            }

            var request = new CompoundItemRecipeRequest
            {
                SourceValue = command.RecipeItemId,
                SourceIsItemId = true,
                RequestedCount = command.RequestedCount,
            };
            if (!InventoryCompoundItemRecipeService.TryCompoundItemRecipe(
                    inventory,
                    request,
                    out var compoundResult))
            {
                result.ErrorCode = compoundResult?.ErrorCode ?? ErrorInvalidState;
                return false;
            }

            foreach (var output in compoundResult.Rewards
                         .Where(item => item.ItemTemplateId > 0 && item.GrantedCount > 0)
                         .GroupBy(item => item.ItemTemplateId)
                         .OrderBy(group => group.Key))
            {
                result.Outputs.Add(new EnchanterCompoundOutput
                {
                    ItemId = output.Key,
                    Count = output.Sum(item => item.GrantedCount),
                });
            }
            if (result.Outputs.Count == 0)
                throw new InvalidOperationException("enchanter product craft granted no output");
            foreach (var slotIndex in compoundResult.GetMainRefreshSlots())
                result.AddChangedMainSlot(slotIndex);

            result.SuccessCount = command.RequestedCount;
            result.ExperienceGain = CalculateProductExperience(recipe, result.SuccessCount);
            CompleteExperience(config, currentExperience, result);
            result.ExtractorInventoryChanged = config.Extractors.ContainsKey(
                recipe.ProductItemId);
            result.GoldSpent = compoundResult.GoldSpent;
            result.ErrorCode = 0;
            return true;
        }

        private static bool TryCraftBead(
            InventoryService inventory,
            EnchanterCompoundCommand command,
            uint currentExperience,
            EnchanterCompoundResult result)
        {
            if (!command.IsCardCraft || command.RequestedCount != 1)
                return false;

            var config = EnchanterConfigProvider.Config;
            if (!config.CardRecipesByItemId.TryGetValue(command.RecipeItemId, out var recipe))
                return false;

            var expertJobLevel = config.GetLevel(currentExperience);
            if (expertJobLevel < recipe.RequiredLevel)
            {
                result.ErrorCode = ErrorLevelTooLow;
                return false;
            }
            if (!config.CardExperienceRulesByLevel.TryGetValue(
                    expertJobLevel,
                    out var experienceRule)
                || recipe.Qualification < 0
                || recipe.Qualification >= experienceRule.SuccessRates.Length)
            {
                return false;
            }

            var card = inventory.GetItem(InventoryListType.Main, command.CardSlotIndex);
            if (card == null
                || card.Count <= 0
                || !config.CardsByItemId.TryGetValue(card.ItemId, out var cardDefinition)
                || cardDefinition.Qualification != recipe.Qualification
                || !config.BeadItemIdByCardItemId.TryGetValue(card.ItemId, out var beadItemId))
            {
                return false;
            }
            if (!InventoryCreateService.TryCreateCore(
                    beadItemId,
                    ItemCreateReason.Unknown,
                    1,
                    out var beadCore))
            {
                return false;
            }
            beadCore.EnchantUpgradeCount = card.EnchantUpgradeCount;

            if (!InventoryMaterialConsumptionService.HasEnough(inventory, recipe.Materials))
            {
                result.ErrorCode = ErrorInsufficientMaterials;
                return false;
            }

            var planningInventory = InventoryCompoundPlanning.CloneInventory(inventory);
            if (!InventoryDeleteService.TryUseStackableForClient(
                    planningInventory,
                    InventoryListType.Main,
                    command.CardSlotIndex,
                    card.ItemId,
                    out _)
                || !InventoryMaterialConsumptionService.TryConsume(
                    planningInventory,
                    recipe.Materials,
                    null))
            {
                throw new InvalidOperationException(
                    "enchanter bead planning mutation failed after validation");
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    new[]
                    {
                        InventoryRewardGrantRequest.Existing(
                            beadCore,
                            1,
                            ItemCreateReason.Unknown),
                    },
                    out var rewardPlan)
                || rewardPlan == null
                || !rewardPlan.Success)
            {
                result.ErrorCode = ErrorInventoryFull;
                return false;
            }
            var succeeded = ServerRandom.Next(100)
                < experienceRule.SuccessRates[recipe.Qualification];

            if (!InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    command.CardSlotIndex,
                    card.ItemId,
                    out _))
            {
                throw new InvalidOperationException(
                    "enchanter bead card mutation failed after validation");
            }
            result.AddChangedMainSlot(command.CardSlotIndex);

            var consumedMaterials = new System.Collections.Generic.List<InventoryMaterialConsumptionEntry>();
            if (!InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    recipe.Materials,
                    consumedMaterials))
            {
                throw new InvalidOperationException(
                    "enchanter bead material mutation failed after validation");
            }
            foreach (var material in consumedMaterials)
                result.AddChangedMainSlot(material.SlotIndex);

            if (succeeded)
            {
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        inventory,
                        rewardPlan,
                        out var rewardBatch)
                    || rewardBatch == null
                    || !rewardBatch.Success
                    || rewardBatch.Results.Count != 1
                    || !rewardBatch.Results[0].Success)
                {
                    throw new InvalidOperationException(
                        "enchanter bead reward mutation failed after validation");
                }

                var reward = rewardBatch.Results[0];
                result.Outputs.Add(new EnchanterCompoundOutput
                {
                    ItemId = beadItemId,
                    Count = reward.GrantedCount,
                });
                result.AddChangedMainSlot(reward.SlotIndex);
                result.SuccessCount = 1;
                result.ExperienceGain = NextInclusive(
                    experienceRule.MinimumExperienceGain,
                    experienceRule.MaximumExperienceGain);
            }
            else
            {
                result.FailureCount = 1;
            }

            CompleteExperience(config, currentExperience, result);
            result.ErrorCode = 0;
            return true;
        }

        private static int CalculateProductExperience(
            EnchanterRecipeDefinition recipe,
            int successCount)
        {
            long total = 0;
            for (var index = 0; index < successCount && total < int.MaxValue; index++)
                total += NextInclusive(recipe.MinimumExperienceGain, recipe.MaximumExperienceGain);
            return (int)Math.Min(int.MaxValue, total);
        }

        private static void CompleteExperience(
            EnchanterConfig config,
            uint currentExperience,
            EnchanterCompoundResult result)
        {
            result.FinalExperience = (uint)Math.Min(
                uint.MaxValue,
                (ulong)currentExperience + (uint)result.ExperienceGain);
            result.LearnedRecipeIds.AddRange(config.GetNewAutoLearnRecipeIds(
                currentExperience,
                result.FinalExperience));
            result.RequiresExpertJobInfoRefresh = config.GetLevel(currentExperience)
                != config.GetLevel(result.FinalExperience);
        }

        private static int NextInclusive(int minimum, int maximum)
            => maximum <= minimum
                ? minimum
                : minimum + ServerRandom.Next(maximum - minimum + 1);
    }
}
