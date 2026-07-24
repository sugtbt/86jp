using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryCompoundItemRecipe(
            int characterId,
            int accountId,
            CompoundItemRecipeRequest request,
            out CompoundItemRecipeResult result)
        {
            result = new CompoundItemRecipeResult
            {
                RequestedCount = request != null ? request.RequestedCount : (ushort)0,
            };

            if (request == null || request.RequestedCount == 0)
            {
                result.ErrorCode = 17;
                return false;
            }

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var sourceItemId = request.SourceValue;
                    ItemRecord source = null;
                    if (!request.SourceIsItemId)
                    {
                        if (request.SourceValue < short.MinValue || request.SourceValue > short.MaxValue)
                        {
                            result.ErrorCode = 17;
                            return false;
                        }

                        source = _db.LoadItemRecord(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Main,
                            (short)request.SourceValue);
                        if (source == null)
                        {
                            result.ErrorCode = 17;
                            return false;
                        }

                        sourceItemId = source.ItemTemplateId;
                        result.SourceSlotIndex = source.SlotIndex;

                        if (IsStackCountedRecord(source) && GetStackedRecordCount(source) < request.RequestedCount)
                        {
                            result.ErrorCode = 17;
                            return false;
                        }
                    }

                    result.SourceItemTemplateId = sourceItemId;
                    if (!TryParseCompoundRecipe(sourceItemId, out var recipe))
                    {
                        result.ErrorCode = 17;
                        return false;
                    }

                    result.PvfPath = recipe.PvfPath;
                    result.RecipeType = recipe.RecipeType;

                    var materials = MultiplyRecipeEntries(recipe.Materials, request.RequestedCount);
                    var outputs = MultiplyRecipeEntries(recipe.Outputs, request.RequestedCount);
                    if (outputs.Count == 0)
                    {
                        result.ErrorCode = 17;
                        return false;
                    }

                    var currentItems = _db.LoadItemsByListType(connection, transaction, characterId, InventoryListType.Main);
                    if (!HasEnoughMaterials(currentItems, materials))
                    {
                        result.ErrorCode = 21;
                        return false;
                    }

                    var deleted = new List<CompoundItemDeletedEntry>();
                    foreach (var material in materials)
                    {
                        if (!DeleteRecipeMaterial(connection, transaction, characterId, currentItems, material, deleted))
                        {
                            result.ErrorCode = 17;
                            return false;
                        }
                    }

                    if (!request.SourceIsItemId)
                    {
                        if (!TryDeleteItemCore(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Main,
                            InventoryListType.Main,
                            source.SlotIndex,
                            (short)request.RequestedCount,
                            out _))
                        {
                            result.ErrorCode = 17;
                            return false;
                        }

                        deleted.Insert(0, new CompoundItemDeletedEntry
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = source.SlotIndex,
                            Count = request.RequestedCount,
                            ItemTemplateId = source.ItemTemplateId,
                        });
                        result.SourceConsumed = true;
                    }

                    var totalGoldCost = recipe.GoldCost * (int)request.RequestedCount;
                    if (totalGoldCost > 0)
                    {
                        if (!CurrencyService.TrySpendGold(connection, transaction, characterId, totalGoldCost))
                        {
                            result.ErrorCode = 22;
                            return false;
                        }

                        result.GoldSpent = totalGoldCost;
                        result.UpdatedGold = ReadCharacterGold(connection, transaction, characterId);
                    }

                    foreach (var output in outputs)
                    {
                        if (!_db.TryAddBoosterRewardItems(
                                connection,
                                transaction,
                                characterId,
                                accountId,
                                output.ItemTemplateId,
                                output.Count,
                                out var rewards)
                            || rewards.Count == 0)
                        {
                            result.ErrorCode = 4;
                            return false;
                        }

                        result.Rewards.AddRange(rewards);
                    }

                    result.DeletedEntries.AddRange(deleted);
                    transaction.Commit();
                    result.ErrorCode = 0;
                    return true;
                }
            }
        }

        private static bool TryParseCompoundRecipe(int itemTemplateId, out CompoundItemRecipeDefinition recipe)
        {
            recipe = null;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out StackableItemFile stackable)
                || stackable == null)
                return false;

            var stackableType = NormalizeRecipeTag(stackable.StackableType);
            if (!stackableType.Equals("[recipe]", StringComparison.OrdinalIgnoreCase))
                return false;

            var values = ParseRecipeIntList(stackable.IntData);
            var materials = new List<CompoundItemRecipeEntry>();
            var outputs = new List<CompoundItemRecipeEntry>();

            if (values.Count >= 1)
            {
                var pos = 0;
                var materialCount = values[pos++];
                if (materialCount < 0 || values.Count < pos + materialCount * 2)
                    return false;

                for (var index = 0; index < materialCount; index++)
                    materials.Add(new CompoundItemRecipeEntry(values[pos++], values[pos++]));

                if (pos < values.Count)
                {
                    var outputCount = values[pos++];
                    if (outputCount < 0 || values.Count < pos + outputCount * 2)
                        return false;

                    for (var index = 0; index < outputCount; index++)
                        outputs.Add(new CompoundItemRecipeEntry(values[pos++], values[pos++]));
                }
            }
            else
            {
                // IntData 为空，回退解析 [input item]/[output item]（生产 stk）
                materials = ParseInputOutputEntries(stackable.InputItem);
                outputs = ParseInputOutputEntries(stackable.OutputItem);
                if (materials.Count == 0 || outputs.Count == 0)
                    return false;
            }

            var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
            var goldCost = ParseGoldCostFromInputItem(stackable.InputItem);
            recipe = new CompoundItemRecipeDefinition
            {
                PvfPath = entry?.FilePath ?? string.Empty,
                RecipeType = ResolveRecipeType(stackable),
                Materials = materials,
                Outputs = outputs,
                GoldCost = goldCost,
            };
            return true;
        }

        private static List<CompoundItemRecipeEntry> MultiplyRecipeEntries(
            IReadOnlyList<CompoundItemRecipeEntry> entries,
            ushort requestedCount)
        {
            var merged = new Dictionary<int, int>();
            if (entries == null)
                return new List<CompoundItemRecipeEntry>();

            foreach (var entry in entries)
            {
                var count = checked(entry.Count * (int)requestedCount);
                if (entry.ItemTemplateId <= 0 || count <= 0)
                    continue;

                if (!merged.ContainsKey(entry.ItemTemplateId))
                    merged[entry.ItemTemplateId] = 0;
                merged[entry.ItemTemplateId] = checked(merged[entry.ItemTemplateId] + count);
            }

            return merged
                .OrderBy(pair => pair.Key)
                .Select(pair => new CompoundItemRecipeEntry(pair.Key, pair.Value))
                .ToList();
        }

        private static bool HasEnoughMaterials(
            IReadOnlyList<ItemRecord> currentItems,
            IReadOnlyList<CompoundItemRecipeEntry> materials)
        {
            foreach (var material in materials)
            {
                var total = 0;
                foreach (var item in currentItems)
                {
                    if (item.ItemTemplateId == material.ItemTemplateId)
                        total += Math.Max(0, item.StackCount);
                }

                if (total < material.Count)
                    return false;
            }

            return true;
        }

        private bool DeleteRecipeMaterial(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            IReadOnlyList<ItemRecord> currentItems,
            CompoundItemRecipeEntry material,
            List<CompoundItemDeletedEntry> deleted)
        {
            var remaining = material.Count;
            foreach (var item in currentItems
                         .Where(candidate => candidate.ItemTemplateId == material.ItemTemplateId)
                         .OrderBy(candidate => candidate.SlotIndex))
            {
                if (remaining <= 0)
                    return true;

                var remove = Math.Min(remaining, Math.Max(0, item.StackCount));
                if (remove <= 0)
                    continue;

                if (!TryDeleteItemCore(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Main,
                        InventoryListType.Main,
                        item.SlotIndex,
                        (short)remove,
                        out _))
                    return false;

                deleted.Add(new CompoundItemDeletedEntry
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = item.SlotIndex,
                    Count = remove,
                    ItemTemplateId = item.ItemTemplateId,
                });
                remaining -= remove;
            }

            return remaining <= 0;
        }

        private static List<CompoundItemRecipeEntry> ParseInputOutputEntries(string text)
        {
            var entries = new List<CompoundItemRecipeEntry>();
            var values = ParseRecipeIntList(text);
            for (var i = 0; i + 1 < values.Count; i += 2)
            {
                if (values[i] > 0 && values[i + 1] > 0)
                    entries.Add(new CompoundItemRecipeEntry(values[i], values[i + 1]));
            }

            return entries;
        }

        private static int ParseGoldCostFromInputItem(string text)
        {
            var values = ParseRecipeIntList(text);
            var totalGold = 0;
            for (var i = 0; i + 3 < values.Count; i += 4)
            {
                // [input item] 格式: itemId count goldId goldAmount
                if (values[i + 2] == 0 && values[i + 3] > 0)
                    totalGold += values[i + 3];
            }

            return totalGold;
        }

        private static List<int> ParseRecipeIntList(string text)
        {
            var values = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return values;

            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            return values;
        }

        private static string ResolveRecipeType(StackableItemFile stackable)
        {
            if (stackable?.StringDataItems != null && stackable.StringDataItems.Count > 0)
                return string.Join(",", stackable.StringDataItems.Select(NormalizeRecipeTag));

            return NormalizeRecipeTag(stackable?.StringData);
        }

        private static int ReadCharacterGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = 0 LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                return result is long l ? (int)l : (result is int i ? i : 0);
            }
        }

        private static string NormalizeRecipeTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var trimmed = text.Trim();
            var first = trimmed.IndexOf('`');
            if (first >= 0)
            {
                var second = trimmed.IndexOf('`', first + 1);
                if (second > first)
                    return trimmed.Substring(first + 1, second - first - 1).Trim();
            }

            return trimmed.Replace("`", string.Empty).Trim();
        }
    }
}
