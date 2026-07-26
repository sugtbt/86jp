using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Thin lifecycle facade. Protocol handlers call this class instead of keeping
    // their own list of enabled dungeon mechanisms.
    internal static class DungeonMechanismCoordinator
    {
        internal sealed class MoveMapContext
        {
            internal MoveMapContext(object state)
            {
                State = state;
            }

            internal object State { get; }
        }

        internal readonly struct ClearRequest
        {
            internal ClearRequest(
                bool shouldClearDungeon,
                string clearReason,
                int bossCode)
            {
                ShouldClearDungeon = shouldClearDungeon;
                ClearReason = clearReason ?? string.Empty;
                BossCode = bossCode;
            }

            internal bool ShouldClearDungeon { get; }
            internal string ClearReason { get; }
            internal int BossCode { get; }
        }

        internal readonly struct CharacterDeathResolution
        {
            internal CharacterDeathResolution(
                bool suppressRespawn,
                ClearRequest clearRequest)
            {
                SuppressRespawn = suppressRespawn;
                ClearRequest = clearRequest;
            }

            internal bool SuppressRespawn { get; }
            internal ClearRequest ClearRequest { get; }
        }

        internal static void OnRunCreated(
            EnhancedClientSession session,
            int dungeonId,
            string source)
            => SpecialDungeonRunCoordinator.InitializeRuntime(
                session,
                dungeonId,
                source);

        internal static Task ClearRunEffectsAsync(
            EnhancedClientSession session,
            string reason)
            => SpecialDungeonNotifier.ClearRunBuffsAsync(session, reason);

        internal static void CancelRunTimers(EnhancedClientSession session)
            => DungeonMechanismTimerCoordinator.Cancel(session);

        internal static void ConfigureSelection(
            EnhancedClientSession session,
            MazeInfo maze,
            int[] bossPosition,
            IReadOnlyList<ActiveQuest> activeQuests,
            string source)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            SpecialDungeonRunCoordinator.ConfigureSelection(
                run,
                maze,
                bossPosition,
                activeQuests);
            ScriptedFatalEndpointCoordinator.ConfigureSelection(
                run,
                maze,
                bossPosition,
                activeQuests);
            DungeonMechanismTimerCoordinator.Start(session, source);
        }

        internal static void CloneSelection(
            EnhancedClientSession session,
            DungeonRun sourceRun,
            DungeonRun targetRun,
            string source)
        {
            SpecialDungeonRunCoordinator.CloneSelectionState(sourceRun, targetRun);
            ScriptedFatalEndpointCoordinator.CloneSelection(sourceRun, targetRun);
            DungeonMechanismTimerCoordinator.Start(session, source);
        }

        internal static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            ResolveSelectionMinimapIconGroups(
                DungeonRun run,
                int dungeonId,
                int mazeIndex)
            => SpecialDungeonRunCoordinator.ResolveMinimapIconGroups(
                run,
                dungeonId,
                mazeIndex);

        internal static Task SendSelectionStateAsync(
            EnhancedClientSession session,
            string reason)
            => SpecialDungeonNotifier.SendBossEntranceMinimapIconInfoAsync(
                session,
                reason);

        internal static MoveMapContext ApplyMoveTargetOverride(
            EnhancedClientSession session,
            int requestedX,
            int requestedY,
            ref DungeonRoomPoint moveTarget)
        {
            var timeSpiral = TimeSpiralDungeonCoordinator.ApplyTeleportOverride(
                session,
                requestedX,
                requestedY,
                ref moveTarget);
            return timeSpiral == null ? null : new MoveMapContext(timeSpiral);
        }

        internal static void ApplyMapOverride(
            EnhancedClientSession session,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
            => SpecialDungeonRunCoordinator.TryApplyGentWarpOverride(
                session,
                moveTarget,
                ref overrideMapId);

        internal static void CopyMoveStateForParty(
            DungeonRun leaderRun,
            DungeonRun memberRun)
            => TimeSpiralDungeonCoordinator.CopyTeleportStateForPartyMove(
                leaderRun,
                memberRun);

        internal static void OnMoveMapCompleted(
            EnhancedClientSession session,
            MoveMapContext context,
            string source)
            => TimeSpiralDungeonCoordinator.LogDeferredBuff(
                session,
                context?.State as TimeSpiralDungeonCoordinator.TeleportMoveContext,
                source);

        internal static int ResolveStartMapOverride(
            DungeonRun run,
            int nextX,
            int nextY,
            int requestedOverrideMapId)
            => SpecialDungeonRunCoordinator.ResolveStartMapOverride(
                run,
                nextX,
                nextY,
                requestedOverrideMapId);

        internal static void RestoreRoomState(
            DungeonRun run,
            RoomState roomState)
            => TimeSpiralDungeonCoordinator.RestoreHiddenBoss(run, roomState);

        internal static void AppendStartMapActors(
            EnhancedClientSession session,
            DungeonData.MazeSumInfo maze)
            => SpecialDungeonRunCoordinator.AppendStartMapActors(session, maze);

        internal static void OnRoomStateCreated(
            EnhancedClientSession session,
            RoomState roomState)
            => TimeSpiralDungeonCoordinator.RegisterHiddenBossAfterStartMap(
                session,
                roomState);

        internal static async Task OnStartMapSentAsync(
            EnhancedClientSession session)
        {
            // Preserve the established order: gauge state first, then the
            // scene condition that depends on client START_MAP actors.
            await SpecialDungeonNotifier.SendStartMapStateAsync(session);
            await EventMonsterConditionCoordinator.AdvanceAfterStartMapAsync(session);
        }

        internal static async Task<ClearRequest> OnMonsterKilledAsync(
            EnhancedClientSession session,
            ushort sequenceId,
            int monsterCode,
            byte monsterType)
        {
            ScriptedFatalEndpointCoordinator.OnMonsterKilled(
                session,
                monsterCode);
            await SpecialDungeonNotifier.ObserveMonsterKilledAsync(
                session,
                monsterCode,
                monsterType);

            var run = session?.Player?.CurrentRun;
            if (run == null)
                return default;

            RoomState roomState;
            bool hiddenBossKilled;
            lock (run.SyncRoot)
            {
                run.RoomStates.TryGetValue(run.RoomKey, out roomState);
                hiddenBossKilled = TimeSpiralDungeonCoordinator.IsTrackedHiddenBossKill(
                    run,
                    roomState,
                    sequenceId,
                    monsterCode);
            }

            if (!hiddenBossKilled)
                return default;

            var roomX = roomState != null ? roomState.Maze.X : 0;
            var roomY = roomState != null ? roomState.Maze.Y : 0;
            var mapId = roomState != null ? roomState.Maze.Index : 0;
            var reason =
                $"TimeSpiral hidden boss seq={run.TimeSpiralHiddenBossSeqId} " +
                $"code={run.TimeSpiralHiddenBossCode}";
            FileLogger.Log(
                $"[DungeonMechanism] MonsterKilled produced clear request: " +
                $"mechanism=time-spiral-hidden-boss cid={session.Player.CharacterId} " +
                $"dungeon={run.DungeonId} room=({roomX},{roomY}) map={mapId} " +
                $"seq={sequenceId} code={monsterCode}");

            return new ClearRequest(
                shouldClearDungeon: true,
                clearReason: reason,
                bossCode: monsterCode);
        }

        internal static void OnPassiveObjectDestroyed(
            EnhancedClientSession session,
            int objectCode)
            => ScriptedFatalEndpointCoordinator.OnPassiveObjectDestroyed(
                session,
                objectCode);

        internal static CharacterDeathResolution OnCharacterDied(
            EnhancedClientSession session)
        {
            var result =
                ScriptedFatalEndpointCoordinator.OnCharacterDied(session);
            return new CharacterDeathResolution(
                result.SuppressRespawn,
                result.ShouldClearDungeon
                    ? new ClearRequest(
                        shouldClearDungeon: true,
                        clearReason: result.Reason,
                        bossCode: 0)
                    : default);
        }

        internal static ClearRequest OnBossDieCheck(
            EnhancedClientSession session,
            BossDieCheckRequest request)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return default;

            run.SpecialDungeon?.NoteSeizeMoneyBossSeq(request.BossSequence);
            FileLogger.Log(
                $"[DungeonMechanism] BOSS_DIE_CHECK observed: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"kind={run.SpecialDungeon?.Kind.ToString() ?? "none"} " +
                $"uid={request.UserId} bossSeq={request.BossSequence}");

            if (!run.HasBossEntranceConditionalSummon
                || run.Phase != DungeonRunPhase.InProgress
                || !run.ConditionalBossSpawned
                || request.BossSequence != SpecialDungeonNotifier.BossSummonRuntimeKey)
            {
                return default;
            }

            var bossCode = run.ConditionalBossCode;
            if (bossCode <= 0)
                return default;

            return new ClearRequest(
                shouldClearDungeon: true,
                clearReason:
                    $"conditional boss die check " +
                    $"uid={request.UserId} bossSeq={request.BossSequence}",
                bossCode: bossCode);
        }

        internal static Task HandleSummonMonsterCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SpecialDungeonNotifier.HandleBossSummonRequestAsync(
                session,
                header,
                body);

        internal static Task HandleTimerModifyInfoCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SpecialDungeonNotifier.HandleGentInfiltrateTimerModifyInfoAsync(
                session,
                header,
                body);

        internal static Task HandleSeaChaseResultCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SpecialDungeonNotifier.HandleSeaChaseMiniGameResultAsync(
                session,
                header,
                body);

        internal static Task ObserveSeaChaseCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SpecialDungeonNotifier.ObserveSeaChasePacketAsync(
                session,
                header,
                body);

        internal static Task HandleNpcItemDropCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            DropService drops)
            => DungeonNpcItemDropCoordinator.HandleCommandAsync(
                session,
                header,
                body,
                drops);

        internal static Task HandleBreakTrapResultCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => TimeSpiralDungeonCoordinator.HandleBreakTrapResultAsync(
                session,
                header,
                body);

        internal static Task OnResultPreparingAsync(
            EnhancedClientSession session,
            byte[] body)
            => SpecialDungeonSettlementCoordinator.OnResultPreparingAsync(
                session,
                body);
    }
}
