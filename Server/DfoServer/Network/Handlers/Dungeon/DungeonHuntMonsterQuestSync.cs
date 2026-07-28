using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonHuntMonsterQuestSync
    {
        internal static Task SyncAsync(
            EnhancedClientSession session,
            int monsterCode,
            DungeonEventEnvelope sourceEvent = null)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || monsterCode <= 0)
            {
                return Task.CompletedTask;
            }
            if (sourceEvent != null
                && !run.Matches(sourceEvent.RunIdentity))
            {
                return Task.CompletedTask;
            }

            sourceEvent ??= DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                "hunt-monster-quest",
                sourceActorCode: monsterCode);
            return DungeonQuestBridge.ApplyAsync(
                session,
                DungeonQuestProgressEvent.HuntMonster(
                    sourceEvent,
                    run.DungeonId,
                    run.Difficulty,
                    monsterCode));
        }
    }
}
