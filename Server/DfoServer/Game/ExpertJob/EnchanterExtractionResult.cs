using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class EnchanterExtractionMaterial
    {
        internal short SlotIndex { get; set; }
        internal int ItemTemplateId { get; set; }
        internal int Count { get; set; }
    }

    internal sealed class EnchanterExtractionResult
    {
        internal byte ErrorCode { get; set; }
        internal InventoryListType TargetListType { get; set; }
        internal short TargetSlotIndex { get; set; }
        internal int ExperienceGain { get; set; }
        internal uint FinalExperience { get; set; }
        internal List<int> LearnedRecipeIds { get; } = new List<int>();
        internal List<EnchanterExtractionMaterial> Materials { get; } =
            new List<EnchanterExtractionMaterial>();
    }
}
