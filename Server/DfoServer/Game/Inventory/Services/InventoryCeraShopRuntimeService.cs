using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.Shop;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PremiumService = DfoServer.Game.Premium.PremiumService;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryCeraShopRuntimeService
    {
        private enum CeraPayMode
        {
            Default,
            OnlyCera,
            OnlyCeraPoint,
        }

        private struct CeraShopPaymentPlan
        {
            public bool Ok;
            public int NewGold;
            public int NewCera;
            public int NewTokenCera;
            public int NewHappyTokenCera;
            public int SpentCera;
            public int SpentTokenCera;
            public int SpentHappyTokenCera;
        }

        private static readonly Dictionary<int, Dictionary<int, int>> AvatarCouponTable = new Dictionary<int, Dictionary<int, int>>
        {
            { 1, new Dictionary<int, int> { { 1, 2681588 }, { 2, 2681589 }, { 3, 2681590 } } },
            { 2, new Dictionary<int, int> { { 1, 2681591 }, { 2, 2681592 }, { 3, 2681593 } } },
            { 3, new Dictionary<int, int> { { 3, 2681594 } } },
        };

        private static readonly int[] AccountCargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64 };
        private static readonly ushort[] PersonalCargoCapacityTiers =
        {
            24, 40, 56, 72, 88, 104, 120, 136, 152
        };

        internal static bool TryBuyCeraShopItem(
            InventoryService inventory,
            int accountId,
            int productId,
            int buyCount,
            int paymentMode,
            byte attributeValue,
            out InventoryMutationResult result,
            out bool handled)
        {
            result = null;
            handled = true;

            if (inventory == null || accountId <= 0 || productId <= 0)
                return false;

            if (!CeraShopProductCatalog.TryResolve(productId, out var product)
                || product == null
                || product.ItemTemplateId <= 0)
                return false;

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(product.ItemTemplateId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] resolve metadata failed product=0x{productId:X8} item=0x{product.ItemTemplateId:X8}: {ex.Message}");
                return false;
            }

            var itemTemplateId = product.ItemTemplateId;
            var isSkillTreeExpansion = itemTemplateId == SkillTreeExpansionState.ExpansionItemTemplateId;
            var isNameTag = metadata != null
                && !string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase)
                && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsNameTagItem(itemTemplateId);

            if (metadata == null)
                return false;

            byte itemKind = 0;
            if (!isSkillTreeExpansion
                && !ItemMetadataResolver.TryResolveItemKind(itemTemplateId, metadata, out itemKind))
                return false;

            var isAvatar = !isSkillTreeExpansion
                && (string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase)
                    || itemKind == ItemCore.KindAvatar);
            var effectiveCount = metadata.IsStackable && !isAvatar
                ? InventoryCeraShopStackPolicy.NormalizeEffectiveStackCount(buyCount, product.Count, metadata.StackLimit)
                : 1;
            if (effectiveCount <= 0)
                return false;

            if (!TryResolveCosts(
                    product,
                    metadata,
                    isAvatar,
                    buyCount,
                    out var avatarDurationDays,
                    out var totalGoldCost,
                    out var totalCeraCost))
                return false;

            var ceraMode = ResolveCeraPayMode(itemTemplateId);
            var couponId = 0;
            if (paymentMode == 1)
            {
                if (!isAvatar || !TryGetAvatarCouponId(metadata.Grade, Math.Max(1, product.Count), out couponId))
                    return false;

                totalCeraCost = 0;
            }
            else if (paymentMode != 0)
            {
                return false;
            }

            if (!CanSpendInventoryCosts(inventory, totalGoldCost, couponId))
                return false;

            if (isSkillTreeExpansion)
                return TryBuySkillTreeExpansion(
                    inventory,
                    itemTemplateId,
                    buyCount,
                    paymentMode,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result);

            if (isNameTag)
                return TryBuyNameTag(
                    inventory,
                    product,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result);

            if (string.Equals(metadata.ItemKind, "special", StringComparison.Ordinal))
                return false;

            if (TryResolvePersonalCargoUpgradeTarget(itemTemplateId, out var personalCargoTarget))
                return TryBuyPersonalCargoUpgrade(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    personalCargoTarget,
                    out result);

            if (InventoryCargoUpgradeRule.IsAccountCargoUpgradeToolItem(itemTemplateId))
                return TryBuyAccountCargoUpgrade(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result);

            if (PremiumService.IsContractItem(itemTemplateId))
                return TryBuyConsumedPremium(
                    inventory,
                    itemTemplateId,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    couponId,
                    out result);

            return TryBuyInventoryReward(
                inventory,
                product,
                metadata,
                effectiveCount,
                totalGoldCost,
                totalCeraCost,
                ceraMode,
                couponId,
                isAvatar,
                attributeValue,
                avatarDurationDays,
                out result);
        }

        private static bool TryBuySkillTreeExpansion(
            InventoryService inventory,
            int itemTemplateId,
            int buyCount,
            int paymentMode,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result)
        {
            result = null;
            if (buyCount != 1 || paymentMode != 0 || couponId > 0)
                return false;

            if (!TrySpendPaymentAndApplyDbAction(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    (connection, transaction) =>
                        SkillTreeExpansionService.TryUnlock(
                            connection,
                            transaction,
                            inventory.CharacterId),
                    out var payment))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
                ConsumedOnPurchase = true,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            FileLogger.Log($"[CeraShopRuntime] skill-tree expansion unlocked cid={inventory.CharacterId} item=0x{itemTemplateId:X8}");
            return true;
        }

        private static bool TryBuyNameTag(
            InventoryService inventory,
            CeraShopProductEntry product,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result)
        {
            result = null;
            if (inventory == null || product == null || couponId > 0)
                return false;

            var durationDays = Math.Max(0, product.DurationDays);
            var unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expireTime = durationDays > 0
                ? (int)Math.Min(int.MaxValue, unixNow + (long)durationDays * 86400L)
                : 0;

            if (!TrySpendPaymentAndApplyDbAction(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    ceraMode,
                    (connection, transaction) =>
                    {
                        NameTagStateRepository.Upsert(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            product.ItemTemplateId,
                            expireTime);
                        return true;
                    },
                    out var payment))
                return false;

            inventory.NameTag.Set(product.ItemTemplateId, expireTime);
            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = -1,
                ItemTemplateId = product.ItemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
                ConsumedOnPurchase = true,
                NameTagEquipped = true,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            FileLogger.Log($"[CeraShopRuntime] name tag equipped cid={inventory.CharacterId} item=0x{product.ItemTemplateId:X8} expire={expireTime}");
            return true;
        }

        private static bool TryBuyInventoryReward(
            InventoryService inventory,
            CeraShopProductEntry product,
            ItemMetadata metadata,
            int effectiveCount,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            bool isAvatar,
            byte attributeValue,
            int avatarDurationDays,
            out InventoryMutationResult result)
        {
            result = null;
            var requests = BuildGrantRequests(
                inventory.CharacterId,
                product,
                metadata,
                effectiveCount,
                isAvatar,
                attributeValue,
                avatarDurationDays);
            if (requests.Count == 0)
                return false;

            if (!InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                || plan == null
                || !plan.Success)
                return false;

            if (!TrySpendPayment(inventory, totalGoldCost, totalCeraCost, ceraMode, out var payment))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var grant)
                || grant == null
                || !grant.Success
                || grant.Results.Count == 0)
                return false;

            result = ToMutationResult(inventory, grant.Results[0], payment, effectiveCount, totalGoldCost > 0);
            for (var index = 1; index < grant.Results.Count; index++)
                result.ExtraResults.Add(ToMutationResult(inventory, grant.Results[index], payment, effectiveCount, false));
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyConsumedPremium(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result)
        {
            result = null;
            if (!TrySpendPayment(inventory, totalGoldCost, totalCeraCost, ceraMode, out var payment))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            result = new InventoryMutationResult
            {
                ItemTemplateId = itemTemplateId,
                ConsumedOnPurchase = true,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyPersonalCargoUpgrade(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            ushort targetCapacity,
            out InventoryMutationResult result)
        {
            result = null;
            var current = inventory.Cargo.Capacity;
            if (!IsPersonalCargoCapacityTier(targetCapacity) || targetCapacity <= current)
                return false;

            if (!TrySpendPayment(inventory, totalGoldCost, totalCeraCost, ceraMode, out var payment))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            inventory.SetListParam16(InventoryListType.PersonalCargo, targetCapacity);
            result = new InventoryMutationResult
            {
                ListType = InventoryListType.PersonalCargo,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = targetCapacity,
                InstanceValue = targetCapacity,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                ConsumedOnPurchase = true,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static bool TryBuyAccountCargoUpgrade(
            InventoryService inventory,
            int itemTemplateId,
            int totalGoldCost,
            int totalCeraCost,
            CeraPayMode ceraMode,
            int couponId,
            out InventoryMutationResult result)
        {
            result = null;
            if (!TryGetNextAccountCargoCapacity(inventory.AccountCargo.SelectionKey, out var nextCapacity))
                return false;

            if (!TrySpendPayment(inventory, totalGoldCost, totalCeraCost, ceraMode, out var payment))
                return false;

            var costMutations = new List<InventoryMutationResult>();
            if (!ApplyInventoryCosts(inventory, totalGoldCost, couponId, costMutations))
                return false;

            inventory.SetListParam16(InventoryListType.AccountCargo, (ushort)nextCapacity);
            result = new InventoryMutationResult
            {
                ListType = InventoryListType.AccountCargo,
                SlotIndex = -1,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = nextCapacity,
                InstanceValue = nextCapacity,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = totalGoldCost > 0,
                ConsumedOnPurchase = true,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            foreach (var cost in costMutations)
                result.ExtraResults.Add(cost);

            return true;
        }

        private static List<InventoryRewardGrantRequest> BuildGrantRequests(
            int characterId,
            CeraShopProductEntry product,
            ItemMetadata metadata,
            int effectiveCount,
            bool isAvatar,
            byte attributeValue,
            int avatarDurationDays)
        {
            var requests = new List<InventoryRewardGrantRequest>();
            if (TryResolveMallAutoOpenRewards(characterId, product.ItemTemplateId, effectiveCount, out var rewards))
                return InventorySpecialConsumableService.BuildRewardRequests(rewards);

            requests.Add(InventoryRewardGrantRequest.Create(
                product.ItemTemplateId,
                effectiveCount,
                ItemCreateReason.MallPurchase,
                CreateOptions(isAvatar, attributeValue, avatarDurationDays)));
            return requests;
        }

        private static bool TryResolveMallAutoOpenRewards(
            int characterId,
            int itemTemplateId,
            int effectiveCount,
            out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = null;
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (!InventoryPackageRewardResolver.TryResolveMallAutoOpenRewards(
                                connection,
                                transaction,
                                characterId,
                                itemTemplateId,
                                out var unitRewards)
                            || unitRewards == null
                            || unitRewards.Count == 0)
                            return false;

                        var allRewards = new List<PvfLib.BoosterRewardEntry>();
                        var repeat = Math.Max(1, effectiveCount);
                        for (var index = 0; index < repeat; index++)
                            allRewards.AddRange(unitRewards);

                        rewards = InventoryPackageRewardResolver.AggregateRewards(allRewards);
                        transaction.Commit();
                        return rewards.Count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] resolve mall auto-open rewards failed item=0x{itemTemplateId:X8}: {ex.Message}");
                return false;
            }
        }

        private static bool TryResolveCosts(
            CeraShopProductEntry product,
            ItemMetadata metadata,
            bool isAvatar,
            int buyCount,
            out int avatarDurationDays,
            out int totalGoldCost,
            out int totalCeraCost)
        {
            avatarDurationDays = 0;
            totalGoldCost = 0;
            totalCeraCost = 0;

            var goldPrice = Math.Max(0, product.GoldPrice);
            var ceraPrice = Math.Max(0, product.CoinPrice);
            if (isAvatar)
            {
                goldPrice = 0;
                if (AvatarTypeSelectResolver.TryGetOption(
                        product.ItemTemplateId,
                        Math.Max(1, product.Count),
                        out var durationDays,
                        out var avatarPrice))
                {
                    avatarDurationDays = durationDays;
                    if (avatarPrice > 0)
                        ceraPrice = avatarPrice;
                }
            }

            var perUnit = Math.Max(1, buyCount);
            var gold = (long)goldPrice * perUnit;
            var cera = (long)ceraPrice * perUnit;
            if (gold > int.MaxValue || cera > int.MaxValue)
                return false;

            totalGoldCost = (int)gold;
            totalCeraCost = (int)cera;
            return true;
        }

        private static bool TrySpendPayment(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            out CeraShopPaymentPlan plan)
        {
            plan = default;
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, inventory.CharacterId);
                        wallet.Gold = inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                        plan = ComputePayment(wallet, goldCost, ceraCost, mode);
                        if (!plan.Ok)
                            return false;

                        if (plan.SpentHappyTokenCera > 0
                            && !CurrencyService.TrySpendHappyTokenCera(connection, transaction, inventory.CharacterId, plan.SpentHappyTokenCera))
                            return false;
                        if (plan.SpentTokenCera > 0
                            && !CurrencyService.TrySpendTokenCera(connection, transaction, inventory.CharacterId, plan.SpentTokenCera))
                            return false;
                        if (plan.SpentCera > 0
                            && !CurrencyService.TrySpendCera(connection, transaction, inventory.CharacterId, plan.SpentCera))
                            return false;

                        transaction.Commit();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] payment failed cid={inventory?.CharacterId ?? 0} gold={goldCost} cera={ceraCost}: {ex.Message}");
                return false;
            }
        }

        private static bool TrySpendPaymentAndApplyDbAction(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            CeraPayMode mode,
            Func<SqliteConnection, SqliteTransaction, bool> action,
            out CeraShopPaymentPlan plan)
        {
            plan = default;
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, inventory.CharacterId);
                        wallet.Gold = inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                        plan = ComputePayment(wallet, goldCost, ceraCost, mode);
                        if (!plan.Ok)
                            return false;

                        if (action != null && !action(connection, transaction))
                            return false;

                        if (!ApplyCeraPayment(connection, transaction, inventory.CharacterId, plan))
                            return false;

                        transaction.Commit();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CeraShopRuntime] special purchase payment failed cid={inventory?.CharacterId ?? 0} gold={goldCost} cera={ceraCost}: {ex.Message}");
                return false;
            }
        }

        private static bool ApplyCeraPayment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            CeraShopPaymentPlan plan)
        {
            if (plan.SpentHappyTokenCera > 0
                && !CurrencyService.TrySpendHappyTokenCera(connection, transaction, characterId, plan.SpentHappyTokenCera))
                return false;
            if (plan.SpentTokenCera > 0
                && !CurrencyService.TrySpendTokenCera(connection, transaction, characterId, plan.SpentTokenCera))
                return false;
            if (plan.SpentCera > 0
                && !CurrencyService.TrySpendCera(connection, transaction, characterId, plan.SpentCera))
                return false;

            return true;
        }

        private static CeraShopPaymentPlan ComputePayment(WalletSnapshot wallet, int goldCost, int ceraCost, CeraPayMode mode)
        {
            var plan = new CeraShopPaymentPlan
            {
                Ok = false,
                NewGold = wallet.Gold,
                NewCera = wallet.Cera,
                NewTokenCera = wallet.TokenCera,
                NewHappyTokenCera = wallet.HappyTokenCera,
            };

            if (goldCost > 0)
            {
                if (wallet.Gold < goldCost)
                    return plan;
                plan.NewGold = wallet.Gold - goldCost;
            }

            if (ceraCost > 0)
            {
                var useHappy = mode != CeraPayMode.OnlyCera;
                var useToken = mode != CeraPayMode.OnlyCera;
                var useCera = mode != CeraPayMode.OnlyCeraPoint;
                var remaining = ceraCost;
                var happy = plan.NewHappyTokenCera;
                var token = plan.NewTokenCera;
                var cera = plan.NewCera;

                if (useHappy && remaining > 0)
                {
                    var spent = Math.Min(remaining, happy);
                    happy -= spent;
                    remaining -= spent;
                    plan.SpentHappyTokenCera = spent;
                }
                if (useToken && remaining > 0)
                {
                    var spent = Math.Min(remaining, token);
                    token -= spent;
                    remaining -= spent;
                    plan.SpentTokenCera = spent;
                }
                if (useCera && remaining > 0)
                {
                    var spent = Math.Min(remaining, cera);
                    cera -= spent;
                    remaining -= spent;
                    plan.SpentCera = spent;
                }
                if (remaining > 0)
                    return plan;

                plan.NewHappyTokenCera = happy;
                plan.NewTokenCera = token;
                plan.NewCera = cera;
            }

            plan.Ok = true;
            return plan;
        }

        private static bool CanSpendInventoryCosts(InventoryService inventory, int goldCost, int couponId)
        {
            if (inventory == null || goldCost < 0)
                return false;

            if (goldCost > 0 && inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart) < goldCost)
                return false;

            return couponId <= 0 || inventory.CountMainItem(couponId) >= 1;
        }

        private static bool ApplyInventoryCosts(
            InventoryService inventory,
            int goldCost,
            int couponId,
            List<InventoryMutationResult> costMutations)
        {
            if (goldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(
                        InventoryService.MainVirtualCurrencySlotStart,
                        goldCost,
                        out var gold)
                    || !gold.Success)
                    return false;
            }

            if (couponId > 0)
            {
                if (!inventory.TryConsumeMainItem(couponId, 1, out var coupon) || !coupon.Success)
                    return false;

                costMutations?.Add(new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = coupon.SlotIndex,
                    ItemTemplateId = couponId,
                    RemainingStackCount = coupon.RemainingCount,
                    InstanceValue = coupon.RemainingCount,
                    CostItemTemplateId = couponId,
                    CostItemNewStackCount = coupon.RemainingCount,
                    CostItemSlotIndex = coupon.SlotIndex,
                    RequestedCount = 1,
                    AppliedCount = 1,
                });
            }

            return true;
        }

        private static InventoryMutationResult ToMutationResult(
            InventoryService inventory,
            InventoryRewardGrantResult grant,
            CeraShopPaymentPlan payment,
            int requestedCount,
            bool goldSpent)
        {
            var result = new InventoryMutationResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                UpdatedGold = payment.NewGold,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                GoldSpent = goldSpent,
                RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(1, requestedCount)),
                AppliedCount = (short)Math.Min(short.MaxValue, Math.Max(1, grant.GrantedCount)),
            };

            if (grant.Kind == InventoryRewardGrantKind.Premium)
            {
                result.ConsumedOnPurchase = true;
                return result;
            }

            if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
            {
                result.RemainingStackCount = grant.FinalCount;
                result.InstanceValue = grant.FinalCount;
                return result;
            }

            var core = grant.SlotIndex >= 0
                ? inventory.GetItem(grant.ListType, grant.SlotIndex)
                : null;
            if (core == null)
                core = grant.Core;

            if (core != null)
            {
                result.ItemTemplateId = core.ItemId;
                result.RemainingStackCount = InventoryStackRuleService.IsStackable(core)
                    ? core.Count
                    : Math.Max(1, grant.GrantedCount);
                result.InstanceValue = core.Value;
                result.Durability = core.Durability;
                result.ExtData0 = core.Attr;
                result.ExpireTime = core.ExpireTime;
            }

            return result;
        }

        private static InventoryCreateOptions CreateOptions(bool isAvatar, byte attributeValue, int avatarDurationDays)
        {
            if (!isAvatar && avatarDurationDays <= 0 && attributeValue == 0)
                return null;

            var expireTime = avatarDurationDays > 0
                ? PvfExpirationMetadata.AddDaysFromNow(avatarDurationDays)
                : 0;
            return new InventoryCreateOptions
            {
                AvatarAbilityNo = isAvatar ? attributeValue : (ushort)0,
                ExpireTime = expireTime,
            };
        }

        private static CeraPayMode ResolveCeraPayMode(int itemTemplateId)
        {
            if (CeraShopProductCatalog.IsBuyOnlyCera(itemTemplateId))
                return CeraPayMode.OnlyCera;
            if (CeraShopProductCatalog.IsBuyOnlyCeraPoint(itemTemplateId))
                return CeraPayMode.OnlyCeraPoint;
            return CeraPayMode.Default;
        }

        private static bool TryGetAvatarCouponId(int grade, int durIndex, out int couponId)
        {
            couponId = 0;
            if (grade == 3)
            {
                if (durIndex != 3)
                    return false;

                couponId = 2681594;
                return true;
            }

            return AvatarCouponTable.TryGetValue(grade, out var durMap)
                && durMap.TryGetValue(durIndex, out couponId);
        }

        private static bool TryResolvePersonalCargoUpgradeTarget(int itemTemplateId, out ushort targetCapacity)
        {
            return InventoryCargoUpgradeRule.TryResolvePersonalCargoUpgradeTarget(itemTemplateId, out targetCapacity);
        }

        private static bool IsPersonalCargoCapacityTier(ushort capacity)
        {
            for (var index = 0; index < PersonalCargoCapacityTiers.Length; index++)
                if (PersonalCargoCapacityTiers[index] == capacity)
                    return true;

            return false;
        }

        private static bool TryGetNextAccountCargoCapacity(ushort current, out int next)
        {
            next = 0;
            var index = Array.IndexOf(AccountCargoCapacityTiers, (int)current);
            if (index < 0 || index + 1 >= AccountCargoCapacityTiers.Length)
                return false;

            next = AccountCargoCapacityTiers[index + 1];
            return true;
        }

    }
}
