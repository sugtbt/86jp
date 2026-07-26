using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonHuntMonsterQuestSync
    {
        internal static Task SyncAsync(
            EnhancedClientSession session,
            int monsterCode)
        {
            var run = session?.Player?.CurrentRun;
            var questManager = session?.GameSession?.QuestManager;
            if (run == null
                || questManager == null
                || monsterCode <= 0)
            {
                return Task.CompletedTask;
            }

            return questManager.SyncHuntMonsterQuestProgressAsync(
                run.DungeonId,
                run.Difficulty,
                monsterCode);
        }
    }
}
