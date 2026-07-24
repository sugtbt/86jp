using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.DeathTower
{
    public sealed class TowerInventoryItem
    {
        public int ItemId { get; internal set; }
        public int Count { get; internal set; }
        public int StackLimit { get; internal set; }
        public bool IsWaste { get; internal set; }
    }

    public sealed class TowerPickupResult
    {
        public short DestinationSlot { get; internal set; }
        public int ItemId { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    public sealed class TowerInventoryMutation
    {
        public int ItemId { get; internal set; }
        public int RemainingCount { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    public sealed class TowerInventoryMoveResult
    {
        public int MoveValue32 { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    internal static class DeathTowerItemSlotPolicy
    {
        internal static bool IsWaste(ItemMetadata metadata)
            => metadata != null && metadata.IsPrimaryStackableFamily("waste");

        internal static int ResolveStackLimit(ItemMetadata metadata)
        {
            if (metadata == null || !metadata.IsStackable)
                return 1;
            return metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue;
        }

        internal static IReadOnlyList<short> GetAllocationOrder(ItemMetadata metadata)
        {
            var result = new List<short>();
            if (IsWaste(metadata))
            {
                AppendRange(
                    result,
                    ItemSlotBoundService.MainQuickSlotStart,
                    ItemSlotBoundService.MainQuickSlotEnd);
                GetSlotRange(metadata, out var overflowStart, out var overflowEnd);
                AppendRange(result, overflowStart, overflowEnd);
                return result;
            }

            GetSlotRange(metadata, out var start, out var end);
            AppendRange(result, start, end);
            return result;
        }

        internal static bool IsSlotAllowed(ItemMetadata metadata, short slot)
        {
            if (IsWaste(metadata))
            {
                GetSlotRange(metadata, out var overflowStart, out var overflowEnd);
                return ItemSlotBoundService.IsMainQuickSlot(slot)
                    || (slot >= overflowStart && slot <= overflowEnd);
            }

            GetSlotRange(metadata, out var start, out var end);
            return slot >= start && slot <= end;
        }

        private static void GetSlotRange(ItemMetadata metadata, out short start, out short end)
        {
            (metadata ?? ItemMetadata.CreateDefaultStackable()).GetSlotRange(
                out var resolvedStart,
                out var resolvedEnd);
            start = (short)resolvedStart;
            end = (short)resolvedEnd;
        }

        private static void AppendRange(ICollection<short> result, int start, int end)
        {
            for (var slot = start; slot <= end; slot++)
                result.Add((short)slot);
        }
    }
}
