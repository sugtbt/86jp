using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal static class DisjointMachineRepairService
    {
        internal const byte ErrorCannotRepair = 22;
        internal const byte ErrorInvalidState = 60;

        internal static bool TryRepair(
            InventoryService inventory,
            DisjointMachineState state,
            out DisjointMachineRepairResult result)
        {
            result = new DisjointMachineRepairResult { ErrorCode = ErrorInvalidState };
            var rule = state == null
                ? null
                : DisjointMachineConfigProvider.Config.GetRepairRule(state.MachineGrade);
            if (inventory == null || state == null || rule == null || state.Endurance < 0)
                return false;

            var currentEndurance = Math.Min(state.Endurance, rule.MaximumEndurance);
            var cost = rule.FullRepairCost
                - currentEndurance * rule.FullRepairCost / rule.MaximumEndurance;
            if (cost <= 0)
            {
                result.ErrorCode = ErrorCannotRepair;
                return false;
            }

            var gold = inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (gold < cost)
            {
                var unitCost = rule.FullRepairCost / rule.MaximumEndurance;
                if (unitCost <= 0)
                    return false;

                cost = gold - gold % unitCost;
                var repairedEndurance = cost / unitCost;
                if (cost <= 0 || repairedEndurance <= 0)
                {
                    result.ErrorCode = ErrorCannotRepair;
                    return false;
                }

                currentEndurance = Math.Min(
                    rule.MaximumEndurance,
                    currentEndurance + repairedEndurance);
            }
            else
            {
                currentEndurance = rule.MaximumEndurance;
            }

            gold -= cost;
            if (!inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    gold))
                return false;

            state.Endurance = currentEndurance;
            result = new DisjointMachineRepairResult
            {
                Gold = gold,
                Endurance = state.Endurance,
                Cost = cost,
            };
            return true;
        }
    }
}
