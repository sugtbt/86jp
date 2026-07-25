using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryOverflowDeliveryStatus
    {
        None = 0,
        MailUnavailable = 1,
    }

    internal sealed class InventoryOverflowDeliveryResult
    {
        public InventoryOverflowDeliveryStatus Status { get; set; }
    }

    internal interface IInventoryOverflowRewardSink
    {
        bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result);
    }

    internal sealed class RejectingInventoryOverflowRewardSink : IInventoryOverflowRewardSink
    {
        internal static readonly RejectingInventoryOverflowRewardSink Instance =
            new RejectingInventoryOverflowRewardSink();

        private RejectingInventoryOverflowRewardSink()
        {
        }

        public bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result)
        {
            result = new InventoryOverflowDeliveryResult
            {
                Status = InventoryOverflowDeliveryStatus.MailUnavailable,
            };
            return false;
        }
    }
}
