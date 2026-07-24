using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            EquipmentItemLockResult result;
            bool ok;
            lock (lease.SyncRoot)
            {
                var equipmentLockId = InventoryEquipmentLockTableService.AllocateLockId(cid, lease.Inventory);
                ok = InventoryLockService.TryLockEquipmentItem(lease.Inventory, listType, slotIndex, equipmentLockId, out result);
                if (ok && result.Success
                    && !InventoryEquipmentLockTableService.UpsertLock(cid, result.EquipmentLockId, result.ListType, result.SlotIndex, state: 1, remainingSeconds: null))
                {
                    InventoryLockService.SetEquipmentLockId(lease.Inventory, result.ListType, result.SlotIndex, 0);
                    MarkEquipmentLockPersistenceFailed(result);
                    ok = false;
                }
                else if (ok && result.Success)
                {
                    lease.Inventory.EquipmentLocks.Attach(new EquipmentItemLock
                    {
                        EquipmentLockId = result.EquipmentLockId,
                        State = 1,
                        RemainingSeconds = 0,
                    });
                }
            }

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010B,
                    EquipmentItemLockBuilder.BuildLockError(result?.ErrorCode ?? (byte)19)));
                return;
            }

            SaveEquipmentLockItemCore(lease, "ITEM_LOCK");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010B,
                EquipmentItemLockBuilder.BuildLockAck(result.ListType, result.SlotIndex)));
            await SendEquipmentItemLockEntryRefresh(session, result, 1, "ITEM_LOCK_LIST_DELTA_LOCK");
        }

        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            EquipmentItemLockResult result;
            bool ok;
            lock (lease.SyncRoot)
            {
                ok = InventoryLockService.TryUnlockEquipmentItem(lease.Inventory, listType, slotIndex, out result);
                if (ok && result.Success
                    && !InventoryEquipmentLockTableService.DeleteLock(cid, result.EquipmentLockId))
                {
                    InventoryLockService.SetEquipmentLockId(lease.Inventory, result.ListType, result.SlotIndex, result.EquipmentLockId);
                    MarkEquipmentLockPersistenceFailed(result);
                    ok = false;
                }
                else if (ok && result.Success)
                {
                    lease.Inventory.EquipmentLocks.Remove(result.EquipmentLockId);
                }
            }

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010C,
                    EquipmentItemLockBuilder.BuildUnlockError(result?.ErrorCode ?? (byte)19, result?.RemainingSeconds ?? 0)));
                return;
            }

            SaveEquipmentLockItemCore(lease, "ITEM_UNLOCK");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010C,
                EquipmentItemLockBuilder.BuildUnlockAck(result.ListType, result.SlotIndex, 0)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FC,
                EquipmentItemLockBuilder.BuildUnlockNotice(result.ListType, result.SlotIndex)));
        }

        public async Task Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseEquipmentItemLockRequest(body, out var listType, out var slotIndex))
                return;

            var (cid, _) = ResolveOwner(session);
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
                return;

            EquipmentItemLockResult result;
            bool ok;
            lock (lease.SyncRoot)
            {
                ok = InventoryLockService.TryCancelEquipmentItemUnlock(lease.Inventory, listType, slotIndex, out result);
                if (ok && result.Success
                    && !InventoryEquipmentLockTableService.UpsertLock(cid, result.EquipmentLockId, result.ListType, result.SlotIndex, state: 1, remainingSeconds: null))
                {
                    MarkEquipmentLockPersistenceFailed(result);
                    ok = false;
                }
                else if (ok && result.Success)
                {
                    lease.Inventory.EquipmentLocks.Attach(new EquipmentItemLock
                    {
                        EquipmentLockId = result.EquipmentLockId,
                        State = 1,
                        RemainingSeconds = 0,
                    });
                }
            }

            if (!ok || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010D,
                    EquipmentItemLockBuilder.BuildLockError(result?.ErrorCode ?? (byte)19)));
                return;
            }

            SaveEquipmentLockItemCore(lease, "ITEM_UNLOCK_CANCEL");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x010D,
                EquipmentItemLockBuilder.BuildUnlockCancelAck(result.ListType, result.SlotIndex)));
            await SendEquipmentItemLockEntryRefresh(session, result, 1, "ITEM_LOCK_LIST_DELTA_CANCEL");
        }

        private async Task SendEquipmentItemLockEntryRefresh(EnhancedClientSession session, EquipmentItemLockResult result, byte state, string tag)
        {
            var entries = new[]
            {
                new EquipmentItemLockEntry
                {
                    ListType = result.ListType,
                    SlotIndex = result.SlotIndex,
                    State = state,
                    RemainingSeconds = state == 2 ? result.RemainingSeconds : 0
                }
            };

            InventoryRefreshSender.LogEquipmentItemLockList(tag, entries);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(entries)));
        }

        private static void SaveEquipmentLockItemCore(InventoryLease lease, string action)
        {
            if (!InventoryPersistenceService.SaveDirty(lease))
                FileLogger.Log($"[GameProtocol] {action}: equipment lock item_core save failed cid={lease?.CharacterId}");
        }

        private static void MarkEquipmentLockPersistenceFailed(EquipmentItemLockResult result)
        {
            if (result == null)
                return;

            result.Success = false;
            result.ErrorCode = 19;
        }

        private static bool TryParseEquipmentItemLockRequest(byte[] body, out InventoryListType listType, out short slotIndex)
        {
            listType = InventoryListType.Main;
            slotIndex = 0;
            if (body == null || body.Length < 3)
                return false;

            listType = (InventoryListType)body[0];
            slotIndex = BitConverter.ToInt16(body, 1);
            return true;
        }

        private void LogEquipmentItemLockList(string tag, System.Collections.Generic.IReadOnlyList<EquipmentItemLockEntry> locks)
        {
            var builder = new StringBuilder();
            builder.Append($"[{ProtocolName}] {tag}: count={locks?.Count ?? 0}");
            if (locks != null)
            {
                foreach (var item in locks)
                    builder.Append($" ({item.ListType},{item.SlotIndex},state={item.State},remain={item.RemainingSeconds})");
            }

            FileLogger.Log(builder.ToString());
        }
    }
}
