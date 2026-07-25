using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        internal const string CharmQuickSlotLimitNoticeMessage = "\u5FEB\u6377\u680F\u6700\u591A\u53EA\u80FD\u653E\u7F6E1\u4E2A\u7B26\u5492\u3002";

        public async Task Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 14)
            {
                if (body != null && body.Length >= 4)
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                        MoveItemSpaceAckBuilder.BuildError(0x04, body[0], body.Length > 11 ? body[11] : body[0])));
                return;
            }

            var request = new InventoryMoveRequest
            {
                SourceListType = (InventoryListType)body[0],
                SourceSlotIndex = BitConverter.ToInt16(body, 1),
                SourceInstanceValue = BitConverter.ToInt32(body, 3),
                MoveCount = BitConverter.ToInt32(body, 7),
                DestinationListType = (InventoryListType)body[11],
                DestinationSlotIndex = BitConverter.ToInt16(body, 12),
                DestinationInstanceValue = body.Length >= 18 ? BitConverter.ToInt32(body, 14) : 0,
            };

            var srcIV = BitConverter.ToInt32(body, 3);
            var srcStack = BitConverter.ToInt32(body, 7);
            var dstStack = body.Length >= 22 ? BitConverter.ToInt32(body, 18) : 0;
            FileLogger.Log($"[{ProtocolName}] MOVE raw({body.Length}B): {BitConverter.ToString(body)}");
            FileLogger.Log($"[{ProtocolName}] MOVE fields: src=({request.SourceListType},slot{request.SourceSlotIndex},IV=0x{srcIV:X8},stk{srcStack}) dst=({request.DestinationListType},slot{request.DestinationSlotIndex},IV=0x{request.DestinationInstanceValue:X8},stk{dstStack})");

            var (cid, _) = ResolveOwner(session);
            if (!InventoryContext.TryGetLease(cid, out var lease) || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: ONLINE_INVENTORY_MISSING characterId={cid} session={session?.SessionId}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                    MoveItemSpaceAckBuilder.BuildError(0x04, (byte)request.SourceListType, (byte)request.DestinationListType)));
                return;
            }

            InventoryMoveServiceResult moveResult;
            bool moveOk;
            lock (lease.SyncRoot)
                moveOk = InventoryMoveService.TryMove(
                    lease.Inventory,
                    request,
                    session.Player.Job,
                    session.Player.GrowType,
                    out moveResult);

            if (!moveOk)
            {
                if (moveResult != null && moveResult.Error == InventoryMoveServiceError.ClientEcho)
                {
                    FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: CLIENT_ECHO ack-error src=({request.SourceListType},{request.SourceSlotIndex}) dst=({request.DestinationListType},{request.DestinationSlotIndex})");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                        MoveItemSpaceAckBuilder.BuildError(0x02, (byte)request.SourceListType, (byte)request.DestinationListType)));
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: FAILED src=({request.SourceListType},{request.SourceSlotIndex}) dst=({request.DestinationListType},{request.DestinationSlotIndex}) error={moveResult?.Error} insertError={moveResult?.InsertError}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                    MoveItemSpaceAckBuilder.BuildError(MapMoveErrorCode(moveResult), (byte)request.SourceListType, (byte)request.DestinationListType)));
                return;
            }

            var ackResult = CreateMoveAckResult(request, moveResult);
            FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: OK src=({ackResult.SourceListType},{ackResult.SourceSlotIndex}) dst=({ackResult.DestinationListType},{ackResult.DestinationSlotIndex}) moveVal={ackResult.MoveValue32}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013, MoveItemSpaceAckBuilder.Build(ackResult)));
            await SendMoveSortLockSignals(session, lease, moveResult);

            if (ShouldSendSubtype0AppearanceUpdate(session, moveResult))
                await _refresh.SendNoti2AppearanceUpdate(session);
        }

        private static InventoryMoveResult CreateMoveAckResult(InventoryMoveRequest request, InventoryMoveServiceResult result)
        {
            return new InventoryMoveResult
            {
                SourceListType = request.SourceListType,
                SourceSlotIndex = request.SourceSlotIndex,
                MoveValue32 = result != null && result.Mutated ? result.MoveCount : 0,
                DestinationListType = result != null ? result.DestinationListType : request.DestinationListType,
                DestinationSlotIndex = result != null ? result.DestinationSlotIndex : request.DestinationSlotIndex,
                Mutated = result != null && result.Mutated,
            };
        }

        private static byte MapMoveErrorCode(InventoryMoveServiceResult result)
        {
            if (result == null)
                return 0x04;

            switch (result.Error)
            {
                case InventoryMoveServiceError.InvalidDestinationSlot:
                case InventoryMoveServiceError.DestinationRejected:
                case InventoryMoveServiceError.CannotStack:
                case InventoryMoveServiceError.CannotSplitSameSpaceStack:
                case InventoryMoveServiceError.CannotSwap:
                    return 0x02;
                default:
                    return 0x04;
            }
        }

        private static bool ShouldSendSubtype0AppearanceUpdate(EnhancedClientSession session, InventoryMoveServiceResult result)
        {
            if (result == null || !result.Mutated || result.Changes == null)
                return false;

            foreach (var change in result.Changes.Slots)
            {
                if (ShouldSendSubtype0AppearanceUpdateForSlot(session, change.ListType, change.SlotIndex))
                    return true;
            }

            return false;
        }

        private static bool ShouldSendSubtype0AppearanceUpdateForSlot(
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex)
        {
            return listType == InventoryListType.Equipment
                && !ShouldUseDungeonEquipmentOnlyRefresh(session, slotIndex)
                && IsAppearanceEquipmentSlot(slotIndex);
        }

        private static bool ShouldUseDungeonEquipmentOnlyRefresh(EnhancedClientSession session, short slot)
        {
            return session?.Player?.CurrentRun != null
                && (slot == (short)EquipmentType.Weapon
                    || slot == (short)EquipmentType.TitleName);
        }

        private static bool IsAppearanceEquipmentSlot(short slot)
        {
            // 客户端收到移动应答后不会自行刷新外观，这些槽位变化必须跟发 subtype0。
            // 装扮0-10、武器11、称号12、宠物24、名称装饰卡28。
            return (slot >= (short)EquipmentType.HatAvatar && slot <= (short)EquipmentType.TitleName)
                || slot == (short)EquipmentType.Creature
                || slot == (short)EquipmentType.NameTag;
        }

        public async Task Handle_ENUM_CMDPACKET_SORT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 2)
                return;

            var listType = (InventoryListType)body[0];
            byte category = body[1];
            byte condition = body.Length > 2 ? body[2] : (byte)0;
            FileLogger.Log($"[{ProtocolName}] SORT_ITEM raw({body.Length}B): {BitConverter.ToString(body)}  listType={listType} category={category} condition={condition}(ignored)");

            var (cid, _) = ResolveOwner(session);
            try
            {
                if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                    return;

                InventorySortServiceResult sortResult;
                bool ok;
                lock (lease.SyncRoot)
                    ok = InventorySortService.TrySort(lease.Inventory, listType, category, out sortResult);

                FileLogger.Log($"[{ProtocolName}] SORT: InventorySortService.TrySort({listType}, cat={category})={ok} mutated={sortResult?.Mutated ?? false} affected={sortResult?.AffectedSlotCount ?? 0}");
                if (!ok)
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0014, SortItemAckBuilder.Build(listType)));
                await _refresh.SendItemListRefresh(session, listType);
                await _refresh.SendEquipmentItemLockListRefresh(session, listType);
                FileLogger.Log($"[{ProtocolName}] SORT: ack + ITEM_LIST sent, done");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] SORT EXCEPTION: {ex}");
                throw;
            }
        }

        public async Task Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return;

            var listType = (InventoryListType)body[0];
            var slotIndex = BitConverter.ToInt16(body, 1);
            var (cid, _) = ResolveOwner(session);

            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            SortItemLockEntry entry;
            lock (lease.SyncRoot)
            {
                if (!InventoryLockService.TryToggleSortItemLock(lease.Inventory, listType, slotIndex, out entry))
                    return;
            }

            if (entry == null)
                return;

            if (entry.State == 0)
            {
                await SendSortItemUnlockAckAndRefresh(session, listType, slotIndex);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return;

            var listType = (InventoryListType)body[0];
            var slotIndex = BitConverter.ToInt16(body, 1);
            var (cid, _) = ResolveOwner(session);

            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            lock (lease.SyncRoot)
                InventoryLockService.TryUnlockSortItemLock(lease.Inventory, listType, slotIndex);

            await SendSortItemUnlockAckAndRefresh(session, listType, slotIndex);
        }

        private static bool TryGetOwnedInventoryLease(EnhancedClientSession session, int characterId, out InventoryLease lease)
        {
            if (InventoryContext.TryGetLease(characterId, out lease) && lease.IsOwnedBy(session.SessionId))
                return true;

            FileLogger.Log($"[GameProtocol] inventory lease missing characterId={characterId} session={session?.SessionId}");
            return false;
        }

        private async Task SendSortItemUnlockAckAndRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            var notiListType = InventoryRefreshSender.MapToSortLockListType(listType);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CB, SortItemLockBuilder.BuildUnlock(notiListType, slotIndex)));
        }

        private async Task SendMoveSortLockSignals(EnhancedClientSession session, InventoryLease lease, InventoryMoveServiceResult result)
        {
            if (lease == null || result == null || !result.Mutated || result.Changes == null || !result.Changes.HasChanges)
                return;

            var lockedSlots = new List<SortItemLockEntry>();
            lock (lease.SyncRoot)
            {
                foreach (var change in result.Changes.Slots)
                {
                    if (!ShouldSendSortLockSignal(change.ListType, change.SlotIndex))
                        continue;

                    var item = lease.Inventory.GetItem(change.ListType, change.SlotIndex);
                    if (item == null || item.SortLockFlag != 1)
                        continue;

                    lockedSlots.Add(new SortItemLockEntry
                    {
                        ListType = InventoryRefreshSender.MapToSortLockListType(change.ListType),
                        SlotIndex = change.SlotIndex,
                        State = 1,
                    });
                }
            }

            foreach (var entry in lockedSlots)
            {
                FileLogger.Log($"[{ProtocolName}] MOVE sort-lock refresh: list={entry.ListType} slot={entry.SlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
            }
        }

        private static bool ShouldSendSortLockSignal(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Equipment)
                return false;

            if (listType == InventoryListType.Main
                && slotIndex >= ItemSlotBoundService.MainQuickSlotStart
                && slotIndex <= ItemSlotBoundService.MainQuickSlotEnd)
                return false;

            return true;
        }
    }
}
