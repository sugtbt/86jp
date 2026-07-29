using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class MonsterCardBindHandler
    {
        private const byte InvalidCardCombinationError = 0x11;
        private readonly MonsterCardBindService _service = new MonsterCardBindService();

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length != 6 || session?.Player == null)
            {
                await SendError(session, header.type);
                return;
            }

            var binderSlot = BitConverter.ToInt16(body, 0);
            var firstCardSlot = BitConverter.ToInt16(body, 2);
            var secondCardSlot = BitConverter.ToInt16(body, 4);
            MonsterCardBindResult result = null;
            string rejection = null;
            var ok = InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                && lease.IsOwnedBy(session.SessionId);
            if (ok)
            {
                lock (lease.SyncRoot)
                    ok = _service.TryBind(lease.Inventory, binderSlot, firstCardSlot, secondCardSlot, out result, out rejection);
            }
            else
                rejection = "inventory lease unavailable";

            if (!ok)
            {
                var errorCode = rejection != null
                    && rejection.StartsWith("card rarity mismatch", StringComparison.Ordinal)
                        ? InvalidCardCombinationError
                        : (byte)0x04;
                await SendError(session, header.type, errorCode);
                FileLogger.Log($"[GameProtocol] MONSTERCARD_BIND rejected: cid={session.Player.CharacterId} slots={binderSlot},{firstCardSlot},{secondCardSlot} reason={rejection}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, header.type, MonsterCardBindAckBuilder.BuildSuccess(
                    binderSlot, firstCardSlot, secondCardSlot, result)));
            FileLogger.Log(
                $"[GameProtocol] MONSTERCARD_BIND ok: cid={session.Player.CharacterId} bind={result.BindType} " +
                $"rarity={result.InputRarity}->{result.ResultRarity} item={result.ResultItemId} slot={result.Grant?.SlotIndex} " +
                $"inputs={binderSlot},{firstCardSlot},{secondCardSlot} ack={MonsterCardBindAckBuilder.SuccessLength}B");
        }

        private static Task SendError(EnhancedClientSession session, ushort type, byte errorCode = 0x04)
        {
            if (session == null)
                return Task.CompletedTask;
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, type, MonsterCardBindAckBuilder.BuildError(errorCode)));
        }
    }
}
