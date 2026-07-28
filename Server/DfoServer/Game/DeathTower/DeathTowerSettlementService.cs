using System;
using System.Collections.Generic;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.DeathTower
{
    public readonly struct DeathTowerRewardItem
    {
        public DeathTowerRewardItem(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public sealed class DeathTowerSettlementResult
    {
        public int ClearedFloorCount { get; set; }
        public uint ExpGained { get; set; }
        public int GoldGained { get; set; }
        public int UpdatedGold { get; set; }
        public byte PreviousLevel { get; set; }
        public byte UpdatedLevel { get; set; }
        public uint NormalExpGained { get; set; }
        public uint HonorExpGained { get; set; }
        public bool LeveledUp { get; set; }
        public bool CharacterStateChanged { get; set; }
        public AccountExperienceProgressSummary AccountProgress { get; set; }
        public IReadOnlyList<short> ChangedMainSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<DeathTowerRewardItem> Items { get; set; } = Array.Empty<DeathTowerRewardItem>();
        internal ExperienceGrantResult ExperienceGrant { get; set; }
    }

    internal readonly struct DeathTowerSettlementContext
    {
        public DeathTowerSettlementContext(
            int characterId,
            int accountId,
            byte level,
            uint exp)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            CharacterId = characterId;
            AccountId = accountId;
            Level = level;
            Exp = exp;
        }

        public int CharacterId { get; }
        public int AccountId { get; }
        public byte Level { get; }
        public uint Exp { get; }
    }

    internal delegate ExperienceGrantResult DeathTowerExperienceGrantInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int characterId,
        int accountId,
        byte currentLevel,
        uint currentExp,
        uint rawGain);

    public sealed class DeathTowerSettlementService
    {
        private readonly string _connectionString;
        private readonly AccountExperienceProgressService _accountExperience;
        private readonly DeathTowerExperienceGrantInTransaction _grantExperienceInTransaction;

        public DeathTowerSettlementService(
            string connectionString,
            AccountExperienceProgressService accountExperience = null)
            : this(connectionString, accountExperience, null)
        {
        }

        internal DeathTowerSettlementService(
            string connectionString,
            AccountExperienceProgressService accountExperience,
            DeathTowerExperienceGrantInTransaction grantExperienceInTransaction)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            _accountExperience = accountExperience
                ?? throw new ArgumentNullException(nameof(accountExperience));
            _grantExperienceInTransaction = grantExperienceInTransaction
                ?? ((connection, transaction, characterId, accountId, level, exp, rawGain) =>
                    CharacterExperienceService.GrantInTransaction(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    level,
                    exp,
                    rawGain,
                    normalizeMaxLevelExp: rawGain > 0));
        }

        internal DeathTowerSettlementResult Grant(
            DeathTowerSettlementContext context,
            DeathTowerSession tower,
            InventoryLease lease)
        {
            if (tower == null) throw new ArgumentNullException(nameof(tower));
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (lease.CharacterId != context.CharacterId)
            {
                throw new InvalidOperationException(
                    $"Death tower settlement inventory character mismatch: " +
                    $"context={context.CharacterId} lease={lease.CharacterId}.");
            }

            var rewardConfig = DeathTowerRewardConfig.Load();
            var clearedFloorCount = Math.Max(1, tower.CurrentStage + 1);
            var previousLevel = context.Level;
            var expGained = CalculateExp(previousLevel, rewardConfig.GetExpWeight(clearedFloorCount));
            var goldGained = CalculateGold(previousLevel, rewardConfig.GoldWeight);
            var lcg = tower.StageLcg ?? new DnfLcg(tower.StageSeed);
            var rewardRollCount = Math.Min(
                Math.Max(0, tower.Config.MaxClearItemCount),
                rewardConfig.GetRewardCardCount(clearedFloorCount));

            var items = new List<DeathTowerRewardItem>(rewardRollCount);
            var changedMainSlots = new List<short>(rewardRollCount);
            var changedMainSlotSet = new HashSet<short>();
            var characterId = context.CharacterId;
            var accountId = context.AccountId;
            var updatedGold = 0;

            ExperienceGrantResult expProgress;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    expProgress = _grantExperienceInTransaction(
                        connection,
                        transaction,
                        characterId,
                        accountId,
                        previousLevel,
                        context.Exp,
                        expGained);
                    var shouldPersistCharacter = expProgress.LeveledUp
                        || expProgress.NormalExpGain > 0
                        || expProgress.NormalizedMaxLevelExp;
                    if (shouldPersistCharacter && !expProgress.Persisted)
                    {
                        throw new InvalidOperationException(
                            $"Death tower settlement progress write failed for character {characterId}.");
                    }

                    transaction.Commit();
                }
            }

            var carryLimit = InventoryGoldCarryLimitLoader.Load(characterId);
            lock (lease.SyncRoot)
            {
                if (goldGained > 0)
                {
                    if (!lease.Inventory.TryGrantGold(goldGained, carryLimit, out _, out updatedGold))
                        FileLogger.Log($"[DeathTower] settlement gold skipped: cid={characterId} amount={goldGained}");
                }
                else
                {
                    updatedGold = lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                }

                for (var index = 0; index < rewardRollCount; index++)
                {
                    var rarity = rewardConfig.RollItemRarity(lcg);
                    var itemId = MonsterDropConfig.ChooseEquipment(lcg, previousLevel, rarity);
                    if (itemId <= 0)
                        itemId = MonsterDropConfig.ChooseStackable(lcg, previousLevel, rarity);
                    if (itemId <= 0)
                        continue;

                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            lease.Inventory,
                            itemId,
                            ItemCreateReason.DungeonDrop,
                            1,
                            out var grant)
                        || !grant.Success)
                    {
                        FileLogger.Log($"[DeathTower] settlement item skipped: inventory full/unsupported cid={characterId} item={itemId}");
                        continue;
                    }

                    items.Add(new DeathTowerRewardItem(itemId, 1));
                    AddChangedMainSlots(changedMainSlots, changedMainSlotSet, grant.Changes);
                }
            }

            AccountExperienceProgressSummary accountProgress = null;
            if (expProgress.HonorExpGain > 0 && accountId > 0)
            {
                var totals = new AccountExperienceProgressTotals(
                    expProgress.TotalHonorExp,
                    expProgress.TotalGrowthCapsuleExp,
                    expProgress.GrowthCapsuleExpGain);
                try
                {
                    accountProgress = _accountExperience.BuildSummary(accountId, totals);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DeathTower] committed account progress summary failed: account={accountId} cid={characterId}: {ex.Message}");
                }
                if (accountProgress != null)
                {
                    expProgress.Honor = accountProgress.Honor;
                    expProgress.GrowthCapsule = accountProgress.GrowthCapsule;
                }
            }

            var characterStateChanged = expProgress.NewLevel != expProgress.PreviousLevel
                || expProgress.NewExp != expProgress.PreviousExp;

            return new DeathTowerSettlementResult
            {
                ClearedFloorCount = clearedFloorCount,
                ExpGained = expGained,
                GoldGained = goldGained,
                UpdatedGold = updatedGold,
                PreviousLevel = previousLevel,
                UpdatedLevel = expProgress.NewLevel,
                NormalExpGained = expProgress.NormalExpGain,
                HonorExpGained = expProgress.HonorExpGain,
                LeveledUp = expProgress.LeveledUp,
                CharacterStateChanged = characterStateChanged,
                AccountProgress = accountProgress,
                ChangedMainSlots = changedMainSlots,
                Items = items,
                ExperienceGrant = expProgress,
            };
        }

        private static uint CalculateExp(byte level, float weight)
        {
            if (weight <= 0)
                return 0;
            var value = ExpTableProvider.GetExpRewardBase(level) * (double)weight;
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static int CalculateGold(byte level, float weight)
        {
            if (weight <= 0)
                return 0;
            var baseGold = ExpTableProvider.GetMonsterGold(level, out _);
            var value = baseGold * (double)weight;
            if (value <= 0)
                return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void AddChangedMainSlots(
            List<short> changedMainSlots,
            HashSet<short> changedMainSlotSet,
            InventoryMutationSet changes)
        {
            if (changes == null)
                return;

            foreach (var change in changes.Slots)
            {
                if (change.ListType == InventoryListType.Main)
                    AddChangedMainSlot(changedMainSlots, changedMainSlotSet, change.SlotIndex);
            }
        }

        private static void AddChangedMainSlot(
            List<short> changedMainSlots,
            HashSet<short> changedMainSlotSet,
            short slotIndex)
        {
            if (changedMainSlotSet.Add(slotIndex))
                changedMainSlots.Add(slotIndex);
        }

    }
}
