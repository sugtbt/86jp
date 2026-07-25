using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    public static class ClearRewardGenerator
    {
        private static readonly float[] DifficultyGoldBonus = { 1.02f, 1.38f, 1.60f, 1.90f, 2.0f };

        public struct CardReward
        {
            public bool IsGold;
            public int GoldAmount;
            public int ItemId;
            public int StackCount;
            public bool IsEquipment;
            public ushort Durability;
        }

        public static CardReward GenerateGoldCard(int dungeonLevel, int difficulty, DnfLcg lcg)
        {
            int baseGold = ExpTableProvider.GetMonsterGold(dungeonLevel, out int variancePct);
            int goldBase = baseGold * 175 / 1000;

            float diffBonus = difficulty >= 0 && difficulty < DifficultyGoldBonus.Length
                ? DifficultyGoldBonus[difficulty] : 1.0f;
            int goldAmount = (int)(goldBase * diffBonus);

            if (variancePct > 0 && goldAmount > 0)
            {
                int variance = (lcg.Next(2 * variancePct + 1) - variancePct) * goldAmount / 100;
                goldAmount += variance;
            }

            return new CardReward { IsGold = true, GoldAmount = Math.Max(1, goldAmount) };
        }

        public static CardReward GenerateItemCard(int dungeonLevel, int difficulty, DnfLcg lcg)
        {
            int rarity = RollClearRewardRarity(lcg);
            int itemId = MonsterDropConfig.ChooseEquipment(lcg, dungeonLevel, rarity);
            bool isEquip = itemId > 0;
            ushort durability = 0;
            if (isEquip)
                durability = ItemMetadataResolver.Resolve(itemId).Durability;

            if (itemId <= 0)
                itemId = MonsterDropConfig.ChooseStackable(lcg, dungeonLevel, rarity);

            if (itemId <= 0)
                return new CardReward { IsGold = true, GoldAmount = 100 };

            return new CardReward
            {
                IsGold = false,
                ItemId = itemId,
                StackCount = 1,
                IsEquipment = isEquip,
                Durability = durability
            };
        }

        public static CardReward GenerateEquipmentCard(int dungeonLevel, int difficulty, DnfLcg lcg)
        {
            int rarity = RollClearRewardRarity(lcg);
            int itemId = MonsterDropConfig.ChooseEquipment(lcg, dungeonLevel, rarity);
            if (itemId <= 0)
            {
                for (var fallbackRarity = 0; fallbackRarity <= 6; fallbackRarity++)
                {
                    if (fallbackRarity == rarity)
                        continue;

                    itemId = MonsterDropConfig.ChooseEquipment(lcg, dungeonLevel, fallbackRarity);
                    if (itemId > 0)
                        break;
                }
            }

            if (itemId <= 0)
            {
                FileLogger.Log($"[ClearRewardGenerator] paid card equipment pool missing level={dungeonLevel} diff={difficulty}");
                return default;
            }

            return new CardReward
            {
                IsGold = false,
                ItemId = itemId,
                StackCount = 1,
                IsEquipment = true,
                Durability = ItemMetadataResolver.Resolve(itemId).Durability
            };
        }

        private static int RollClearRewardRarity(DnfLcg lcg)
        {
            int roll = lcg.Next(1000000) + 1;
            if (roll <= 500000) return 0;
            if (roll <= 799900) return 1;
            if (roll <= 999900) return 2;
            return 3;
        }
    }
}
