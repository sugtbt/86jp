using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using System;
using System.Threading;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Owns timers that belong to optional dungeon mechanisms. Generic run
    // lifecycle code only starts or cancels this coordinator.
    internal static class DungeonMechanismTimerCoordinator
    {
        private const int GentInfiltrateClientTimerSyncGraceSeconds = 4;

        internal static void Start(EnhancedClientSession session, string source)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.GentInfiltrate)
            {
                return;
            }

            Cancel(session);
            var seconds = special.GentInfiltrateTimerSeconds;
            if (seconds <= 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] GENT_INFILTRATE timer skipped " +
                    $"source={source} cid={session.Player.CharacterId} " +
                    $"dungeon={special.DungeonId} reason=no_timer");
                return;
            }

            var version = Interlocked.Increment(ref run.SpecialDungeonTimerVersion);
            if (version == 0)
                version = Interlocked.Increment(ref run.SpecialDungeonTimerVersion);

            var scheduledSeconds =
                seconds + GentInfiltrateClientTimerSyncGraceSeconds;
            var timerName =
                $"special-dungeon:gent-infiltrate:{session.Player.CharacterId}:{run.StartedUtc.Ticks}";
            var handle = ClockService.Instance.ScheduleOneShotAfterAsync(
                timerName,
                TimeSpan.FromSeconds(scheduledSeconds),
                async _ =>
                {
                    if (!IsCurrent(session, run, version))
                        return;

                    await SpecialDungeonNotifier.MarkGentInfiltrateTimeoutAsync(
                        session,
                        "timer");
                });

            StoreHandle(run, version, handle);
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE timer scheduled " +
                $"source={source} cid={session.Player.CharacterId} " +
                $"dungeon={special.DungeonId} configSeconds={seconds} " +
                $"scheduledSeconds={scheduledSeconds} " +
                $"clientSyncGrace={GentInfiltrateClientTimerSyncGraceSeconds} " +
                $"version={version}");
        }

        internal static void Cancel(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            Interlocked.Increment(ref run.SpecialDungeonTimerVersion);
            var handle = Interlocked.Exchange(
                ref run.SpecialDungeonTimerHandle,
                null);
            handle?.Cancel();
        }

        private static bool IsCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            int version)
            => session?.Player != null
                && ReferenceEquals(session.Player.CurrentRun, run)
                && run.SpecialDungeonTimerVersion == version;

        private static void StoreHandle(
            DungeonRun run,
            int version,
            ClockService.ClockTimerHandle handle)
        {
            if (run.SpecialDungeonTimerVersion != version)
            {
                handle.Cancel();
                return;
            }

            var previous = Interlocked.Exchange(
                ref run.SpecialDungeonTimerHandle,
                handle);
            if (previous != null && !ReferenceEquals(previous, handle))
                previous.Cancel();

            if (run.SpecialDungeonTimerVersion != version)
            {
                Interlocked.CompareExchange(
                    ref run.SpecialDungeonTimerHandle,
                    null,
                    handle);
                handle.Cancel();
            }
        }
    }
}
