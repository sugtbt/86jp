using DfoServer.Game.Names;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureRenameService
    {
        private const int PetCreatureNameMaxBytes = 13;

        internal static bool TryRenameEquippedPetCreature(
            InventoryService inventory,
            PetCreatureRenameRequest request,
            out PetCreatureRenameResult result)
        {
            result = null;
            if (inventory == null)
            {
                FileLogger.Log("[PetRename] failed: missing online inventory");
                return false;
            }

            if (!IsValidRequest(request, out var failure))
            {
                FileLogger.Log($"[PetRename] failed: invalid request cid={inventory.CharacterId} reason={failure}");
                return false;
            }

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out var equipped, out var detail))
            {
                FileLogger.Log($"[PetRename] failed: no equipped creature cid={inventory.CharacterId}");
                return false;
            }

            if (!inventory.TryGetItem(request.SourceListType, request.SourceSlotIndex, out var source)
                || source == null
                || source.ItemKind != ItemCore.KindCreatureConsumable)
            {
                FileLogger.Log($"[PetRename] failed: source missing cid={inventory.CharacterId} list={request.SourceListType} slot={request.SourceSlotIndex}");
                return false;
            }

            var nameBytes = CopyNameBytes(request.NameBytes);
            detail.NameBytes = nameBytes;
            inventory.CreatureDetails.Put(detail);

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    1,
                    out var delete))
                return false;

            result = new PetCreatureRenameResult
            {
                SourceListType = request.SourceListType,
                SourceSlotIndex = request.SourceSlotIndex,
                PetItemTemplateId = equipped.ItemId,
                CreatureSerial = detail.Uid,
                NameBytes = nameBytes,
                SourceItemConsumed = true,
                SourceRemainingCount = delete.RemainingCount,
            };
            FileLogger.Log($"[PetRename] renamed cid={inventory.CharacterId} key=0x{detail.Uid:X8} item=0x{equipped.ItemId:X8} source=({request.SourceListType},{request.SourceSlotIndex}) remain={delete.RemainingCount}");
            return true;
        }

        private static bool IsValidRequest(
            PetCreatureRenameRequest request,
            out NameInputValidationFailure failure)
        {
            failure = NameInputValidationFailure.None;
            if (request == null)
            {
                failure = NameInputValidationFailure.Null;
                return false;
            }

            if (!PetConsumableService.IsPetConsumableSlot(request.SourceListType, request.SourceSlotIndex))
                return false;

            return NameInputValidator.TryValidateRawName(
                request.NameBytes,
                minBytes: 0,
                maxBytes: PetCreatureNameMaxBytes,
                out _,
                out failure);
        }

        private static byte[] CopyNameBytes(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[Math.Min(nameBytes.Length, PetCreatureNameMaxBytes)];
            Buffer.BlockCopy(nameBytes, 0, copy, 0, copy.Length);
            return copy;
        }
    }
}
