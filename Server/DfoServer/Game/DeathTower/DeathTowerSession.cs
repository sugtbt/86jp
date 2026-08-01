using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.DeathTower
{
    public sealed class DeathTowerSession
    {
        private readonly Dictionary<ushort, List<StageTowerItem>> _stageItemsByMonster =
            new Dictionary<ushort, List<StageTowerItem>>();
        private readonly Dictionary<ushort, DropInfo> _groundItems =
            new Dictionary<ushort, DropInfo>();
        private readonly HashSet<ushort> _deadMonsters = new HashSet<ushort>();
        private readonly Dictionary<short, TowerInventoryItem> _inventoryItems =
            new Dictionary<short, TowerInventoryItem>();
        private readonly HashSet<short> _persistentMainSlots = new HashSet<short>();
        private readonly HashSet<int> _seenItemIds = new HashSet<int>();
        private DnfLcg _stageLcg;

        public DeathTowerData.TowerConfig Config { get; }
        public int CurrentStage { get; private set; }
        public int EndStage => Config.TotalStages - 1;
        public ushort MonsterSequence { get; private set; }
        public ushort ItemSequence { get; private set; }
        public int State { get; private set; }  // 0=init, 1=fighting, 2=cleared
        public uint StageSeed { get; private set; }
        internal DnfLcg StageLcg => _stageLcg;
        public IReadOnlyDictionary<ushort, DropInfo> GroundItems => _groundItems;
        public IReadOnlyDictionary<short, TowerInventoryItem> InventoryItems => _inventoryItems;
        public IReadOnlyCollection<int> SeenItemIds => _seenItemIds;

        public DeathTowerSession(DeathTowerData.TowerConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            CurrentStage = 0;
            MonsterSequence = 1;
            ItemSequence = 1;
            State = 0;
        }

        public int GetCurrentMapId()
        {
            if (CurrentStage < 0 || CurrentStage >= Config.StageMapIds.Count)
                return -1;
            return Config.StageMapIds[CurrentStage];
        }

        public ushort NextMonsterSeq() => MonsterSequence++;

        public ushort NextItemSeq()
        {
            var value = ItemSequence++;
            if (value != 0)
                return value;
            return ItemSequence++;
        }

        public void BeginStage(uint seed, IReadOnlyList<StageTowerItem> items)
        {
            StageSeed = seed;
            _stageLcg = new DnfLcg(seed);
            _stageItemsByMonster.Clear();
            _groundItems.Clear();
            _deadMonsters.Clear();

            if (items == null || items.Count == 0)
                return;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.ItemId > 0)
                    _seenItemIds.Add(item.ItemId);
                if (item.SourceMonsterUniqueId == 0
                    || item.ItemUniqueId == 0
                    || item.ItemId <= 0)
                {
                    continue;
                }

                if (!_stageItemsByMonster.TryGetValue(item.SourceMonsterUniqueId, out var bucket))
                {
                    bucket = new List<StageTowerItem>();
                    _stageItemsByMonster[item.SourceMonsterUniqueId] = bucket;
                }
                bucket.Add(item);
            }
        }

        public IReadOnlyList<DropInfo> GenerateDropsForMonster(ushort monsterUniqueId)
        {
            if (monsterUniqueId == 0 || !_deadMonsters.Add(monsterUniqueId))
                return Array.Empty<DropInfo>();
            if (!_stageItemsByMonster.TryGetValue(monsterUniqueId, out var configuredItems))
                return Array.Empty<DropInfo>();

            var drops = new List<DropInfo>();
            foreach (var item in configuredItems)
            {
                var dropRate = Math.Max(0, Math.Min(10000, item.DropRate));
                if (dropRate == 0)
                    continue;
                if (dropRate < 10000 && (_stageLcg == null || _stageLcg.Next(10000) >= dropRate))
                    continue;

                var drop = new DropInfo
                {
                    SceneSlot = item.ItemUniqueId,
                    TemplateId = (uint)item.ItemId,
                    StackCount = (uint)Math.Max(1, item.StackCount),
                };
                if (_groundItems.ContainsKey(drop.SceneSlot))
                    continue;

                _groundItems[drop.SceneSlot] = drop;
                drops.Add(drop);
            }

            return drops;
        }

        public bool TryPickupGroundItem(ushort sceneSlot, out TowerPickupResult result)
        {
            result = null;
            if (!_groundItems.TryGetValue(sceneSlot, out var drop)
                || drop.TemplateId == 0
                || drop.TemplateId > int.MaxValue
                || drop.StackCount == 0
                || drop.StackCount > int.MaxValue)
            {
                return false;
            }

            var itemId = (int)drop.TemplateId;
            if (!TryAddInventoryItem(itemId, (int)drop.StackCount, out var destination, out var changedSlots))
                return false;

            _groundItems.Remove(sceneSlot);
            _seenItemIds.Add(itemId);
            result = new TowerPickupResult
            {
                DestinationSlot = destination,
                ItemId = itemId,
                ChangedSlots = changedSlots,
            };
            return true;
        }

        public bool TryUseItem(short slot, int expectedItemId, out TowerInventoryMutation result)
        {
            result = null;
            if (!_inventoryItems.TryGetValue(slot, out var item)
                || item.ItemId != expectedItemId
                || !item.IsWaste
                || item.Count <= 0)
            {
                return false;
            }

            item.Count--;
            var remaining = item.Count;
            if (remaining == 0)
                _inventoryItems.Remove(slot);

            result = new TowerInventoryMutation
            {
                ItemId = item.ItemId,
                RemainingCount = remaining,
                ChangedSlots = new[] { slot },
            };
            return true;
        }

        public bool TryMoveItem(
            short sourceSlot,
            short destinationSlot,
            int requestedCount,
            out TowerInventoryMoveResult result)
        {
            result = null;
            if (!_inventoryItems.TryGetValue(sourceSlot, out var source))
                return false;
            if (_persistentMainSlots.Contains(destinationSlot))
                return false;

            var sourceMetadata = Inventory.ItemMetadataResolver.Resolve(source.ItemId);
            if (!DeathTowerItemSlotPolicy.IsSlotAllowed(sourceMetadata, destinationSlot))
                return false;

            if (sourceSlot == destinationSlot)
            {
                result = CreateMoveResult(requestedCount, Array.Empty<short>());
                return true;
            }

            var moveCount = requestedCount <= 0
                ? source.Count
                : Math.Min(requestedCount, source.Count);
            if (moveCount <= 0)
                return false;

            if (!_inventoryItems.TryGetValue(destinationSlot, out var destination))
            {
                var moved = CreateInventoryItem(source.ItemId, moveCount, sourceMetadata);
                _inventoryItems[destinationSlot] = moved;
                source.Count -= moveCount;
                if (source.Count == 0)
                    _inventoryItems.Remove(sourceSlot);
                result = CreateMoveResult(
                    requestedCount,
                    new[] { sourceSlot, destinationSlot });
                return true;
            }

            if (destination.ItemId == source.ItemId)
            {
                var available = Math.Max(0, destination.StackLimit - destination.Count);
                var merged = Math.Min(moveCount, available);
                if (merged <= 0)
                    return false;
                destination.Count += merged;
                source.Count -= merged;
                if (source.Count == 0)
                    _inventoryItems.Remove(sourceSlot);
                result = CreateMoveResult(
                    requestedCount,
                    new[] { sourceSlot, destinationSlot });
                return true;
            }

            if (moveCount != source.Count)
                return false;
            var destinationMetadata = Inventory.ItemMetadataResolver.Resolve(destination.ItemId);
            if (!DeathTowerItemSlotPolicy.IsSlotAllowed(destinationMetadata, sourceSlot))
                return false;

            _inventoryItems[sourceSlot] = destination;
            _inventoryItems[destinationSlot] = source;
            result = CreateMoveResult(
                requestedCount,
                new[] { sourceSlot, destinationSlot });
            return true;
        }

        public bool TryGetInventoryItem(short slot, out TowerInventoryItem item)
            => _inventoryItems.TryGetValue(slot, out item);

        public void SetPersistentMainSlotOccupancy(IEnumerable<short> occupiedSlots)
        {
            _persistentMainSlots.Clear();
            if (occupiedSlots == null)
                return;

            foreach (var slot in occupiedSlots)
            {
                // Death Tower owns its temporary 3-8 quickbar view. Persistent quickbar
                // occupancy must never change tower pickup order.
                if (Inventory.ItemSlotBoundService.IsMainQuickSlot(slot))
                    continue;
                _persistentMainSlots.Add(slot);
            }
        }

        public IReadOnlyDictionary<int, int> GetItemCountsSnapshot()
        {
            var result = new Dictionary<int, int>();
            foreach (var item in _inventoryItems.Values)
            {
                result.TryGetValue(item.ItemId, out var current);
                result[item.ItemId] = current > int.MaxValue - item.Count
                    ? int.MaxValue
                    : current + item.Count;
            }
            return result;
        }

        public void SetFighting() { State = 1; }

        public void SetCleared() { State = 2; }

        // 允许从 state>=1 推进(state==1: 86JP可能不发0x009F(2)直接MOVE_MAP; state==2: 正常流程)
        // state==0(init, 未开始战斗)不允许推进。
        public bool TryAdvanceStage()
        {
            if (State < 1)
                return false;
            if (CurrentStage >= EndStage)
                return false;
            ClearStageState();
            CurrentStage++;
            State = 0;
            return true;
        }

        public bool IsLastStage => CurrentStage >= EndStage;

        private void ClearStageState()
        {
            StageSeed = 0;
            _stageLcg = null;
            _stageItemsByMonster.Clear();
            _groundItems.Clear();
            _deadMonsters.Clear();
        }

        private bool TryAddInventoryItem(
            int itemId,
            int count,
            out short destinationSlot,
            out IReadOnlyList<short> changedSlots)
        {
            destinationSlot = -1;
            changedSlots = Array.Empty<short>();
            if (itemId <= 0 || count <= 0)
                return false;

            var metadata = Inventory.ItemMetadataResolver.Resolve(itemId);
            var stackLimit = DeathTowerItemSlotPolicy.ResolveStackLimit(metadata);
            var allocationOrder = DeathTowerItemSlotPolicy.GetAllocationOrder(metadata);
            var remaining = count;
            var additions = new Dictionary<short, int>();

            foreach (var slot in allocationOrder)
            {
                if (!_inventoryItems.TryGetValue(slot, out var existing)
                    || existing.ItemId != itemId
                    || existing.Count >= stackLimit)
                {
                    continue;
                }

                var add = Math.Min(remaining, stackLimit - existing.Count);
                if (add <= 0)
                    continue;
                additions[slot] = add;
                remaining -= add;
                if (remaining == 0)
                    break;
            }

            if (remaining > 0)
            {
                foreach (var slot in allocationOrder)
                {
                    if (_inventoryItems.ContainsKey(slot)
                        || additions.ContainsKey(slot)
                        || _persistentMainSlots.Contains(slot))
                        continue;
                    var add = Math.Min(remaining, stackLimit);
                    additions[slot] = add;
                    remaining -= add;
                    if (remaining == 0)
                        break;
                }
            }

            if (remaining > 0)
                return false;

            var changed = new List<short>();
            foreach (var entry in additions)
            {
                if (_inventoryItems.TryGetValue(entry.Key, out var existing))
                {
                    existing.Count += entry.Value;
                }
                else
                {
                    _inventoryItems[entry.Key] = CreateInventoryItem(
                        itemId,
                        entry.Value,
                        metadata);
                }
                changed.Add(entry.Key);
            }

            destinationSlot = changed[0];
            changedSlots = changed;
            return true;
        }

        private static TowerInventoryItem CreateInventoryItem(
            int itemId,
            int count,
            Inventory.ItemMetadata metadata)
        {
            return new TowerInventoryItem
            {
                ItemId = itemId,
                Count = count,
                StackLimit = DeathTowerItemSlotPolicy.ResolveStackLimit(metadata),
                IsWaste = DeathTowerItemSlotPolicy.IsWaste(metadata),
            };
        }

        private static TowerInventoryMoveResult CreateMoveResult(
            int moveValue32,
            IReadOnlyList<short> changedSlots)
        {
            return new TowerInventoryMoveResult
            {
                MoveValue32 = moveValue32,
                ChangedSlots = changedSlots,
            };
        }
    }
}

