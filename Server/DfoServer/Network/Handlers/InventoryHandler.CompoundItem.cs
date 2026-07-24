using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_COMPOUND_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryParseCompoundItemRequest(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] COMPOUND_ITEM invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CompoundItemAckBuilder.BuildError(17)));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            _inventoryStore.TryCompoundItemRecipe(cid, aid, request, out var result);

            var ackBody = CompoundItemAckBuilder.Build(result);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ackBody));

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] COMPOUND_ITEM failed error={result.ErrorCode} cid={cid} " +
                    $"source={request.SourceValue} byItemId={request.SourceIsItemId} count={request.RequestedCount} " +
                    $"raw={BitConverter.ToString(body ?? Array.Empty<byte>())}");
                return;
            }

            if (result.GoldSpent > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildGoldUpdate(result.UpdatedGold)));
            }

            FileLogger.Log(
                $"[{ProtocolName}] COMPOUND_ITEM ok cid={cid} ackLen={ackBody.Length} " +
                $"recipe=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} count={result.RequestedCount} " +
                $"pvf={result.PvfPath} recipeType={result.RecipeType} " +
                $"deleted=[{string.Join(", ", result.DeletedEntries.Select(e => $"0x{e.ItemTemplateId:X8}x{e.Count}@{e.SlotIndex}"))}] " +
                $"rewards=[{string.Join(", ", result.Rewards.Select(r => $"0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex} total={r.StackCount}"))}]");
        }

        private static bool TryParseCompoundItemRequest(byte[] body, out CompoundItemRecipeRequest request)
        {
            request = null;
            if (body == null || body.Length < 7)
                return false;

            var requestedCount = BitConverter.ToUInt16(body, 5);
            if (requestedCount == 0)
                return false;

            request = new CompoundItemRecipeRequest
            {
                SourceValue = BitConverter.ToInt32(body, 0),
                SourceIsItemId = body[4] == 1,
                RequestedCount = requestedCount,
            };
            return true;
        }
    }
}
