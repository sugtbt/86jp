using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct TowerOfDespairGrantedReward
    {
        internal TowerOfDespairGrantedReward(
            ClearRewardGenerator.CardReward reward,
            short slot)
        {
            Reward = reward;
            Slot = slot;
        }

        internal ClearRewardGenerator.CardReward Reward { get; }
        internal short Slot { get; }
    }

    internal sealed class TowerOfDespairRewardGrantService
    {
        private readonly IAssetService _assetService;

        internal TowerOfDespairRewardGrantService(IAssetService assetService)
        {
            _assetService = assetService
                ?? throw new ArgumentNullException(nameof(assetService));
        }

        internal IReadOnlyList<TowerOfDespairGrantedReward> Grant(
            int characterId,
            int accountId,
            IReadOnlyList<ClearRewardGenerator.CardReward> candidates)
        {
            if (characterId <= 0 || candidates == null || candidates.Count == 0)
                return Array.Empty<TowerOfDespairGrantedReward>();

            var granted =
                new List<TowerOfDespairGrantedReward>(candidates.Count);
            try
            {
                using (var scope = _assetService.OpenScope(
                    characterId,
                    accountId))
                {
                    foreach (var reward in candidates)
                    {
                        if (reward.IsGold
                            || reward.ItemId <= 0
                            || reward.StackCount <= 0
                            || !_assetService.TryAddItem(
                                scope,
                                reward.ItemId,
                                reward.StackCount,
                                out var slot))
                        {
                            continue;
                        }

                        granted.Add(
                            new TowerOfDespairGrantedReward(reward, slot));
                    }

                    scope.Commit();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TowerOfDespair] reward grant rolled back: " +
                    $"cid={characterId} error={ex.Message}");
                return Array.Empty<TowerOfDespairGrantedReward>();
            }

            return granted;
        }
    }
}
