using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class DungeonRunTimerKeys
    {
        public static readonly RunTimerKey SettlementCardAutoFlow =
            new RunTimerKey("settlement", "card-auto-flow");

        public static readonly RunTimerKey CombatDeathRespawn =
            new RunTimerKey("combat", "death-respawn");

        public static readonly RunTimerKey GentInfiltrateTimeout =
            new RunTimerKey("special-dungeon", "gent-infiltrate-timeout");
    }

    /// <summary>
    /// Identifies one delayed action owned by a single <see cref="DungeonRun"/>.
    /// A mechanism may own multiple purposes, but replacing one purpose never
    /// cancels another purpose accidentally.
    /// </summary>
    public readonly struct RunTimerKey : IEquatable<RunTimerKey>
    {
        public RunTimerKey(string mechanism, string purpose)
        {
            if (string.IsNullOrWhiteSpace(mechanism))
                throw new ArgumentException("A timer mechanism is required.", nameof(mechanism));
            if (string.IsNullOrWhiteSpace(purpose))
                throw new ArgumentException("A timer purpose is required.", nameof(purpose));

            Mechanism = mechanism;
            Purpose = purpose;
        }

        public string Mechanism { get; }
        public string Purpose { get; }

        public bool Equals(RunTimerKey other) =>
            string.Equals(Mechanism, other.Mechanism, StringComparison.Ordinal)
            && string.Equals(Purpose, other.Purpose, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is RunTimerKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(Mechanism ?? string.Empty),
                StringComparer.Ordinal.GetHashCode(Purpose ?? string.Empty));

        public override string ToString() => Mechanism + "/" + Purpose;
    }

    /// <summary>
    /// Captured by a ClockService callback. It is valid only while the
    /// registry still owns the exact key/generation pair.
    /// </summary>
    public readonly struct RunTimerTicket
    {
        internal RunTimerTicket(RunTimerKey key, int generation)
        {
            Key = key;
            Generation = generation;
        }

        public RunTimerKey Key { get; }
        public int Generation { get; }
        public bool IsValid => Generation != 0;
    }

    /// <summary>
    /// Per-run timer ownership. Callers reserve a ticket before scheduling a
    /// ClockService callback, attach the resulting handle afterwards, and have
    /// the callback validate the same ticket before projecting a typed event.
    /// </summary>
    public sealed class RunTimerRegistry
    {
        private sealed class Entry
        {
            internal int Generation;
            internal ClockService.ClockTimerHandle Handle;
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<RunTimerKey, Entry> _entries =
            new Dictionary<RunTimerKey, Entry>();

        public RunTimerTicket Begin(RunTimerKey key)
        {
            ClockService.ClockTimerHandle previous = null;
            RunTimerTicket ticket;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                previous = entry.Handle;
                entry.Handle = null;
                entry.Generation = NextGeneration(entry.Generation);
                ticket = new RunTimerTicket(key, entry.Generation);
            }

            previous?.Cancel();
            return ticket;
        }

        public void Attach(RunTimerTicket ticket, ClockService.ClockTimerHandle handle)
        {
            if (!ticket.IsValid || handle == null)
            {
                handle?.Cancel();
                return;
            }

            ClockService.ClockTimerHandle previous = null;
            var attach = false;
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(ticket.Key, out var entry)
                    && entry.Generation == ticket.Generation)
                {
                    previous = entry.Handle;
                    entry.Handle = handle;
                    attach = true;
                }
            }

            if (!attach)
            {
                handle.Cancel();
                return;
            }

            if (previous != null && !ReferenceEquals(previous, handle))
                previous.Cancel();
        }

        public bool IsCurrent(RunTimerTicket ticket)
        {
            if (!ticket.IsValid)
                return false;

            lock (_syncRoot)
            {
                return _entries.TryGetValue(ticket.Key, out var entry)
                    && entry.Generation == ticket.Generation;
            }
        }

        public void Cancel(RunTimerKey key)
        {
            ClockService.ClockTimerHandle handle = null;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    return;

                entry.Generation = NextGeneration(entry.Generation);
                handle = entry.Handle;
                entry.Handle = null;
            }

            handle?.Cancel();
        }

        public void CancelAll()
        {
            List<ClockService.ClockTimerHandle> handles = null;
            lock (_syncRoot)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Generation = NextGeneration(entry.Generation);
                    if (entry.Handle != null)
                    {
                        (handles ??= new List<ClockService.ClockTimerHandle>())
                            .Add(entry.Handle);
                        entry.Handle = null;
                    }
                }
            }

            if (handles == null)
                return;
            foreach (var handle in handles)
                handle.Cancel();
        }

        public int GetGeneration(RunTimerKey key)
        {
            lock (_syncRoot)
            {
                return _entries.TryGetValue(key, out var entry)
                    ? entry.Generation
                    : 0;
            }
        }

        public bool TryGetCurrentTicket(
            RunTimerKey key,
            out RunTimerTicket ticket)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.Generation != 0)
                {
                    ticket = new RunTimerTicket(key, entry.Generation);
                    return true;
                }
            }

            ticket = default;
            return false;
        }

        private static int NextGeneration(int previous)
        {
            var next = previous == int.MaxValue ? 1 : previous + 1;
            return next == 0 ? 1 : next;
        }
    }
}
