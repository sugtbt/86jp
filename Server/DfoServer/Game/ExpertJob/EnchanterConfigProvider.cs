using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class EnchanterExtractionRule
    {
        internal int ResultItemId { get; set; }
        internal double Multiplier { get; set; }
        internal int AdditionalTable { get; set; }
        internal int BigWinTable { get; set; }
        internal int BigWinChancePercent { get; set; }
    }

    internal sealed class EnchanterExtractorDefinition
    {
        internal int ItemId { get; set; }
        internal int RequiredExpertJobLevel { get; set; }
        internal int ExtractionIndex { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class EnchanterSelectionRule : ExpertJobSelectionRule
    {
        internal double QuantityMultiplier { get; set; }
    }

    internal sealed class EnchanterRecipeDefinition
    {
        internal int RecipeItemId { get; set; }
        internal int ProductItemId { get; set; }
        internal int RequiredLevel { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class EnchanterCardRecipeDefinition
    {
        internal int Qualification { get; set; }
        internal int RecipeItemId { get; set; }
        internal int RequiredLevel { get; set; }
        internal IReadOnlyList<InventoryMaterialRequirement> Materials { get; set; } =
            Array.Empty<InventoryMaterialRequirement>();
    }

    internal sealed class EnchanterCardDefinition
    {
        internal int Qualification { get; set; }
        internal int BindChance { get; set; }
    }

    internal sealed class EnchanterCardExperienceRule
    {
        internal int ExpertJobLevel { get; set; }
        internal int[] SuccessRates { get; set; } = Array.Empty<int>();
        internal int ExtraRate { get; set; }
        internal int MinimumExperienceGain { get; set; }
        internal int MaximumExperienceGain { get; set; }
    }

    internal sealed class EnchanterConfig
    {
        private const int RecipeLearningLevelOffset = 2;

        internal int MaximumStoreCharge { get; set; }
        internal int InitialEndurance { get; set; }
        internal int EnduranceReduction { get; set; }
        internal int EnduranceReductionMinimumLevel { get; set; }
        internal int ExtractionBaseConst { get; set; }
        internal Dictionary<int, EnchanterExtractorDefinition> Extractors { get; } =
            new Dictionary<int, EnchanterExtractorDefinition>();
        internal Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), EnchanterExtractionRule>
            ExtractionRules { get; } =
                new Dictionary<(int ExtractorItemId, int Rarity, int EquipmentState), EnchanterExtractionRule>();
        internal Dictionary<int, List<EnchanterSelectionRule>> AdditionalResults { get; } =
            new Dictionary<int, List<EnchanterSelectionRule>>();
        internal Dictionary<int, List<EnchanterSelectionRule>> BigWinResults { get; } =
            new Dictionary<int, List<EnchanterSelectionRule>>();
        internal List<int> ExperienceThresholds { get; } = new List<int>();
        internal Dictionary<int, int> AutoLearnRecipes { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> StoreSkills { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> CardQualificationLevelRequirements { get; } =
            new Dictionary<int, int>();
        internal Dictionary<int, EnchanterCardRecipeDefinition> CardRecipesByItemId { get; } =
            new Dictionary<int, EnchanterCardRecipeDefinition>();
        internal Dictionary<int, EnchanterCardDefinition> CardsByItemId { get; } =
            new Dictionary<int, EnchanterCardDefinition>();
        internal Dictionary<int, int> BeadItemIdByCardItemId { get; } =
            new Dictionary<int, int>();
        internal Dictionary<int, EnchanterCardExperienceRule> CardExperienceRulesByLevel { get; } =
            new Dictionary<int, EnchanterCardExperienceRule>();
        internal Dictionary<int, EnchanterRecipeDefinition> RecipesByItemId { get; } =
            new Dictionary<int, EnchanterRecipeDefinition>();
        internal List<EnchanterRepairRule> RepairRules { get; } = new List<EnchanterRepairRule>();

        internal int GetLevel(uint experience)
        {
            var level = 1;
            foreach (var threshold in ExperienceThresholds)
            {
                if (experience < threshold)
                    break;
                level++;
            }
            return Math.Min(ExperienceThresholds.Count, level);
        }

        internal bool CanLearnRecipe(uint experience, EnchanterRecipeDefinition recipe)
            => recipe != null
                && recipe.RequiredLevel <= GetLevel(experience) + RecipeLearningLevelOffset;

        internal IReadOnlyList<int> GetAutoLearnRecipeIds(uint experience)
        {
            var level = GetLevel(experience);
            return AutoLearnRecipes
                .Where(pair => pair.Key <= level)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
        }

        internal IReadOnlyList<int> GetNewAutoLearnRecipeIds(
            uint previousExperience,
            uint currentExperience)
        {
            var previousLevel = GetLevel(previousExperience);
            var currentLevel = GetLevel(currentExperience);
            if (currentLevel <= previousLevel)
                return Array.Empty<int>();
            return AutoLearnRecipes
                .Where(pair => pair.Key > previousLevel && pair.Key <= currentLevel)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
        }

        internal IReadOnlyList<byte> GetStoreSkillIds(uint experience)
        {
            var level = GetLevel(experience);
            return StoreSkills
                .Where(pair => pair.Value <= level && pair.Key <= byte.MaxValue)
                .OrderBy(pair => pair.Key)
                .Select(pair => (byte)pair.Key)
                .ToArray();
        }

        internal IReadOnlyList<byte> GetCardQualificationLevels(uint experience)
        {
            var level = GetLevel(experience);
            return CardQualificationLevelRequirements
                .Where(pair => pair.Value <= level && pair.Key >= 0 && pair.Key <= byte.MaxValue)
                .OrderBy(pair => pair.Key)
                .Select(pair => (byte)pair.Key)
                .ToArray();
        }

        internal EnchanterRepairRule GetRepairRule(int level)
            => level > 0 && level <= RepairRules.Count ? RepairRules[level - 1] : null;
    }

    internal sealed class EnchanterRepairRule
    {
        internal int FullRepairCost { get; set; }
        internal int MaximumEndurance { get; set; }
    }

    internal static class EnchanterConfigProvider
    {
        private const string PvfPath = "character/expertjob/enchanter.exj";
        private static readonly Lazy<EnchanterConfig> ConfigValue = new Lazy<EnchanterConfig>(Load);

        internal static EnchanterConfig Config => ConfigValue.Value;

        private static EnchanterConfig Load()
        {
            var content = PvfArchiveAccessor.ReadText(PvfPath);
            var root = new ScriptParser().Parse(content);
            var config = new EnchanterConfig
            {
                MaximumStoreCharge = ReadSingleInt(root, content, "limit store charge"),
                InitialEndurance = ReadSingleInt(root, content, "endurance initial value"),
            };
            var enduranceReduction = ReadTokens(root, content, "endurance reduce");
            if (enduranceReduction.Length != 2)
                throw new InvalidOperationException($"PVF {PvfPath} [endurance reduce] is invalid");
            config.EnduranceReduction = ParseInt(enduranceReduction[1]);
            config.EnduranceReductionMinimumLevel = ParseInt(enduranceReduction[0]);
            ParseRepairRules(ReadTokens(root, content, "endurance repair cost"), config);

            ParseExperienceThresholds(ReadTokens(root, content, "expertness exp"), config);
            ParsePairs(ReadTokens(root, content, "auto learn recipe"), config.AutoLearnRecipes, "auto learn recipe");
            ParsePairs(ReadTokens(root, content, "skill"), config.StoreSkills, "skill");
            ParseCardQualifications(root, content, config);
            ParseCardExperienceRules(ReadTokens(root, content, "monstercard exp"), config);
            var productExperience = ParseExperienceRanges(
                ReadTokens(root, content, "product exp"),
                "product exp");
            var extractionExperience = ParseExperienceRanges(
                ReadTokens(root, content, "extract exp"),
                "extract exp");
            ParseExtractors(ReadTokens(root, content, "items"), extractionExperience, config);
            ParseExtractionBase(ReadTokens(root, content, "enchanter extraction result item"), config);
            ParseExtractionRules(ReadTokens(root, content, "extraction result"), config);
            ParseRecipes(ReadTokens(root, content, "items"), productExperience, config);
            ParseSelections(ReadTokens(root, content, "additional result"), config.AdditionalResults);
            ParseSelections(ReadTokens(root, content, "big win result"), config.BigWinResults);

            if (config.MaximumStoreCharge < 0
                || config.InitialEndurance <= 0
                || config.EnduranceReduction <= 0
                || config.EnduranceReductionMinimumLevel <= 0
                || config.RepairRules.Count != config.ExperienceThresholds.Count
                || config.ExtractionBaseConst <= 0
                || config.ExperienceThresholds.Count == 0
                || config.AutoLearnRecipes.Count == 0
                || config.Extractors.Count == 0
                || config.ExtractionRules.Count == 0
                || config.CardQualificationLevelRequirements.Count == 0
                || config.CardRecipesByItemId.Count != config.CardQualificationLevelRequirements.Count
                || config.CardsByItemId.Count == 0
                || config.BeadItemIdByCardItemId.Count == 0
                || config.CardExperienceRulesByLevel.Count != config.ExperienceThresholds.Count)
            {
                throw new InvalidOperationException($"PVF {PvfPath} has invalid enchanter configuration");
            }
            ValidateReferences(config);
            return config;
        }

        private static void ParseRecipes(
            string[] tokens,
            IReadOnlyDictionary<int, (int Minimum, int Maximum)> productExperience,
            EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 4 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [items] row width is not 4");
            for (var index = 0; index < tokens.Length; index += 4)
            {
                var definition = new EnchanterRecipeDefinition
                {
                    RecipeItemId = ParseInt(tokens[index + 1]),
                    ProductItemId = ParseInt(tokens[index + 2]),
                    RequiredLevel = ParseInt(tokens[index + 3]),
                };
                if (!productExperience.TryGetValue(definition.RecipeItemId, out var experience))
                    continue;
                definition.MinimumExperienceGain = experience.Minimum;
                definition.MaximumExperienceGain = experience.Maximum;
                if (definition.RecipeItemId <= 0
                    || definition.ProductItemId <= 0
                    || definition.RequiredLevel <= 0
                    || config.RecipesByItemId.ContainsKey(definition.RecipeItemId))
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid recipe definition");

                if (!DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        definition.RecipeItemId,
                        out var recipeItem)
                    || !NormalizeTag(recipeItem.StackableType).StartsWith(
                        "[recipe]",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        NormalizeTag(recipeItem.ItemCategory),
                        "expertjob recipe",
                        StringComparison.OrdinalIgnoreCase)
                    || !HasRequiredEnchanterSkill(recipeItem.NeedSkill, config.StoreSkills))
                    continue;
                config.RecipesByItemId.Add(definition.RecipeItemId, definition);
            }
            if (config.RecipesByItemId.Count == 0)
                throw new InvalidOperationException($"PVF {PvfPath} has no learnable recipe definitions");
        }

        private static string NormalizeTag(string value)
            => (value ?? string.Empty).Replace("`", string.Empty).Trim();

        private static bool HasRequiredEnchanterSkill(
            string needSkill,
            IReadOnlyDictionary<int, int> skills)
        {
            var values = (needSkill ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillId)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillLevel))
                return false;
            return skillLevel > 0 && skills.ContainsKey(skillId);
        }

        private static void ParseRepairRules(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [endurance repair cost] is invalid");
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var cost = ParseInt(tokens[index]);
                var endurance = ParseInt(tokens[index + 1]);
                if (cost <= 0 || endurance <= 0)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid endurance repair rule");
                config.RepairRules.Add(new EnchanterRepairRule
                {
                    FullRepairCost = cost,
                    MaximumEndurance = endurance,
                });
            }
        }

        private static void ParseExperienceThresholds(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [expertness exp] row width is not 3");
            var previous = -1;
            for (var index = 0; index < tokens.Length; index += 3)
            {
                var threshold = ParseInt(tokens[index]);
                if (threshold <= previous)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid expertness thresholds");
                config.ExperienceThresholds.Add(threshold);
                previous = threshold;
            }
        }

        private static Dictionary<int, (int Minimum, int Maximum)> ParseExperienceRanges(
            string[] tokens,
            string tag)
        {
            if (tokens.Length == 0 || tokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] row width is not 3");
            var result = new Dictionary<int, (int Minimum, int Maximum)>();
            for (var index = 0; index < tokens.Length; index += 3)
            {
                var itemId = ParseInt(tokens[index]);
                var minimum = ParseInt(tokens[index + 1]);
                var maximum = ParseInt(tokens[index + 2]);
                if (itemId <= 0 || minimum < 0 || maximum < minimum)
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid {tag} range");
                if (result.ContainsKey(itemId))
                    throw new InvalidOperationException($"PVF {PvfPath} has duplicate {tag} item");
                result.Add(itemId, (minimum, maximum));
            }
            return result;
        }

        private static void ParseExtractors(
            string[] tokens,
            IReadOnlyDictionary<int, (int Minimum, int Maximum)> extractionExperience,
            EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 4 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [items] row width is not 4");
            for (var index = 0; index < tokens.Length; index += 4)
            {
                var productItemId = ParseInt(tokens[index + 2]);
                var requiredLevel = ParseInt(tokens[index + 3]);
                if (!extractionExperience.TryGetValue(productItemId, out var experience))
                    continue;
                if (requiredLevel <= 0
                    || requiredLevel > config.ExperienceThresholds.Count
                    || config.Extractors.ContainsKey(productItemId)
                    || !DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        productItemId,
                        out var item)
                    || !string.Equals(
                        item.ExpertJobOnlyType,
                        "enchanter",
                        StringComparison.OrdinalIgnoreCase)
                    || item.ExpertJobOnlyLevel != requiredLevel
                    || item.EnchanterExtractionIndex < 0)
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid extractor definition");
                }
                config.Extractors.Add(productItemId, new EnchanterExtractorDefinition
                {
                    ItemId = productItemId,
                    RequiredExpertJobLevel = requiredLevel,
                    ExtractionIndex = item.EnchanterExtractionIndex,
                    MinimumExperienceGain = experience.Minimum,
                    MaximumExperienceGain = experience.Maximum,
                });
            }
            if (config.Extractors.Count != extractionExperience.Count
                || config.Extractors.Values.Select(item => item.ExtractionIndex).Distinct().Count()
                    != config.Extractors.Count)
                throw new InvalidOperationException($"PVF {PvfPath} has unresolved or duplicate extractors");
        }

        private static void ParseExtractionBase(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length != 2 || ParseInt(tokens[0]) <= 0)
                throw new InvalidOperationException($"PVF {PvfPath} [enchanter extraction result item] is invalid");
            config.ExtractionBaseConst = ParseInt(tokens[1]);
        }

        private static void ParseExtractionRules(string[] tokens, EnchanterConfig config)
        {
            if (tokens.Length == 0 || tokens.Length % 8 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [extraction result] row width is not 8");
            for (var index = 0; index < tokens.Length; index += 8)
            {
                var key = (ParseInt(tokens[index]), ParseInt(tokens[index + 1]), ParseInt(tokens[index + 2]));
                if (config.ExtractionRules.ContainsKey(key))
                    throw new InvalidOperationException($"PVF {PvfPath} has duplicate extraction result");
                var rule = new EnchanterExtractionRule
                {
                    ResultItemId = ParseInt(tokens[index + 3]),
                    Multiplier = ParseDouble(tokens[index + 4]),
                    AdditionalTable = ParseInt(tokens[index + 5]),
                    BigWinTable = ParseInt(tokens[index + 6]),
                    BigWinChancePercent = ParseInt(tokens[index + 7]),
                };
                if (key.Item1 <= 0
                    || key.Item2 < 0
                    || key.Item2 > 6
                    || key.Item3 < 0
                    || key.Item3 > 2
                    || rule.ResultItemId <= 0
                    || rule.Multiplier <= 0
                    || rule.AdditionalTable < 0
                    || rule.BigWinTable < 0
                    || rule.BigWinChancePercent < 0
                    || rule.BigWinChancePercent > 100)
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid extraction result values");
                }
                config.ExtractionRules.Add(key, rule);
            }
        }

        private static void ParseSelections(string[] tokens, Dictionary<int, List<EnchanterSelectionRule>> target)
        {
            if (tokens.Length % 6 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} selection row width is not 6");
            for (var index = 0; index < tokens.Length; index += 6)
            {
                var table = ParseInt(tokens[index]);
                if (!target.TryGetValue(table, out var rules))
                {
                    rules = new List<EnchanterSelectionRule>();
                    target.Add(table, rules);
                }
                var rule = new EnchanterSelectionRule
                {
                    MinimumLevel = ParseInt(tokens[index + 1]),
                    MaximumLevel = ParseInt(tokens[index + 2]),
                    ItemId = ParseInt(tokens[index + 3]),
                    Weight = ParseInt(tokens[index + 4]),
                    QuantityMultiplier = ParseDouble(tokens[index + 5]),
                };
                if (table <= 0
                    || rule.MinimumLevel < 0
                    || rule.MaximumLevel < rule.MinimumLevel
                    || rule.ItemId <= 0
                    || rule.Weight <= 0
                    || rule.QuantityMultiplier <= 0)
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid selection values");
                }
                rules.Add(rule);
            }
        }

        private static void ParsePairs(string[] tokens, Dictionary<int, int> target, string tag)
        {
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] row width is not 2");
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var key = ParseInt(tokens[index]);
                var value = ParseInt(tokens[index + 1]);
                if (key <= 0 || value <= 0 || target.ContainsKey(key))
                    throw new InvalidOperationException($"PVF {PvfPath} [{tag}] has invalid or duplicate values");
                target.Add(key, value);
            }
        }

        private static void ValidateReferences(EnchanterConfig config)
        {
            var maximumLevel = config.ExperienceThresholds.Count;
            if (config.AutoLearnRecipes.Any(pair => pair.Key > maximumLevel)
                || config.AutoLearnRecipes.Values.Distinct().Count() != config.AutoLearnRecipes.Count
                || config.StoreSkills.Any(pair => pair.Key > byte.MaxValue || pair.Value > maximumLevel)
                || config.CardQualificationLevelRequirements.Any(pair =>
                    pair.Key < 0 || pair.Key > byte.MaxValue || pair.Value <= 0 || pair.Value > maximumLevel)
                || config.CardRecipesByItemId.Values.Any(recipe =>
                    !config.CardQualificationLevelRequirements.TryGetValue(
                        recipe.Qualification, out var requiredLevel)
                    || requiredLevel != recipe.RequiredLevel
                    || recipe.Materials.Count == 0)
                || config.CardsByItemId.Keys.Any(cardItemId =>
                    !config.BeadItemIdByCardItemId.ContainsKey(cardItemId))
                || config.CardExperienceRulesByLevel.Keys.Any(level =>
                    level <= 0 || level > maximumLevel))
            {
                throw new InvalidOperationException($"PVF {PvfPath} has invalid enchanter cross references");
            }

            foreach (var pair in config.ExtractionRules)
            {
                var rule = pair.Value;
                if (!config.Extractors.ContainsKey(pair.Key.ExtractorItemId)
                    || (rule.AdditionalTable > 0 && !config.AdditionalResults.ContainsKey(rule.AdditionalTable))
                    || (rule.BigWinTable > 0 && !config.BigWinResults.ContainsKey(rule.BigWinTable)))
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has unresolved extraction references");
                }
            }
        }

        private static void ParseCardQualifications(
            ScriptNode root,
            string content,
            EnchanterConfig config)
        {
            var rarityRecipe = root.GetChild("rarity recipe");
            if (rarityRecipe == null)
                throw new InvalidOperationException($"PVF {PvfPath} has no [rarity recipe]");

            foreach (var rarityNode in rarityRecipe.GetChildren("rarity"))
            {
                var rarity = ParseSingleNodeInt(rarityNode, content, "rarity");
                var recipeItemId = ParseSingleNodeInt(rarityNode, content, "recipe");
                var requiredLevel = ParseSingleNodeInt(rarityNode, content, "expert job level");
                if (!DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        recipeItemId,
                        out var recipeItem)
                    || !NormalizeTag(recipeItem.StackableType).StartsWith(
                        "[recipe]",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        NormalizeTag(recipeItem.ExpertJobOnlyType),
                        "enchanter",
                        StringComparison.OrdinalIgnoreCase)
                    || recipeItem.ExpertJobOnlyLevel != requiredLevel
                    || !HasRequiredEnchanterSkill(recipeItem.NeedSkill, config.StoreSkills))
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has an invalid card recipe item");
                }

                var materials = ParseMaterialRequirements(recipeItem.InputItem);
                if (rarity < 0 || rarity > byte.MaxValue || recipeItemId <= 0 || requiredLevel <= 0
                    || materials.Count == 0
                    || config.CardQualificationLevelRequirements.ContainsKey(rarity)
                    || config.CardRecipesByItemId.ContainsKey(recipeItemId))
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid rarity recipe");
                config.CardQualificationLevelRequirements.Add(rarity, requiredLevel);
                config.CardRecipesByItemId.Add(recipeItemId, new EnchanterCardRecipeDefinition
                {
                    Qualification = rarity,
                    RecipeItemId = recipeItemId,
                    RequiredLevel = requiredLevel,
                    Materials = materials,
                });

                var baseResults = ReadTokens(rarityNode, content, "base result");
                if (baseResults.Length == 0 || baseResults.Length % 2 != 0)
                    throw new InvalidOperationException($"PVF {PvfPath} [base result] row width is not 2");
                for (var index = 0; index < baseResults.Length; index += 2)
                {
                    var cardItemId = ParseInt(baseResults[index]);
                    var beadItemId = ParseInt(baseResults[index + 1]);
                    DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        beadItemId,
                        out var bead);
                    if (cardItemId <= 0
                        || beadItemId <= 0
                        || config.BeadItemIdByCardItemId.ContainsKey(cardItemId)
                        || (bead != null
                            && bead.MonsterCardId > 0
                            && bead.MonsterCardId != cardItemId))
                    {
                        throw new InvalidOperationException(
                            $"PVF {PvfPath} has an invalid card bead result " +
                            $"card={cardItemId} bead={beadItemId}");
                    }
                    config.BeadItemIdByCardItemId.Add(cardItemId, beadItemId);
                }
            }

            ParseCardDefinitions(
                ReadTokens(root, content, "monstercard bind list"),
                config,
                "monstercard bind list");
        }

        private static void ParseCardDefinitions(
            string[] bindTokens,
            EnchanterConfig config,
            string tag)
        {
            if (bindTokens.Length == 0 || bindTokens.Length % 3 != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] row width is not 3");

            for (var index = 0; index < bindTokens.Length; index += 3)
            {
                var cardItemId = ParseInt(bindTokens[index]);
                var qualification = ParseInt(bindTokens[index + 1]);
                var chance = ParseInt(bindTokens[index + 2]);
                if (cardItemId <= 0
                    || qualification < 0
                    || !config.CardQualificationLevelRequirements.ContainsKey(qualification)
                    || chance < 0
                    || chance > 1000
                    || config.CardsByItemId.ContainsKey(cardItemId)
                    || !DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        cardItemId,
                        out var card)
                    || !string.Equals(
                        NormalizeTag(card.ItemCategory),
                        "monster card",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PVF {PvfPath} has invalid card qualification reference " +
                        $"tag={tag} card={cardItemId} qualification={qualification}");
                }
                config.CardsByItemId.Add(cardItemId, new EnchanterCardDefinition
                {
                    Qualification = qualification,
                    BindChance = chance,
                });
            }
        }

        private static IReadOnlyList<InventoryMaterialRequirement> ParseMaterialRequirements(
            string inputItems)
        {
            var tokens = (inputItems ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                return Array.Empty<InventoryMaterialRequirement>();

            var merged = new Dictionary<int, int>();
            for (var index = 0; index < tokens.Length; index += 2)
            {
                var itemId = ParseInt(tokens[index]);
                var count = ParseInt(tokens[index + 1]);
                if (itemId <= 0
                    || count <= 0
                    || !DfoServer.Game.Inventory.ItemMetadataResolver.TryLoadStackableFile(
                        itemId,
                        out _))
                {
                    return Array.Empty<InventoryMaterialRequirement>();
                }

                merged[itemId] = checked((merged.TryGetValue(itemId, out var current) ? current : 0) + count);
            }

            return merged
                .OrderBy(pair => pair.Key)
                .Select(pair => new InventoryMaterialRequirement(pair.Key, pair.Value))
                .ToArray();
        }

        private static void ParseCardExperienceRules(string[] tokens, EnchanterConfig config)
        {
            const int rowWidth = 9;
            if (tokens.Length == 0 || tokens.Length % rowWidth != 0)
                throw new InvalidOperationException($"PVF {PvfPath} [monstercard exp] row width is not 9");

            for (var index = 0; index < tokens.Length; index += rowWidth)
            {
                var level = ParseInt(tokens[index]);
                var rates = new int[5];
                for (var rateIndex = 0; rateIndex < rates.Length; rateIndex++)
                    rates[rateIndex] = ParseInt(tokens[index + 1 + rateIndex]);
                var rule = new EnchanterCardExperienceRule
                {
                    ExpertJobLevel = level,
                    SuccessRates = rates,
                    ExtraRate = ParseInt(tokens[index + 6]),
                    MinimumExperienceGain = ParseInt(tokens[index + 7]),
                    MaximumExperienceGain = ParseInt(tokens[index + 8]),
                };
                if (level <= 0
                    || config.CardExperienceRulesByLevel.ContainsKey(level)
                    || rates.Any(rate => rate < 0 || rate > 100)
                    || rule.ExtraRate < 0
                    || rule.ExtraRate > 100
                    || rule.MinimumExperienceGain < 0
                    || rule.MaximumExperienceGain < rule.MinimumExperienceGain)
                {
                    throw new InvalidOperationException($"PVF {PvfPath} has invalid monstercard exp rule");
                }
                config.CardExperienceRulesByLevel.Add(level, rule);
            }
        }

        private static int ParseSingleNodeInt(ScriptNode parent, string content, string tag)
        {
            var node = string.Equals(parent.Tag, tag, StringComparison.OrdinalIgnoreCase)
                ? parent
                : parent.GetChild(tag);
            var tokens = node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 1)
                throw new InvalidOperationException($"PVF {PvfPath} [{tag}] must contain one integer");
            return ParseInt(tokens[0]);
        }

        private static int ReadSingleInt(ScriptNode root, string content, string tag)
        {
            var tokens = ReadTokens(root, content, tag);
            return tokens.Length == 0 ? 0 : ParseInt(tokens[0]);
        }

        private static string[] ReadTokens(ScriptNode root, string content, string tag)
        {
            var node = root.Children.FirstOrDefault(child =>
                string.Equals(child.Tag, tag, StringComparison.OrdinalIgnoreCase));
            return node == null
                ? Array.Empty<string>()
                : node.GetFirstDataContent(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static int ParseInt(string value)
            => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static double ParseDouble(string value)
            => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
