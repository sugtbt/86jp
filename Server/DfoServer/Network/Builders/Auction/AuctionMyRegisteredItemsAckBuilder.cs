using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    internal static class AuctionMyRegisteredItemsAckBuilder
    {
        private const int HeaderSize = 3;
        private const int RecordSize = 147;
        private const int ListingIdOffset = 0;
        private const int BidPriceOffset = 8;
        private const int InstantPriceOffset = 12;
        private const int StatusOffset = 29;
        private const int ItemDescriptorOffset = 30;

        public static byte[] BuildEmpty(byte mode)
            => BuildSuccess(mode, Array.Empty<AuctionListingBundle>());

        public static byte[] BuildSuccess(
            byte mode,
            IReadOnlyList<AuctionListingBundle> listings)
        {
            if (mode > 1)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (listings == null)
                throw new ArgumentNullException(nameof(listings));
            if (listings.Count > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(listings),
                    "The client count field is one byte.");
            }

            var body = new byte[HeaderSize + (RecordSize * listings.Count)];
            body[0] = 0x01;
            body[1] = mode;
            body[2] = (byte)listings.Count;

            for (var index = 0; index < listings.Count; index++)
            {
                WriteRecord(
                    body.AsSpan(HeaderSize + (index * RecordSize), RecordSize),
                    listings[index]);
            }
            return body;
        }

        private static void WriteRecord(
            Span<byte> record,
            AuctionListingBundle bundle)
        {
            if (bundle?.Listing == null || bundle.Escrow == null)
            {
                throw new ArgumentException(
                    "Every auction row requires listing and escrow data.",
                    nameof(bundle));
            }

            var listing = bundle.Listing;
            var escrow = bundle.Escrow;
            if (listing.ListingId <= 0
                || escrow.ListingId != listing.ListingId
                || listing.Status != AuctionListingStatus.Active)
            {
                throw new ArgumentException(
                    "Only matching active auction rows can be serialized.",
                    nameof(bundle));
            }
            if (listing.UnitPrice <= 0
                || listing.UnitPrice > int.MaxValue
                || listing.TotalPrice <= 0
                || listing.TotalPrice > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bundle),
                    "The client price fields are positive signed dwords.");
            }
            long expectedTotal;
            try
            {
                expectedTotal = checked(
                    listing.UnitPrice * listing.Quantity);
            }
            catch (OverflowException ex)
            {
                throw new ArgumentException(
                    "The listing total price overflows Int64.",
                    nameof(bundle),
                    ex);
            }
            if (expectedTotal != listing.TotalPrice)
            {
                throw new ArgumentException(
                    "Listing total price must match unit price and quantity.",
                    nameof(bundle));
            }
            if (escrow.ItemCore == null
                || escrow.ItemCore.Length != ItemCore.Size)
            {
                throw new ArgumentException(
                    "The escrow item core must use the 82-byte inventory layout.",
                    nameof(bundle));
            }
            var core = ItemCore.FromBytes(escrow.ItemCore);
            if (core.ItemId != listing.ItemId
                || core.ItemKind != listing.ItemKind
                || escrow.Quantity != listing.Quantity
                || (InventoryStackRuleService.IsStackable(core)
                    && core.Count != listing.Quantity)
                || (!InventoryStackRuleService.IsStackable(core)
                    && listing.Quantity != 1))
            {
                throw new ArgumentException(
                    "Listing and escrow item identity must match.",
                    nameof(bundle));
            }

            // Current 86JP client callback 0x00CD5800 consumes a fixed
            // 147-byte row. PR1 has no bidding, so bidder/status/auxiliary
            // fields remain zero and bid price is -1.
            BinaryPrimitives.WriteInt64LittleEndian(
                record.Slice(ListingIdOffset, sizeof(long)),
                listing.ListingId);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(BidPriceOffset, sizeof(int)),
                -1);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(InstantPriceOffset, sizeof(int)),
                (int)listing.TotalPrice);
            record[StatusOffset] = 0;

            // The 83-byte auction descriptor is a dedicated wire shape, not
            // an ItemCore blob. The codec projects only client-established
            // fields; the following 30+4 auxiliary bytes remain zero.
            AuctionItemDescriptorCodec.Write(
                record.Slice(
                    ItemDescriptorOffset,
                    AuctionItemDescriptorCodec.Size),
                core);
        }
    }
}
