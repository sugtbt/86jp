using DfoServer.Game.Inventory;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ResetItemQualityRequestParser.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemQualityAckBuilder.BuildError(ResetItemQualityResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR raw({body.Length}B): {BitConverter.ToString(body)} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex}");

            var (cid, _) = ResolveOwner(session);
            ResetItemQualityResult result;
            bool ok;
            InventoryLease lease = null;
            if (TryGetOwnedInventoryLease(session, cid, out lease))
            {
                lock (lease.SyncRoot)
                    ok = InventoryEquipmentMutationService.TryResetItemQuality(lease.Inventory, request, out result);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                var errorCode = result != null ? result.ErrorCode : ResetItemQualityResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: FAILED error=0x{errorCode:X2} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemQualityAckBuilder.BuildError(errorCode)));
                return;
            }

            // 成功后主动落库, 保证品质种子强一致。
            if (lease != null)
                InventoryPersistenceService.SaveDirty(lease);

            // 命令 ACK 与 COMPLETE_DISPLAY 刻意分离: 二者同用 0x0051, 但前者 cmd=1。
            // 通用 CMD 状态字节在目标物品 id、容器类型和槽位之前。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.RESET_ITEM_ATTR,
                ResetItemQualityAckBuilder.BuildSuccess(result)));

            await _refresh.SendUpdateItemList(
                session,
                InventoryListType.Main,
                new[] { result.TargetSlotIndex, result.MaterialSlotIndex });

            if (result.MaterialRemainingCount == 0)
                await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: OK mode={result.Mode} targetSlot={result.TargetSlotIndex} material=0x{result.MaterialItemTemplateId:X8}@{result.MaterialSlotIndex} remaining={result.MaterialRemainingCount} quality={result.OldQualitySeed}->{result.NewQualitySeed}");
        }
    }
}
