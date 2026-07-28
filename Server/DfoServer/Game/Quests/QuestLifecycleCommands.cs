namespace DfoServer.Game.Quests
{
    internal readonly struct QuestAcceptCommand
    {
        internal QuestAcceptCommand(ushort questId) => QuestId = questId;
        internal ushort QuestId { get; }
    }

    internal readonly struct QuestGiveupCommand
    {
        internal QuestGiveupCommand(ushort questId) => QuestId = questId;
        internal ushort QuestId { get; }
    }

    internal readonly struct QuestFinishCommand
    {
        internal QuestFinishCommand(
            ushort questId,
            bool hasRewardSelection,
            ushort rewardSelectionIndex,
            ushort multiplier)
        {
            QuestId = questId;
            HasRewardSelection = hasRewardSelection;
            RewardSelectionIndex = rewardSelectionIndex;
            Multiplier = multiplier == 0 ? (ushort)1 : multiplier;
        }

        internal ushort QuestId { get; }
        internal bool HasRewardSelection { get; }
        internal ushort RewardSelectionIndex { get; }
        internal ushort Multiplier { get; }
    }
}
