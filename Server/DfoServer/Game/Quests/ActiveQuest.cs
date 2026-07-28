using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    public sealed class ActiveQuest
    {
        public int Slot;
        public ushort QuestId;
        public uint TriggerValue;
        public long Version;
    }

    internal static class QuestActiveListRules
    {
        internal static ActiveQuest FindByQuestId(
            IReadOnlyCollection<ActiveQuest> active,
            ushort questId)
        {
            if (active == null)
                return null;

            foreach (var quest in active)
            {
                if (quest != null && quest.QuestId == questId)
                    return quest;
            }
            return null;
        }

        internal static int FindFreeSlot(IReadOnlyCollection<ActiveQuest> active)
        {
            var used = new HashSet<int>();
            if (active != null)
            {
                foreach (var quest in active)
                {
                    if (quest != null)
                        used.Add(quest.Slot);
                }
            }

            for (var slot = 0; slot < QuestSlotLayout.ActiveSlotCount; slot++)
            {
                if (!used.Contains(slot))
                    return slot;
            }
            return -1;
        }
    }
}
