using System;
using System.Buffers.Binary;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    internal static class AuctionItemDescriptorCodec
    {
        public const int Size = 83;

        private const int SealFlagOffset = 0;
        private const int ItemIdOffset = 1;
        private const int UpgradeOffset = 5;
        private const int ValueOffset = 6;
        private const int DurabilityOffset = 10;
        private const int EnchantCardIdOffset = 12;
        private const int AmplifyTypeOffset = 16;
        private const int AmplifyValueOffset = 17;
        private const int RandomOptionBlockOffset = 36;
        private const int RandomOptionBlockSize = 15;
        private const int GenuineUpgradeOffset = 51;

        public static void Write(Span<byte> descriptor, ItemCore core)
        {
            if (descriptor.Length != Size)
            {
                throw new ArgumentException(
                    "The auction item descriptor must be exactly 83 bytes.",
                    nameof(descriptor));
            }
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            descriptor.Clear();
            descriptor[SealFlagOffset] = core.SealFlag;
            BinaryPrimitives.WriteInt32LittleEndian(
                descriptor.Slice(ItemIdOffset, sizeof(int)),
                core.ItemId);
            descriptor[UpgradeOffset] = core.Attr;
            BinaryPrimitives.WriteInt32LittleEndian(
                descriptor.Slice(ValueOffset, sizeof(int)),
                core.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.Slice(DurabilityOffset, sizeof(ushort)),
                core.Durability);
            BinaryPrimitives.WriteInt32LittleEndian(
                descriptor.Slice(EnchantCardIdOffset, sizeof(int)),
                core.EnchantCardId);
            descriptor[AmplifyTypeOffset] = core.AmplifyType;
            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.Slice(AmplifyValueOffset, sizeof(ushort)),
                core.AmplifyValue);

            if (HasRandomOptionData(core))
            {
                var itemCore = core.ToBytes();
                itemCore.AsSpan(
                        ItemCore.RandomOption0Offset,
                        RandomOptionBlockSize)
                    .CopyTo(descriptor.Slice(
                        RandomOptionBlockOffset,
                        RandomOptionBlockSize));
            }

            descriptor[GenuineUpgradeOffset] = core.GenuineUpgrade;
        }

        private static bool HasRandomOptionData(ItemCore core)
        {
            return core.RandomOptionCount > 0
                || core.RandomOptionState != 0
                || core.RandomOptionChangedIndex
                    != ItemCore.RandomOptionChangedIndexDefault
                || core.RandomOptionChangeState != 0
                || !core.RandomOptionChange.IsEmpty;
        }
    }
}
