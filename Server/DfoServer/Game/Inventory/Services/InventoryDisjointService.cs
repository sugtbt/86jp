using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryDisjointService
    {
        internal static bool TryDisjointItem(
            InventoryService inventory,
            DisjointItemRequest request,
            out DisjointItemResult result)
        {
            result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidRequest);
            if (inventory == null || request == null || request.TargetSlotIndex < 0)
                return false;

            if (request.ItemSpace != InventoryListType.Main || request.DisjointItemSlotIndex < -1)
                return false;

            var source = inventory.GetItem(InventoryListType.Main, request.TargetSlotIndex);
            if (source == null)
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, source))
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(source.ItemId);
            if (!TryValidateDisjoint(source, metadata, out var errorCode))
            {
                result = CreateErrorResult(request, errorCode);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!TryValidatePortableDisjointItem(
                    inventory,
                    request,
                    metadata,
                    out var disjointTool,
                    out errorCode))
            {
                result = CreateErrorResult(request, errorCode);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            var materials = DisjointResultCalculator.Calculate(metadata);
            if (materials.Count == 0)
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!CanGrantMaterials(inventory, materials))
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInventoryFull);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!inventory.RemoveItem(InventoryListType.Main, request.TargetSlotIndex))
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (disjointTool != null
                && !InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    request.DisjointItemSlotIndex,
                    1,
                    out _))
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            if (!GrantMaterials(inventory, materials))
            {
                result = CreateErrorResult(request, DisjointItemResult.ErrorInventoryFull);
                result.SourceItemTemplateId = source.ItemId;
                return false;
            }

            result = new DisjointItemResult
            {
                Request = request,
                ErrorCode = 0,
                SourceItemTemplateId = source.ItemId,
            };
            result.Materials.AddRange(materials);
            return true;
        }

        private static bool TryValidateDisjoint(ItemCore source, ItemMetadata metadata, out byte errorCode)
        {
            errorCode = DisjointItemResult.ErrorInvalidTarget;
            if (source == null || metadata == null)
                return false;

            if (source.ItemKind != ItemCore.KindEquipment
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            if (ContainsImpossibleContent(metadata, "disjoint"))
                return false;

            if (IsTradeDeleteAttachType(metadata.AttachType))
                return false;

            if (IsUnidentifiedAmplifyEquipment(source))
                return false;

            return true;
        }

        private static bool TryValidatePortableDisjointItem(
            InventoryService inventory,
            DisjointItemRequest request,
            ItemMetadata targetMetadata,
            out ItemCore disjointTool,
            out byte errorCode)
        {
            disjointTool = null;
            errorCode = DisjointItemResult.ErrorInvalidTarget;

            if (request.DisjointItemSlotIndex == -1)
                return true;

            if (request.DisjointItemSlotIndex == request.TargetSlotIndex)
                return false;

            disjointTool = inventory.GetItem(InventoryListType.Main, request.DisjointItemSlotIndex);
            if (disjointTool == null || disjointTool.Count <= 0 || disjointTool.IsEquipmentItem())
                return false;

            if (!ItemMetadataResolver.TryLoadStackableFile(disjointTool.ItemId, out var stackable))
                return false;

            var maxLevel = GetPortableDisjointMaxLevel(stackable.PortableDisjoint);
            if (maxLevel < 0)
                return false;

            var targetLevel = Math.Max(0, targetMetadata?.MinimumLevel ?? 0);
            return targetLevel <= maxLevel;
        }

        private static bool CanGrantMaterials(
            InventoryService inventory,
            IReadOnlyList<DisjointMaterialResult> materials)
        {
            return BuildGrantRequests(materials, out var requests)
                && InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                && plan.Success;
        }

        private static bool GrantMaterials(
            InventoryService inventory,
            IReadOnlyList<DisjointMaterialResult> materials)
        {
            if (!BuildGrantRequests(materials, out var requests)
                || !InventoryRewardGrantService.TryGrantBatch(inventory, requests, out var grantResult)
                || !grantResult.Success
                || grantResult.Results.Count != materials.Count)
                return false;

            for (var index = 0; index < materials.Count; index++)
            {
                var material = materials[index];
                var grant = grantResult.Results[index];
                material.SlotIndex = grant.SlotIndex;
            }

            return true;
        }

        private static bool BuildGrantRequests(
            IReadOnlyList<DisjointMaterialResult> materials,
            out List<InventoryRewardGrantRequest> requests)
        {
            requests = new List<InventoryRewardGrantRequest>();
            if (materials == null)
                return false;

            foreach (var material in materials)
            {
                if (material == null || material.ItemTemplateId <= 0 || material.Count <= 0)
                    return false;

                requests.Add(InventoryRewardGrantRequest.Create(
                    material.ItemTemplateId,
                    material.Count,
                    ItemCreateReason.Unknown));
            }

            return requests.Count > 0;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static bool IsUnidentifiedAmplifyEquipment(ItemCore source)
        {
            return source != null && (source.AmplifyType & 0x80) != 0;
        }

        private static bool ContainsImpossibleContent(ItemMetadata metadata, string expected)
        {
            if (metadata.ImpossibleContents == null)
                return false;

            foreach (var item in metadata.ImpossibleContents)
            {
                if (string.Equals(NormalizePvfToken(item), expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsTradeDeleteAttachType(string attachType)
        {
            return string.Equals(NormalizePvfToken(attachType), "trade delete", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`').Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);

            return normalized.Trim();
        }

        private static int GetPortableDisjointMaxLevel(int portableDisjoint)
        {
            switch (portableDisjoint)
            {
                case 0: return 30;
                case 1: return 50;
                case 2: return 70;
                case 3: return 85;
                default: return -1;
            }
        }

        private static DisjointItemResult CreateErrorResult(DisjointItemRequest request, byte errorCode)
        {
            return new DisjointItemResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
