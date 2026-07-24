using DfoServer.Game.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 0x0051 RESET_ITEM_ATTR (黄金蜜蜡/重新封装装备)。
        // 客户端发包 8 字节: [0-1] Int16 targetSlot  [2-5] Int32 targetItemId  [6-7] Int16 waxSlot
        // 服务端回包 12 字节: [0-3] Int32 result_code  [4-7] Int32 param1(targetItemId)  [8-11] Int32 param2(targetSlot)
        //   result_code: 0=静默失败  1=成功(vtable[0x824]+vtable[0x640] UI刷新)  >=2=错误
        public async Task Handle_RESET_ITEM_ATTR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 8)
            {
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x01, 0x0051, BuildResetItemAttrAck(0, 0, 0)));
                return;
            }

            var targetSlot = BitConverter.ToInt16(body, 0);
            var targetItemId = BitConverter.ToInt32(body, 2);
            var waxSlot = BitConverter.ToInt16(body, 6);

            var (cid, aid) = ResolveOwner(session);
            var store = _inventoryStore as SqliteInventoryStore;

            if (store == null || !store.TryUseWaxForReseal(cid, aid, targetSlot, targetItemId, waxSlot, out var resealResult))
            {
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: failed cid={cid} aid={aid} targetSlot={targetSlot} targetItem=0x{targetItemId:X8} waxSlot={waxSlot}");
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x01, 0x0051, BuildResetItemAttrAck(0, targetItemId, targetSlot)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: OK cid={cid} aid={aid} targetSlot={targetSlot} targetItem=0x{targetItemId:X8} waxSlot={waxSlot} waxCost={resealResult.WaxCost} newSealFlag={resealResult.NewSealFlag} newReSealCount={resealResult.NewReSealCount}");

            // 发送成功回包: result_code=1, param1=targetItemId, param2=targetSlot
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x01, 0x0051, BuildResetItemAttrAck(1, targetItemId, targetSlot)));

            // 刷新受影响槽位(目标装备 + 消耗的蜡)，让客户端读出新的封装次数并显示蜡已消耗
            await _refresh.SendUpdateItemList(session, resealResult.TargetListType, targetSlot);
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, waxSlot);
        }

        private static byte[] BuildResetItemAttrAck(int resultCode, int targetItemId, short targetSlot)
        {
            var w = new GamePacketWriter();
            w.WriteInt32(resultCode);
            w.WriteInt32(targetItemId);
            w.WriteInt32(targetSlot);
            return w.ToArray();
        }
    }
}
