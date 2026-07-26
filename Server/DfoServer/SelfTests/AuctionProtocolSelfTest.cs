using DfoServer.Game.Auction;
using DfoServer.Network;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Auction;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class AuctionProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== AUCTION_PROTOCOL selftest ===");
            var failures = 0;

            TestCapturedAveragePriceRequest(ref failures);
            TestMalformedAveragePriceRequests(ref failures);
            TestNoHistoryAveragePriceResponse(ref failures);
            TestCapturedRegisterItemRequest(ref failures);
            TestCapturedStackRegisterItemRequest(ref failures);
            TestRegisterItemCommandMapping(ref failures);
            TestMalformedRegisterItemRequests(ref failures);
            TestRegisterItemResponses(ref failures);
            TestCapturedCancelListingRequest(ref failures);
            TestCancelListingResponses(ref failures);
            TestCapturedMyRegisteredItemsRequest(ref failures);
            TestMyRegisteredItemsEmptyResponse(ref failures);
            TestMyRegisteredItemsRecordResponse(ref failures);

            Console.WriteLine($"=== AUCTION_PROTOCOL selftest result: fail={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void TestCapturedAveragePriceRequest(ref int failures)
        {
            var capturedRequest = new byte[]
            {
                0x00,
                0x22, 0xB3, 0x98, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };

            Check("captured B6 request parses exact item id and opaque descriptor",
                AuctionAskAveragePriceRequestParser.TryParse(capturedRequest, out var request)
                && request.QueryMode == 0
                && request.ItemTemplateId == 10007330
                && request.OpaqueItemDescriptor.SequenceEqual(new byte[14]),
                ref failures);
        }

        private static void TestMalformedAveragePriceRequests(ref int failures)
        {
            var valid = new byte[]
            {
                0x00,
                0x22, 0xB3, 0x98, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };

            Check("B6 parser rejects a short body",
                !AuctionAskAveragePriceRequestParser.TryParse(valid.Take(18).ToArray(), out _),
                ref failures);
            Check("B6 parser rejects trailing bytes",
                !AuctionAskAveragePriceRequestParser.TryParse(valid.Concat(new byte[] { 0x00 }).ToArray(), out _),
                ref failures);

            var unknownMode = (byte[])valid.Clone();
            unknownMode[0] = 0x02;
            Check("B6 parser rejects an unknown query mode",
                !AuctionAskAveragePriceRequestParser.TryParse(unknownMode, out _),
                ref failures);

            var missingItemId = (byte[])valid.Clone();
            Array.Clear(missingItemId, 1, sizeof(int));
            Check("B6 parser rejects a missing item id",
                !AuctionAskAveragePriceRequestParser.TryParse(missingItemId, out _),
                ref failures);
        }

        private static void TestNoHistoryAveragePriceResponse(ref int failures)
        {
            var expectedBody = new byte[]
            {
                0x01, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            var body = AuctionAveragePriceAckBuilder.BuildNoHistory();
            Check("B6 empty-market ACK is the observed 30-byte comparison layout",
                body.SequenceEqual(expectedBody),
                ref failures);

            var expectedPacket = new byte[]
            {
                0x01,
                0xB6, 0x00,
                0x2D, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x01, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            var packet = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.AUCTION_ASK_AVERAGE_PRICE,
                body);
            Check("B6 ACK keeps command, type, length, and body offsets",
                packet.SequenceEqual(expectedPacket),
                ref failures);
        }

        private static void TestCapturedRegisterItemRequest(ref int failures)
        {
            var capturedRequest = CapturedRegisterItemRequest();

            Check("captured B7 request parses fixed-price listing terms and opaque fields",
                AuctionRegisterItemRequestParser.TryParse(capturedRequest, out var request)
                && request.PayType == 0
                && request.SourceListType == Game.Inventory.InventoryListType.Main
                && request.SourceSlotIndex == 73
                && request.ItemTemplateId == 10015172
                && request.Quantity == 1
                && request.BidPrice == -1
                && request.InstantPrice == 111111
                && request.UnitPrice == 111111
                && request.RoiCategories.SequenceEqual(new byte[]
                {
                    0x18, 0x00, 0x00,
                    0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00,
                })
                && request.OpaqueTrailer.SequenceEqual(new byte[4]),
                ref failures);
        }

        private static void TestMalformedRegisterItemRequests(ref int failures)
        {
            var valid = CapturedRegisterItemRequest();

            Check("B7 parser rejects short and trailing bodies",
                !AuctionRegisterItemRequestParser.TryParse(
                    valid.Take(valid.Length - 1).ToArray(),
                    out _)
                && !AuctionRegisterItemRequestParser.TryParse(
                    valid.Concat(new byte[] { 0x00 }).ToArray(),
                    out _),
                ref failures);

            var unsupportedPayType = (byte[])valid.Clone();
            unsupportedPayType[0] = 1;
            var unsupportedList = (byte[])valid.Clone();
            unsupportedList[1] = 1;
            Check("B7 parser accepts only gold listings from the main bag",
                !AuctionRegisterItemRequestParser.TryParse(
                    unsupportedPayType,
                    out _)
                && !AuctionRegisterItemRequestParser.TryParse(
                    unsupportedList,
                    out _),
                ref failures);

            var bidListing = (byte[])valid.Clone();
            Array.Copy(BitConverter.GetBytes(1), 0, bidListing, 12, 4);
            var invalidItem = (byte[])valid.Clone();
            Array.Clear(invalidItem, 4, 4);
            var invalidQuantity = (byte[])valid.Clone();
            Array.Clear(invalidQuantity, 8, 4);
            Check("B7 parser rejects bid listings and missing item terms",
                !AuctionRegisterItemRequestParser.TryParse(bidListing, out _)
                && !AuctionRegisterItemRequestParser.TryParse(invalidItem, out _)
                && !AuctionRegisterItemRequestParser.TryParse(
                    invalidQuantity,
                    out _),
                ref failures);

            var inconsistentTotal = (byte[])valid.Clone();
            Array.Copy(
                BitConverter.GetBytes(222222),
                0,
                inconsistentTotal,
                16,
                4);
            Check("B7 parser rejects inconsistent duplicated unit prices",
                !AuctionRegisterItemRequestParser.TryParse(
                    inconsistentTotal,
                    out _),
                ref failures);
        }

        private static void TestCapturedStackRegisterItemRequest(
            ref int failures)
        {
            var capturedRequest = new byte[]
            {
                0x00, 0x00,
                0x85, 0x00,
                0x54, 0x0C, 0x00, 0x00,
                0x59, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF,
                0x0A, 0x00, 0x00, 0x00,
                0x0A, 0x00, 0x00, 0x00,
                0x18, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };

            Check("live B7 stack request keeps both wire prices per item",
                AuctionRegisterItemRequestParser.TryParse(
                    capturedRequest,
                    out var request)
                && request.SourceSlotIndex == 133
                && request.ItemTemplateId == 3156
                && request.Quantity == 89
                && request.InstantPrice == 10
                && request.UnitPrice == 10,
                ref failures);
        }

        private static void TestRegisterItemCommandMapping(ref int failures)
        {
            AuctionRegisterItemRequestParser.TryParse(
                CapturedRegisterItemRequest(),
                out var request);
            var command = AuctionRegisterItemCommandMapper.Map(request);

            Check("B7 request maps all authoritative listing and stale-slot terms",
                command != null
                && command.SourceListType
                    == Game.Inventory.InventoryListType.Main
                && command.SourceSlotIndex == 73
                && command.ExpectedItemTemplateId == 10015172
                && command.Quantity == 1
                && command.UnitPrice == 111111,
                ref failures);
        }

        private static void TestRegisterItemResponses(ref int failures)
        {
            Check("B7 success ACK selects the client registration-result subtype",
                AuctionRegisterItemAckBuilder.BuildSuccess(0x1234)
                    .SequenceEqual(new byte[] { 0x01, 0x00 }),
                ref failures);
            Check("B7 failure ACK selects the client registration-result subtype",
                AuctionRegisterItemAckBuilder.BuildFailure(0x98, 0x1234)
                    .SequenceEqual(new byte[] { 0x00, 0x00 }),
                ref failures);
            Check("B7 auction-gold-limit failure has a concrete client notice",
                AuctionRegisterItemFailureReasonMapper.Map(
                    AuctionApplicationError.AuctionGoldLimitExceeded)
                    .Equals("您输入的金额超过拍卖额上限"),
                ref failures);
            Check("B7 active-listing-limit failure uses the client auction-limit notice",
                AuctionRegisterItemFailureReasonMapper.Map(
                    AuctionApplicationError.ActiveListingLimitReached)
                    .Equals(
                        "已超过上架数量上限， 需要拍卖行优惠券才能继续上架"),
                ref failures);
            Check("B7 unknown failure has a retryable client notice",
                AuctionRegisterItemFailureReasonMapper.Map(
                    AuctionApplicationError.PersistenceFailed)
                    .Equals("拍卖行操作失败，请稍后重试"),
                ref failures);

            var expectedPacket = new byte[]
            {
                0x01,
                0xB7, 0x00,
                0x11, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x01, 0x00,
            };
            var packet = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.AUCTION_REGIST_ITEM,
                AuctionRegisterItemAckBuilder.BuildSuccess(0x1234));
            Check("B7 success ACK keeps command envelope and body offsets",
                packet.SequenceEqual(expectedPacket),
                ref failures);
        }

        private static void TestCapturedCancelListingRequest(ref int failures)
        {
            var capturedRequest = new byte[]
            {
                0x00,
                0x11, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            Check("live B8 request parses gold mode and 64-bit listing id",
                AuctionCancelListingRequestParser.TryParse(
                    capturedRequest,
                    out var request)
                && request.Mode == 0
                && request.ListingId == 17,
                ref failures);

            Check("B8 parser rejects missing, unknown-mode, zero-id, and trailing requests",
                !AuctionCancelListingRequestParser.TryParse(
                    Array.Empty<byte>(),
                    out _)
                && !AuctionCancelListingRequestParser.TryParse(
                    new byte[]
                    {
                        0x02,
                        0x11, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    },
                    out _)
                && !AuctionCancelListingRequestParser.TryParse(
                    new byte[9],
                    out _)
                && !AuctionCancelListingRequestParser.TryParse(
                    capturedRequest.Concat(new byte[] { 0x00 }).ToArray(),
                    out _),
                ref failures);
        }

        private static void TestCancelListingResponses(ref int failures)
        {
            Check("B8 success ACK returns result and echoes auction mode",
                AuctionCancelListingAckBuilder.BuildSuccess(0)
                    .SequenceEqual(new byte[] { 0x01, 0x00 }),
                ref failures);
            Check("B8 failure ACK returns result and echoes auction mode",
                AuctionCancelListingAckBuilder.BuildFailure(1)
                    .SequenceEqual(new byte[] { 0x00, 0x01 }),
                ref failures);
            Check("B8 stale listing failure has a concrete client notice",
                AuctionCancelListingFailureReasonMapper.Map(
                    AuctionApplicationError.ListingNotActive)
                    .Equals("该拍卖品已不存在或状态发生变化"),
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.AUCTION_REGIST_CANCEL,
                AuctionCancelListingAckBuilder.BuildSuccess(0));
            Check("B8 success ACK keeps command envelope and body offsets",
                packet.SequenceEqual(new byte[]
                {
                    0x01,
                    0xB8, 0x00,
                    0x11, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x01, 0x00,
                }),
                ref failures);
        }

        private static void TestCapturedMyRegisteredItemsRequest(
            ref int failures)
        {
            Check("captured BC request parses the gold-auction mode",
                AuctionMyRegisteredItemsRequestParser.TryParse(
                    new byte[] { 0x00 },
                    out var request)
                && request.Mode == 0,
                ref failures);

            Check("BC parser rejects missing, unknown, and trailing modes",
                !AuctionMyRegisteredItemsRequestParser.TryParse(
                    Array.Empty<byte>(),
                    out _)
                && !AuctionMyRegisteredItemsRequestParser.TryParse(
                    new byte[] { 0x02 },
                    out _)
                && !AuctionMyRegisteredItemsRequestParser.TryParse(
                    new byte[] { 0x00, 0x00 },
                    out _),
                ref failures);
        }

        private static void TestMyRegisteredItemsEmptyResponse(
            ref int failures)
        {
            var body = AuctionMyRegisteredItemsAckBuilder.BuildEmpty(0);
            Check("BC empty-list ACK echoes mode and reports zero records",
                body.SequenceEqual(new byte[] { 0x01, 0x00, 0x00 }),
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.AUCTION_MY_REGISTED_ITEM_INFO,
                body);
            Check("BC empty-list ACK keeps command envelope and body offsets",
                packet.SequenceEqual(new byte[]
                {
                    0x01,
                    0xBC, 0x00,
                    0x12, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x01, 0x00, 0x00,
                }),
                ref failures);
        }

        private static void TestMyRegisteredItemsRecordResponse(
            ref int failures)
        {
            const long listingId = 0x0102030405060708;
            const int unitPrice = 111;
            var core = new Game.Inventory.ItemCore
            {
                ItemKind = Game.Inventory.ItemCore.KindMaterial,
                ItemId = 3156,
                Value = 5,
                Attr = 4,
            };
            var bundle = new AuctionListingBundle
            {
                Listing = new AuctionListingRecord
                {
                    ListingId = listingId,
                    ItemId = core.ItemId,
                    ItemKind = core.ItemKind,
                    Quantity = core.Value,
                    UnitPrice = unitPrice,
                    TotalPrice = unitPrice * core.Value,
                    Status = AuctionListingStatus.Active,
                },
                Escrow = new AuctionEscrowItemRecord
                {
                    ListingId = listingId,
                    ItemCore = core.ToBytes(),
                    Quantity = core.Value,
                },
            };

            var body = AuctionMyRegisteredItemsAckBuilder.BuildSuccess(
                0,
                new[] { bundle });
            var expected = new byte[3 + 147];
            expected[0] = 0x01;
            expected[1] = 0x00;
            expected[2] = 0x01;
            Array.Copy(BitConverter.GetBytes(listingId), 0, expected, 3, 8);
            Array.Copy(BitConverter.GetBytes(-1), 0, expected, 11, 4);
            Array.Copy(
                BitConverter.GetBytes(unitPrice * core.Value),
                0,
                expected,
                15,
                4);
            Array.Copy(BitConverter.GetBytes(core.ItemId), 0, expected, 34, 4);
            expected[38] = core.Attr;
            Array.Copy(BitConverter.GetBytes(core.Value), 0, expected, 39, 4);
            Array.Copy(
                BitConverter.GetBytes(core.Durability),
                0,
                expected,
                43,
                2);

            Check("BC one-record ACK maps the client item descriptor fields",
                body.SequenceEqual(expected),
                ref failures);

            var equipment = new Game.Inventory.ItemCore
            {
                ItemKind = Game.Inventory.ItemCore.KindEquipment,
                ItemId = 27656,
                InstanceValue = unchecked((int)0x35ED81E6),
                Attr = 0x6A,
                Durability = 321,
                SealFlag = 1,
                EnchantCardId = 10015142,
                AmplifyType = 3,
                AmplifyValue = 0x1234,
                RandomOptionState = 4,
                RandomOptionChangedIndex = 1,
                RandomOptionChangeState = 5,
                GenuineUpgrade = 7,
            };
            equipment.RandomOption0.Type = 0x11;
            equipment.RandomOption0.Value1 = 0x12;
            equipment.RandomOption0.Value2 = 0x13;
            equipment.RandomOption1.Type = 0x21;
            equipment.RandomOption1.Value1 = 0x22;
            equipment.RandomOption1.Value2 = 0x23;
            equipment.RandomOption2.Type = 0x31;
            equipment.RandomOption2.Value1 = 0x32;
            equipment.RandomOption2.Value2 = 0x33;
            equipment.RandomOptionChange.Type = 0x41;
            equipment.RandomOptionChange.Value1 = 0x42;
            equipment.RandomOptionChange.Value2 = 0x43;
            var equipmentBundle = new AuctionListingBundle
            {
                Listing = new AuctionListingRecord
                {
                    ListingId = listingId + 1,
                    ItemId = equipment.ItemId,
                    ItemKind = equipment.ItemKind,
                    Quantity = 1,
                    UnitPrice = unitPrice,
                    TotalPrice = unitPrice,
                    Status = AuctionListingStatus.Active,
                },
                Escrow = new AuctionEscrowItemRecord
                {
                    ListingId = listingId + 1,
                    ItemCore = equipment.ToBytes(),
                    Quantity = 1,
                },
            };
            var equipmentBody =
                AuctionMyRegisteredItemsAckBuilder.BuildSuccess(
                    0,
                    new[] { equipmentBundle });
            var descriptorOffset = 3 + 30;
            Check("BC equipment descriptor maps upgrade, seal and enchant",
                equipmentBody[descriptorOffset] == equipment.SealFlag
                && equipmentBody[descriptorOffset + 5] == equipment.Attr
                && BitConverter.ToInt32(
                    equipmentBody,
                    descriptorOffset + 12) == equipment.EnchantCardId,
                ref failures);
            Check("BC equipment descriptor maps amplification and forge",
                equipmentBody[descriptorOffset + 16] == equipment.AmplifyType
                && BitConverter.ToUInt16(
                    equipmentBody,
                    descriptorOffset + 17) == equipment.AmplifyValue
                && equipmentBody[descriptorOffset + 51]
                    == equipment.GenuineUpgrade,
                ref failures);
            Check("BC equipment descriptor maps the random-option instance block",
                equipmentBody
                    .Skip(descriptorOffset + 36)
                    .Take(15)
                    .SequenceEqual(new byte[]
                    {
                        0x11, 0x12, 0x13,
                        0x21, 0x22, 0x23,
                        0x31, 0x32, 0x33,
                        0x04, 0x01, 0x05,
                        0x41, 0x42, 0x43,
                    }),
                ref failures);
            Check("BC equipment descriptor keeps instance value and durability",
                BitConverter.ToInt32(
                    equipmentBody,
                    descriptorOffset + 6) == equipment.InstanceValue
                && BitConverter.ToUInt16(
                    equipmentBody,
                    descriptorOffset + 10) == equipment.Durability,
                ref failures);
        }

        private static byte[] CapturedRegisterItemRequest()
            => new byte[]
            {
                0x00, 0x00,
                0x49, 0x00,
                0xC4, 0xD1, 0x98, 0x00,
                0x01, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF,
                0x07, 0xB2, 0x01, 0x00,
                0x07, 0xB2, 0x01, 0x00,
                0x18, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };

        private static void Check(string label, bool passed, ref int failures)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed)
                failures++;
        }
    }
}
