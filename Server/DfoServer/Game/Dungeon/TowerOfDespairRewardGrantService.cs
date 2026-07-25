using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct TowerOfDespairGrantedReward
    {
        internal TowerOfDespairGrantedReward(
            ClearRewardGenerator.CardReward reward,
            InventoryListType listType,
            short slot)
        {
            Reward = reward;
            ListType = listType;
            Slot = slot;
        }

        internal ClearRewardGenerator.CardReward Reward { get; }
        internal InventoryListType ListType { get; }
        internal short Slot { get; }
    }

    internal sealed class TowerOfDespairRewardGrantService
    {
        internal IReadOnlyList<TowerOfDespairGrantedReward> Grant(
            InventoryService inventory,
            IReadOnlyList<ClearRewardGenerator.CardReward> candidates)
        {
            if (inventory == null
                || candidates == null
                || candidates.Count == 0)
                return Array.Empty<TowerOfDespairGrantedReward>();

            var requests =
                new List<InventoryRewardGrantRequest>(candidates.Count);
            foreach (var reward in candidates)
            {
                if (reward.IsGold
                    || reward.ItemId <= 0
                    || reward.StackCount <= 0)
                {
                    continue;
                }

                requests.Add(InventoryRewardGrantRequest.Create(
                    reward.ItemId,
                    reward.StackCount,
                    ItemCreateReason.DungeonDrop));
            }
            if (requests.Count == 0)
                return Array.Empty<TowerOfDespairGrantedReward>();

            try
            {
                if (!InventoryRewardGrantService.TryGrantBatch(
                        inventory,
                        requests,
                        out var batch)
                    || !batch.Success
                    || batch.Results.Count != requests.Count)
                {
                    FileLogger.Log(
                        $"[TowerOfDespair] reward batch rejected: " +
                        $"cid={inventory.CharacterId} error={batch?.Error}");
                    return Array.Empty<TowerOfDespairGrantedReward>();
                }

                var granted =
                    new List<TowerOfDespairGrantedReward>(batch.Results.Count);
                for (var index = 0; index < batch.Results.Count; index++)
                {
                    var result = batch.Results[index];
                    if (!result.Success || result.SlotIndex < 0)
                        continue;

                    granted.Add(new TowerOfDespairGrantedReward(
                        new ClearRewardGenerator.CardReward
                        {
                            ItemId = result.ItemTemplateId,
                            StackCount = result.GrantedCount,
                        },
                        result.ListType,
                        result.SlotIndex));
                }

                return granted;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TowerOfDespair] reward batch failed: " +
                    $"cid={inventory.CharacterId} error={ex.Message}");
                return Array.Empty<TowerOfDespairGrantedReward>();
            }
        }
    }
}
