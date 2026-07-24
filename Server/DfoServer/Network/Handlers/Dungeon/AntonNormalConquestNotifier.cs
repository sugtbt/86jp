using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class AntonNormalConquestNotifier
    {
        private const int LinkedChallengeRate = 100;
        private const int LinkedChallengeCondition = -1;
        private const int SequentialRouteMask = 0;

        private readonly SqliteCharacterStateRepository _repository;

        internal AntonNormalConquestNotifier(
            SqliteCharacterStateRepository repository)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
        {
            if (run == null
                || !AntonNormalConquest.TryGetSequence(
                    run.DungeonId,
                    out _))
            {
                return;
            }

            if (!AntonNormalConquest.TryResolveLinkedNext(
                    run.DungeonId,
                    out var nextDungeonId))
            {
                run.LinkedDungeonNextId = 0;
                run.LinkedDungeonNextRate = 0;
                run.LinkedDungeonNextCondition = 0;
                FileLogger.Log(
                    $"[AntonNormal] linked challenge suppressed: " +
                    $"dungeon={run.DungeonId} reason=main_sequence_final");
                return;
            }

            run.LinkedDungeonNextId = nextDungeonId;
            run.LinkedDungeonNextRate = LinkedChallengeRate;
            run.LinkedDungeonNextCondition = LinkedChallengeCondition;
            FileLogger.Log(
                $"[AntonNormal] linked challenge armed: " +
                $"dungeon={run.DungeonId} next={nextDungeonId} " +
                $"difficulty={run.Difficulty}");
        }

        internal async Task RestoreBeforeSelectAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0)
            {
                return;
            }

            try
            {
                var permissions = _repository.LoadDungeonPermissions(
                    session.Player.CharacterId);
                if (!AntonNormalConquest.TryResolveSyncState(
                        permissions,
                        out var state))
                {
                    return;
                }

                await SendSyncAsync(
                    session,
                    state,
                    "enter-select-dungeon");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] restore skipped: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        internal async Task ApplyClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || run == null
                || !AntonNormalConquest.TryResolveClearPlan(
                    run.DungeonId,
                    out var plan))
            {
                return;
            }

            try
            {
                await ApplyClearCoreAsync(session, run, plan);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] clear sync skipped: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={run.DungeonId} error={ex.Message}");
            }
        }

        private async Task ApplyClearCoreAsync(
            EnhancedClientSession session,
            DungeonRun run,
            AntonNormalClearPlan plan)
        {
            var changes = new List<string>();
            PersistPermission(
                session.Player.CharacterId,
                run.DungeonId,
                plan.Sequence.Difficulty,
                completed: true,
                changes);
            PersistPermission(
                session.Player.CharacterId,
                plan.NextDungeonId,
                plan.Sequence.Difficulty,
                completed: false,
                changes);
            PersistPreviewPermission(
                session.Player.CharacterId,
                plan.PreviewDungeonId,
                plan.Sequence.Difficulty,
                changes);

            var permissions = _repository.LoadDungeonPermissions(
                session.Player.CharacterId);
            if (!AntonNormalConquest.TryResolveSyncState(
                    permissions,
                    out var state)
                || state.Sequence.IndexOf(run.DungeonId) < 0)
            {
                FileLogger.Log(
                    $"[AntonNormal] clear sync skipped: " +
                    $"dungeon={run.DungeonId} reason=state_resolve_failed");
                return;
            }

            await SendSyncAsync(session, state, "dungeon-clear");
            FileLogger.Log(
                $"[AntonNormal] clear applied: " +
                $"dungeon={run.DungeonId} " +
                $"changes={(changes.Count == 0 ? "none" : string.Join(",", changes))} " +
                $"progress={state.ProgressIndex}");
        }

        private void PersistPermission(
            int characterId,
            int dungeonId,
            byte difficulty,
            bool completed,
            ICollection<string> changes)
        {
            if (dungeonId <= 0)
                return;

            var resolved = completed
                ? AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var clearState)
                : AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out clearState);
            if (!resolved)
                return;

            if (_repository.UpsertDungeonPermission(
                    characterId,
                    dungeonId,
                    clearState))
            {
                changes.Add($"{dungeonId}:{clearState}");
            }
        }

        private void PersistPreviewPermission(
            int characterId,
            int dungeonId,
            byte difficulty,
            ICollection<string> changes)
        {
            if (dungeonId <= 0
                || !AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out var unlockedState))
            {
                return;
            }

            var previewState = (byte)Math.Max(1, unlockedState - 1);
            if (_repository.UpsertDungeonPermission(
                    characterId,
                    dungeonId,
                    previewState))
            {
                changes.Add($"{dungeonId}:{previewState}");
            }
        }

        private static async Task SendSyncAsync(
            EnhancedClientSession session,
            AntonNormalSyncState state,
            string source)
        {
            if (state.PermissionEntries.Count > 0)
            {
                var permissionBody =
                    DungeonPermissionBodyBuilder.BuildEntries(
                        state.PermissionEntries);
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketType.DUNGEON_PERMISSION,
                        permissionBody));
            }

            var sequentialBody =
                DungeonNotificationBuilder.BuildSequentialDungeonInfo(
                    state.Sequence.ConfigKey,
                    state.ProgressIndex,
                    SequentialRouteMask);
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.SEQUENTIAL_DUNGEON_INFO,
                    sequentialBody));
            FileLogger.Log(
                $"[AntonNormal] state sent: source={source} " +
                $"key={state.Sequence.ConfigKey} " +
                $"progress={state.ProgressIndex} " +
                $"routeMask={SequentialRouteMask} " +
                $"sequence={string.Join(",", state.Sequence.DungeonIds)} " +
                $"permissions={string.Join(",", state.PermissionEntries.Select(
                    entry => $"{entry.DungeonId}:{entry.ClearState}"))} " +
                $"body={BitConverter.ToString(sequentialBody)}");
        }
    }
}
