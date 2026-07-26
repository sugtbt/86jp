using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class AuctionHandler
    {
        private readonly AuctionListingService _listingService;
        private readonly AuctionQueryService _queryService;
        private readonly AuctionReturnService _returnService;
        private readonly InventoryRefreshSender _inventoryRefresh;

        public AuctionHandler(
            AuctionListingService listingService,
            AuctionQueryService queryService,
            AuctionReturnService returnService,
            InventoryRefreshSender inventoryRefresh)
        {
            _listingService = listingService
                ?? throw new ArgumentNullException(nameof(listingService));
            _queryService = queryService
                ?? throw new ArgumentNullException(nameof(queryService));
            _returnService = returnService
                ?? throw new ArgumentNullException(nameof(returnService));
            _inventoryRefresh = inventoryRefresh
                ?? throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        public Task HandleAskAveragePriceAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!AuctionAskAveragePriceRequestParser.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[Auction] Rejected malformed average-price request bodyLen={body?.Length ?? 0}");
                return Task.CompletedTask;
            }

            FileLogger.Log(
                $"[Auction] Average-price request itemId={request.ItemTemplateId} mode={request.QueryMode}; returning no-history samples");
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                AuctionAveragePriceAckBuilder.BuildNoHistory()));
        }

        public async Task HandleRegisterItemAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
                return;

            var characterId = session.Player?.CharacterId ?? 0;
            if (!AuctionRegisterItemRequestParser.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    $"[Auction] Rejected malformed fixed-price listing cid={characterId} bodyLen={body?.Length ?? 0}");
                await SendRegisterFailureAsync(
                    session,
                    header.type,
                    characterId,
                    AuctionApplicationError.InvalidTerms);
                return;
            }

            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0
                || characterId <= 0
                || !InventoryContext.TryGetLease(
                    characterId,
                    out var lease)
                || !lease.IsOwnedBy(session.SessionId)
                || lease.CharacterId != characterId
                || lease.AccountId != accountId)
            {
                FileLogger.Log(
                    $"[Auction] Rejected fixed-price listing without owned lease aid={accountId} cid={characterId} slot={request.SourceSlotIndex}");
                await SendRegisterFailureAsync(
                    session,
                    header.type,
                    characterId,
                    AuctionApplicationError.InvalidLease);
                return;
            }

            var result = _listingService.TryCreateListing(
                lease,
                AuctionRegisterItemCommandMapper.Map(request));
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Auction] Fixed-price listing rejected aid={accountId} cid={characterId} slot={request.SourceSlotIndex} itemId={request.ItemTemplateId} quantity={request.Quantity} unitPrice={request.UnitPrice} error={result.Error}");
                await SendRegisterFailureAsync(
                    session,
                    header.type,
                    characterId,
                    result.Error);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                AuctionRegisterItemAckBuilder.BuildSuccess(characterId)));
            await _inventoryRefresh.SendUpdateItemList(
                session,
                InventoryListType.Main,
                new[]
                {
                    request.SourceSlotIndex,
                    InventoryService.MainVirtualCurrencySlotStart,
                });
            FileLogger.Log(
                $"[Auction] Fixed-price listing created listingId={result.ListingId} aid={accountId} cid={characterId} slot={request.SourceSlotIndex} itemId={request.ItemTemplateId} quantity={request.Quantity} unitPrice={request.UnitPrice} total={result.TotalPrice} deposit={result.DepositAmount}");
        }

        public async Task HandleMyRegisteredItemsAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
                return;
            if (!AuctionMyRegisteredItemsRequestParser.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    $"[Auction] Rejected malformed my-registered-items request bodyLen={body?.Length ?? 0}");
                return;
            }

            var accountId = session.Account?.AccountId ?? 0;
            var characterId = session.Player?.CharacterId ?? 0;
            var responseBody =
                AuctionMyRegisteredItemsAckBuilder.BuildEmpty(request.Mode);

            if (request.Mode == 0
                && accountId > 0
                && characterId > 0
                && InventoryContext.TryGetLease(
                    characterId,
                    out var lease)
                && lease.IsOwnedBy(session.SessionId)
                && lease.CharacterId == characterId
                && lease.AccountId == accountId)
            {
                try
                {
                    var listings = _queryService.LoadMyActiveListingBundles(
                        lease);
                    responseBody =
                        AuctionMyRegisteredItemsAckBuilder.BuildSuccess(
                            request.Mode,
                            listings);
                    FileLogger.Log(
                        $"[Auction] My registered items aid={accountId} cid={characterId} mode={request.Mode} count={listings.Count}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[Auction] My registered items failed aid={accountId} cid={characterId}: {ex.Message}");
                }
            }
            else
            {
                FileLogger.Log(
                    $"[Auction] My registered items returned empty aid={accountId} cid={characterId} mode={request.Mode}");
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                responseBody));
        }

        public async Task HandleCancelListingAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
                return;

            var fallbackMode =
                body != null && body.Length > 0 && body[0] <= 1
                    ? body[0]
                    : (byte)0;
            if (!AuctionCancelListingRequestParser.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    $"[Auction] Rejected malformed cancel-listing request bodyLen={body?.Length ?? 0}");
                await SendCancelFailureAsync(
                    session,
                    header.type,
                    fallbackMode,
                    AuctionApplicationError.ListingNotFound);
                return;
            }

            var accountId = session.Account?.AccountId ?? 0;
            var characterId = session.Player?.CharacterId ?? 0;
            if (accountId <= 0
                || characterId <= 0
                || !InventoryContext.TryGetLease(
                    characterId,
                    out var lease)
                || !lease.IsOwnedBy(session.SessionId)
                || lease.CharacterId != characterId
                || lease.AccountId != accountId)
            {
                FileLogger.Log(
                    $"[Auction] Rejected cancel without owned lease aid={accountId} cid={characterId} listingId={request.ListingId}");
                await SendCancelFailureAsync(
                    session,
                    header.type,
                    request.Mode,
                    AuctionApplicationError.InvalidLease);
                return;
            }

            var result = _returnService.TryCancel(
                lease,
                request.ListingId);
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Auction] Cancel rejected aid={accountId} cid={characterId} listingId={request.ListingId} error={result.Error}");
                await SendCancelFailureAsync(
                    session,
                    header.type,
                    request.Mode,
                    result.Error);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                AuctionCancelListingAckBuilder.BuildSuccess(
                    request.Mode)));
            FileLogger.Log(
                $"[Auction] Listing cancelled listingId={result.ListingId} aid={accountId} cid={characterId} status={result.Status}");
        }

        private static async Task SendRegisterFailureAsync(
            EnhancedClientSession session,
            ushort type,
            int characterId,
            AuctionApplicationError error)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                type,
                AuctionRegisterItemAckBuilder.BuildFailure(
                    0x00,
                    characterId)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(
                    AuctionRegisterItemFailureReasonMapper.Map(error))));
        }

        private static async Task SendCancelFailureAsync(
            EnhancedClientSession session,
            ushort type,
            byte mode,
            AuctionApplicationError error)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                type,
                AuctionCancelListingAckBuilder.BuildFailure(mode)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(
                    AuctionCancelListingFailureReasonMapper.Map(error))));
        }
    }
}
