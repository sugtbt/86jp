using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonNpcItemDropCoordinator
    {
        internal sealed class QuestMatch
        {
            internal int QuestId { get; set; }
            internal IReadOnlyList<int> ItemIds { get; set; }
        }

        internal static async Task HandleCommandAsync(
            EnhancedClientSession session,
            NpcItemDropDungeonCommand command,
            DropService drops,
            DungeonEventEnvelope sourceEvent)
        {
            try
            {
                await TryGenerateDropAsync(
                    session,
                    command,
                    drops,
                    sourceEvent);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"error={ex.Message}");
            }

            if (session != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    command?.WireType
                        ?? (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
            }
        }

        private static async Task TryGenerateDropAsync(
            EnhancedClientSession session,
            NpcItemDropDungeonCommand command,
            DropService drops,
            DungeonEventEnvelope sourceEvent)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || command == null
                || drops == null
                || !IsCurrentEvent(session, sourceEvent))
                return;
            if (!run.RewardPolicy.AllowsQuestDrops)
                return;

            if (command.HasUnexpectedPayload)
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop ignored non-empty command: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"body={BitConverter.ToString(command.Payload)}");
                return;
            }

            var mapId = ResolveCurrentMapId(run);
            if (!DungeonNpcItemDropData.TryResolve(
                    mapId,
                    out var scene,
                    out var sceneRejectReason))
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop rejected by map action: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} reason={sceneRejectReason}");
                return;
            }

            var activeQuests = LoadActiveQuests(session.Player.CharacterId);
            var matches = ResolveQuestMatches(
                activeQuests,
                run.DungeonId,
                run.Difficulty,
                session.Player.Job);
            if (!IsCurrentEvent(session, sourceEvent))
                return;
            if (matches.Count != 1)
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop rejected by quest scope: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} activeMatches={matches.Count}");
                return;
            }

            var match = matches[0];
            if (!run.TryMarkNpcItemDropGenerated(match.QuestId))
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop duplicate command acknowledged: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} quest={match.QuestId}");
                return;
            }

            var itemId = match.ItemIds.Count == 1
                ? match.ItemIds[0]
                : match.ItemIds[ServerRandom.Next(match.ItemIds.Count)];
            if (!drops.TryRegisterTemplateDrop(run, itemId, 1, out var drop))
            {
                run.UnmarkNpcItemDropGenerated(match.QuestId);
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop registration failed: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"map={mapId} quest={match.QuestId} item={itemId}");
                return;
            }

            await DungeonMechanismNotificationSender.SendNpcItemDropAsync(
                session,
                drop,
                scene.X,
                scene.Y,
                match.QuestId,
                scene.ObjectCode,
                scene.ActionPath);
        }

        private static bool IsCurrentEvent(
            EnhancedClientSession session,
            DungeonEventEnvelope sourceEvent)
        {
            if (session?.Player == null || sourceEvent == null)
                return false;
            if (!session.Player.IsCurrentDungeonRun(sourceEvent.RunIdentity))
                return false;

            var run = session.Player.CurrentRun;
            return !sourceEvent.RoomInstanceId.HasValue
                || (run != null
                    && run.CurrentRoomInstanceId == sourceEvent.RoomInstanceId.Value);
        }

        internal static List<QuestMatch> ResolveQuestMatches(
            IReadOnlyList<ActiveQuest> activeQuests,
            int dungeonId,
            int difficulty,
            byte characterJob)
        {
            var result = new List<QuestMatch>();
            if (activeQuests == null || dungeonId <= 0)
                return result;

            foreach (var activeQuest in activeQuests)
            {
                if (activeQuest == null
                    || activeQuest.QuestId == 0
                    || activeQuest.TriggerValue == 0
                    || !QuestData.TryGetNpcItemDropQuestTarget(
                        activeQuest.QuestId,
                        dungeonId,
                        difficulty,
                        out var target))
                {
                    continue;
                }

                var itemIds = new List<int>();
                foreach (var itemId in target.ItemIds)
                {
                    if (ItemMetadataResolver.IsEquipmentUsableByJob(
                            itemId,
                            characterJob))
                    {
                        itemIds.Add(itemId);
                    }
                }

                if (itemIds.Count > 0)
                {
                    result.Add(new QuestMatch
                    {
                        QuestId = activeQuest.QuestId,
                        ItemIds = itemIds,
                    });
                }
            }

            return result;
        }

        private static IReadOnlyList<ActiveQuest> LoadActiveQuests(int characterId)
        {
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                return QuestService.LoadActiveQuests(
                    connectionString,
                    characterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonMechanism] NPC item drop active quest load failed: " +
                    $"cid={characterId} error={ex.Message}");
                return Array.Empty<ActiveQuest>();
            }
        }

        private static int ResolveCurrentMapId(DungeonRun run)
        {
            if (run == null)
                return 0;

            lock (run.SyncRoot)
            {
                if (run.RoomStates.TryGetValue(run.RoomKey, out var roomState)
                    && roomState != null
                    && roomState.Maze.Index > 0)
                {
                    return roomState.Maze.Index;
                }

                return run.RoomKey.OverrideMapId > 0
                    ? run.RoomKey.OverrideMapId
                    : 0;
            }
        }
    }
}
