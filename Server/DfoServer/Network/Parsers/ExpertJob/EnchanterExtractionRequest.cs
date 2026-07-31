using System;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal static class EnchanterExtractionRequest
    {
        internal static bool TryParse(byte[] body, out EnchanterExtractionCommand command)
        {
            command = null;
            if (body == null || body.Length != 6)
                return false;

            command = new EnchanterExtractionCommand
            {
                ExtractorType = body[0],
                ExtractorSlotIndex = BitConverter.ToInt16(body, 1),
                TargetListType = (InventoryListType)body[3],
                TargetSlotIndex = BitConverter.ToInt16(body, 4),
            };
            return true;
        }
    }
}
