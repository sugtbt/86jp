using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DropService
    {
        private readonly IAssetService _assetService;
        private readonly IInventoryStore _inventoryStore;

        internal DropService(IAssetService assetService, IInventoryStore inventoryStore)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
        }

        internal static void WarmUpAbyssParty()
        {
            HellMonsterDropConfig.WarmUp();
        }

        internal bool TryRegisterTemplateDrop(
            DungeonRun run,
            int itemTemplateId,
            int count,
            out DropInfo drop)
        {
            drop = default;
            if (run == null || itemTemplateId <= 0 || count <= 0)
                return false;

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            }
            catch (Exception)
            {
                return false;
            }

            if (metadata == null
                || (metadata.ItemKind != "equipment" && metadata.ItemKind != "stackable"))
            {
                return false;
            }

            lock (run.SyncRoot)
            {
                run.SceneSlotCounter++;
                if (run.SceneSlotCounter == 0)
                    run.SceneSlotCounter++;

                drop = new DropInfo
                {
                    SceneSlot = run.SceneSlotCounter,
                    TemplateId = (uint)itemTemplateId,
                    StackCount = (uint)count,
                    Endurance = metadata.Durability,
                };
                run.Drops[drop.SceneSlot] = drop;
            }

            return true;
        }

        internal MonsterDropResult GenerateAndRegister(DungeonRun run, MonsterDropRequest request)
        {
            if (run == null) return default;

            var slotCounter = run.SceneSlotCounter;

            var dropPool = MonsterDropTable.GetDropPool(request.MonsterCode);
            int areaMaterialId = AreaMaterialDropProvider.GetAreaMaterialItem(run.DungeonId);
            if (areaMaterialId > 0)
            {
                var extended = new List<MonsterDropTable.DropPoolEntry>();
                if (dropPool != null) extended.AddRange(dropPool);
                extended.Add(new MonsterDropTable.DropPoolEntry { ItemId = areaMaterialId, Weight = 100 });
                dropPool = extended;
            }

            var generator = new DropGenerator(run.RoomLcg);
            var result = generator.GenerateMonsterDrops(
                request.DropRateLevel, request.MonsterType, request.MonsterCode,
                run.Difficulty, request.DungeonBasisLevel,
                ref slotCounter, dropPool);

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, result.drops);

            return new MonsterDropResult
            {
                GoldAmount = result.goldAmount,
                Drops = result.drops
            };
        }

        internal List<DropInfo> GenerateAbyssPartyAndRegister(DungeonRun run, AbyssPartyDropRequest request)
        {
            if (run == null) return new List<DropInfo>();

            var slotCounter = run.SceneSlotCounter;

            var drops = IndependentDropSystem.GenerateDrops(
                request.MonsterCode, run.Difficulty, request.DungeonBasisLevel,
                run.RoomLcg, ref slotCounter);

            if (request.IsLastGroupMonster && !request.IsAbyssMonsterScript)
            {
                var rewardDrops = HellMonsterDropConfig.GenerateSpecificEquipmentDrops(
                    run.RoomLcg,
                    request.DungeonMinimumLevel,
                    request.DungeonBasisLevel,
                    run.Difficulty,
                    request.AbyssPartyDifficulty,
                    request.RewardRollCount,
                    ref slotCounter);
                drops.AddRange(rewardDrops);
            }

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, drops);
            return drops;
        }

        internal PickupResult TryPickup(DungeonRun run, ushort srcSlot, int characterId, int accountId)
        {
            if (run == null || !run.Drops.TryGetValue(srcSlot, out var drop))
                return PickupResult.NotFound;

            if (drop.IsGold)
            {
                var baseGold = (int)drop.StackCount;
                var bonusPct = drop.IsPlayerDropped ? 0 : GetEquippedGoldBonus(characterId);
                var extraGold = baseGold * bonusPct / 100;
                var grantedGold = PersistGold(characterId, accountId, baseGold + extraGold);
                if (!grantedGold.HasValue)
                    return PickupResult.PersistenceFailed;

                run.Drops.Remove(srcSlot);
                var grantedBaseGold = Math.Min(baseGold, grantedGold.Value);
                var grantedExtraGold = Math.Min(extraGold, Math.Max(0, grantedGold.Value - grantedBaseGold));
                return new PickupResult
                {
                    Success = true,
                    IsGold = true,
                    GoldAmount = grantedGold.Value,
                    ExtraGold = grantedExtraGold
                };
            }

            short invSlot;
            CommonInventoryItem restoredItem = null;
            if (drop.InventoryPayload != null)
            {
                if (!_inventoryStore.TryRestoreDungeonDrop(
                        characterId,
                        drop.InventoryPayload,
                        out invSlot,
                        out restoredItem))
                {
                    return PickupResult.InventoryFull;
                }
            }
            else if (!TryPickupItemToInventory(characterId, accountId, (int)drop.TemplateId, (int)drop.StackCount, out invSlot))
            {
                return PickupResult.InventoryFull;
            }

            run.Drops.Remove(srcSlot);
            return new PickupResult
            {
                Success = true,
                IsGold = false,
                InventorySlot = invSlot,
                PickedUpItemId = (int)drop.TemplateId,
                RestoredItem = restoredItem,
            };
        }

        internal InventoryDropResult TryDropInventoryItem(
            DungeonRun run,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int count)
        {
            if (run == null
                || listType != InventoryListType.Main
                || slotIndex < 0
                || count <= 0)
            {
                return InventoryDropResult.InvalidRequest;
            }

            if (!_inventoryStore.TryTakeDungeonDrop(
                    characterId,
                    listType,
                    slotIndex,
                    count,
                    out var payload)
                || payload == null
                || payload.DroppedCount <= 0)
            {
                return InventoryDropResult.InventoryRejected;
            }

            DropInfo drop;
            lock (run.SyncRoot)
            {
                run.SceneSlotCounter++;
                if (run.SceneSlotCounter == 0)
                    run.SceneSlotCounter++;

                drop = new DropInfo
                {
                    SceneSlot = run.SceneSlotCounter,
                    TemplateId = (uint)payload.ItemTemplateId,
                    StackCount = (uint)payload.DroppedCount,
                    Endurance = payload.PacketItem?.Durability ?? 0,
                    UpgradeLevel = payload.PacketItem?.ExtData0 ?? 0,
                    IsPlayerDropped = true,
                    InventoryPayload = payload,
                };
                run.Drops[drop.SceneSlot] = drop;
            }

            return new InventoryDropResult
            {
                Success = true,
                Drop = drop,
                RemainingStackCount = payload.RemainingCount,
            };
        }

        private static void RegisterDrops(DungeonRun run, List<DropInfo> drops)
        {
            if (drops == null || drops.Count == 0) return;
            foreach (var drop in drops)
                run.Drops[drop.SceneSlot] = drop;
        }

        private int? PersistGold(int characterId, int accountId, int goldGained)
        {
            if (goldGained <= 0) return 0;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var granted = _assetService.GrantGold(scope, goldGained);
                    scope.Commit();
                    return granted;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DropService] PersistGold ERROR: {ex.Message}");
                return null;
            }
        }

        private int GetEquippedGoldBonus(int characterId)
        {
            var totalBonus = 0;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, 0))
                {
                    using var cmd = scope.Connection.CreateCommand();
                    cmd.CommandText = "SELECT item_id FROM character_equipped_entries WHERE character_id = @cid";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var itemId = reader.GetInt32(0);
                        if (GoldBonusEquipments.TryGetValue(itemId, out var bonus))
                            totalBonus += bonus;
                    }
                }
            }
            catch (Exception ex) { FileLogger.Log($"[DropService] GetEquippedGoldBonus ERROR: cid={characterId}: {ex.Message}"); }
            return totalBonus;
        }

        private bool TryPickupItemToInventory(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot)
        {
            assignedSlot = -1;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var result = _assetService.TryAddItem(scope, itemTemplateId, stackCount, out assignedSlot);
                    if (result) scope.Commit();
                    return result;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DropService] TryPickupItemToInventory ERROR: {ex.Message}");
                return false;
            }
        }

        private static readonly Dictionary<int, int> GoldBonusEquipments = new()
        {
            {100320775, 12},
            {24191, 10},
            {100341606, 30},
            {100331240, 10},
            {100331319, 3},
            {26626, 3},
            {26627, 4},
            {26341, 3},
            {26342, 4},
            {26115, 3},
            {104000181, 3},
            {101020286, 3},
            {101020526, 3},
            {109000133, 3}
        };
    }

    internal struct MonsterDropRequest
    {
        public int DropRateLevel;
        public int MonsterType;
        public int MonsterCode;
        public int DungeonBasisLevel;
    }

    internal struct AbyssPartyDropRequest
    {
        public int MonsterCode;
        public int DungeonMinimumLevel;
        public int DungeonBasisLevel;
        public byte AbyssPartyDifficulty;
        public int RewardRollCount;
        public bool IsLastGroupMonster;
        public bool IsAbyssMonsterScript;
    }

    internal struct MonsterDropResult
    {
        public int GoldAmount;
        public List<DropInfo> Drops;
    }

    internal struct PickupResult
    {
        public bool Success;
        public bool IsGold;
        public int GoldAmount;
        public int ExtraGold;
        public short InventorySlot;
        public int PickedUpItemId;
        public CommonInventoryItem RestoredItem;
        public PickupFailReason FailReason;

        internal static readonly PickupResult NotFound = new PickupResult { FailReason = PickupFailReason.NotFound };
        internal static readonly PickupResult InventoryFull = new PickupResult { FailReason = PickupFailReason.InventoryFull };
        internal static readonly PickupResult PersistenceFailed = new PickupResult { FailReason = PickupFailReason.PersistenceFailed };
    }

    internal struct InventoryDropResult
    {
        internal bool Success;
        internal DropInfo Drop;
        internal int RemainingStackCount;
        internal InventoryDropFailReason FailReason;

        internal static readonly InventoryDropResult InvalidRequest = new InventoryDropResult
        {
            FailReason = InventoryDropFailReason.InvalidRequest,
        };

        internal static readonly InventoryDropResult InventoryRejected = new InventoryDropResult
        {
            FailReason = InventoryDropFailReason.InventoryRejected,
        };
    }

    internal enum InventoryDropFailReason : byte
    {
        None,
        InvalidRequest,
        InventoryRejected,
    }

    internal enum PickupFailReason : byte
    {
        None,
        NotFound,
        InventoryFull,
        PersistenceFailed,
    }
}
