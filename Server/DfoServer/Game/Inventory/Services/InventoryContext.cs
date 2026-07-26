using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryContext
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<Guid, int> SessionOwnership = new Dictionary<Guid, int>();
        private static readonly Dictionary<int, InventoryLease> CharacterLeases = new Dictionary<int, InventoryLease>();
        private static readonly Dictionary<Guid, object> SessionLifecycleGates =
            new Dictionary<Guid, object>();
        private static readonly Dictionary<int, object> CharacterLifecycleGates =
            new Dictionary<int, object>();
        private static long _nextVersion;

        public static InventoryLease Register(Guid sessionId, InventoryService inventory)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            return Register(sessionId, inventory.CharacterId, inventory);
        }

        public static InventoryLease Register(Guid sessionId, int characterId, InventoryService inventory)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("注册在线背包需要有效的 sessionId。", nameof(sessionId));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "注册在线背包需要有效的角色 ID。");
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            if (inventory.CharacterId > 0 && inventory.CharacterId != characterId)
                throw new ArgumentException("注册在线背包时，参数角色 ID 与背包角色 ID 不一致。", nameof(characterId));

            InventoryLease removedForSession;
            InventoryLease removedForCharacter;
            InventoryLease replacedForSameSession;
            InventoryLease lease;
            object sessionGate;
            lock (SyncRoot)
                sessionGate = GetSessionGateLocked(sessionId);

            lock (sessionGate)
            {
                List<KeyValuePair<int, object>> gates;
                lock (SyncRoot)
                {
                    gates = GetRegisterGatesLocked(
                        sessionId,
                        characterId);
                }

                EnterGates(gates);
                try
                {
                    lock (SyncRoot)
                    {
                        removedForSession =
                            RemovePreviousCharacterForSession(
                                sessionId,
                                characterId);
                        removedForCharacter =
                            RemovePreviousSessionForCharacter(
                                sessionId,
                                characterId);
                        replacedForSameSession =
                            CharacterLeases.TryGetValue(
                                characterId,
                                out var existingLease)
                            && existingLease.IsOwnedBy(sessionId)
                                ? existingLease
                                : null;

                        lease = new InventoryLease(
                            sessionId,
                            characterId,
                            inventory,
                            ++_nextVersion);
                        SessionOwnership[sessionId] = characterId;
                        CharacterLeases[characterId] = lease;
                    }

                    SaveRemovedLease(removedForSession);
                    SaveRemovedLease(removedForCharacter);
                    SaveRemovedLease(replacedForSameSession);
                }
                finally
                {
                    ExitGates(gates);
                }
            }
            return lease;
        }

        public static bool Unregister(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                return false;

            InventoryLease removed = null;
            object sessionGate;
            lock (SyncRoot)
                sessionGate = GetSessionGateLocked(sessionId);

            lock (sessionGate)
            {
                KeyValuePair<int, object> gate;
                lock (SyncRoot)
                {
                    if (!SessionOwnership.TryGetValue(
                            sessionId,
                            out var characterId))
                        return false;
                    gate = new KeyValuePair<int, object>(
                        characterId,
                        GetCharacterGateLocked(characterId));
                }

                Monitor.Enter(gate.Value);
                try
                {
                    lock (SyncRoot)
                    {
                        if (!SessionOwnership.TryGetValue(
                                sessionId,
                                out var currentCharacterId)
                            || currentCharacterId != gate.Key)
                            return false;

                        SessionOwnership.Remove(sessionId);
                        removed = RemoveCharacterLeaseIfOwnedBy(
                            gate.Key,
                            sessionId);
                    }
                    SaveRemovedLease(removed);
                }
                finally
                {
                    Monitor.Exit(gate.Value);
                }
            }
            return removed != null;
        }

        public static bool Unregister(Guid sessionId, int characterId)
        {
            if (sessionId == Guid.Empty || characterId <= 0)
                return false;

            InventoryLease removed = null;
            object sessionGate;
            object gate;
            lock (SyncRoot)
            {
                sessionGate = GetSessionGateLocked(sessionId);
                gate = GetCharacterGateLocked(characterId);
            }

            lock (sessionGate)
            {
                Monitor.Enter(gate);
                try
                {
                    lock (SyncRoot)
                    {
                        if (SessionOwnership.TryGetValue(
                                sessionId,
                                out var ownedCharacterId)
                            && ownedCharacterId == characterId)
                            SessionOwnership.Remove(sessionId);

                        removed = RemoveCharacterLeaseIfOwnedBy(
                            characterId,
                            sessionId);
                    }
                    SaveRemovedLease(removed);
                }
                finally
                {
                    Monitor.Exit(gate);
                }
            }
            return removed != null;
        }

        public static InventoryService Get(int characterId)
        {
            return TryGet(characterId, out var inventory) ? inventory : null;
        }

        public static bool TryGet(int characterId, out InventoryService inventory)
        {
            inventory = null;
            if (characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                if (!CharacterLeases.TryGetValue(characterId, out var lease))
                    return false;

                inventory = lease.Inventory;
                return true;
            }
        }

        public static bool TryGetLease(int characterId, out InventoryLease lease)
        {
            lease = null;
            if (characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                return CharacterLeases.TryGetValue(characterId, out lease);
            }
        }

        public static bool IsCurrentLease(InventoryLease lease)
        {
            if (lease == null
                || lease.SessionId == Guid.Empty
                || lease.CharacterId <= 0)
                return false;

            lock (SyncRoot)
                return IsCurrentLeaseLocked(lease);
        }

        public static bool TryExecuteCurrentLease<TResult>(
            InventoryLease lease,
            Func<InventoryLease, TResult> action,
            out TResult result)
        {
            result = default;
            if (lease == null || action == null)
                return false;

            object gate;
            lock (SyncRoot)
                gate = GetCharacterGateLocked(lease.CharacterId);

            lock (gate)
            {
                lock (SyncRoot)
                {
                    if (!IsCurrentLeaseLocked(lease))
                        return false;
                }

                lock (lease.SyncRoot)
                {
                    result = action(lease);
                    return true;
                }
            }
        }

        public static IReadOnlyList<InventoryLease> GetLeasesSnapshot()
        {
            lock (SyncRoot)
                return new List<InventoryLease>(CharacterLeases.Values);
        }

        public static void SaveAllDirty()
        {
            InventoryPersistenceService.SaveAllDirty();
        }

        public static bool TryGetForSession(Guid sessionId, int characterId, out InventoryService inventory)
        {
            inventory = null;
            if (sessionId == Guid.Empty || characterId <= 0)
                return false;

            lock (SyncRoot)
            {
                if (!SessionOwnership.TryGetValue(sessionId, out var ownedCharacterId)
                    || ownedCharacterId != characterId)
                    return false;
                if (!CharacterLeases.TryGetValue(characterId, out var lease)
                    || !lease.IsOwnedBy(sessionId))
                    return false;

                inventory = lease.Inventory;
                return true;
            }
        }

        private static InventoryLease RemovePreviousCharacterForSession(Guid sessionId, int characterId)
        {
            if (!SessionOwnership.TryGetValue(sessionId, out var oldCharacterId)
                || oldCharacterId == characterId)
                return null;

            SessionOwnership.Remove(sessionId);
            return RemoveCharacterLeaseIfOwnedBy(oldCharacterId, sessionId);
        }

        private static object GetSessionGateLocked(Guid sessionId)
        {
            if (!SessionLifecycleGates.TryGetValue(
                    sessionId,
                    out var gate))
            {
                gate = new object();
                SessionLifecycleGates[sessionId] = gate;
            }
            return gate;
        }

        private static object GetCharacterGateLocked(int characterId)
        {
            if (!CharacterLifecycleGates.TryGetValue(
                    characterId,
                    out var gate))
            {
                gate = new object();
                CharacterLifecycleGates[characterId] = gate;
            }
            return gate;
        }

        private static List<KeyValuePair<int, object>> GetRegisterGatesLocked(
            Guid sessionId,
            int characterId)
        {
            var characterIds = new List<int> { characterId };
            if (SessionOwnership.TryGetValue(
                    sessionId,
                    out var previousCharacterId)
                && previousCharacterId != characterId)
            {
                characterIds.Add(previousCharacterId);
            }
            characterIds.Sort();

            var gates = new List<KeyValuePair<int, object>>(
                characterIds.Count);
            foreach (var id in characterIds)
            {
                gates.Add(new KeyValuePair<int, object>(
                    id,
                    GetCharacterGateLocked(id)));
            }
            return gates;
        }

        private static void EnterGates(
            IReadOnlyList<KeyValuePair<int, object>> gates)
        {
            for (var index = 0; index < gates.Count; index++)
                Monitor.Enter(gates[index].Value);
        }

        private static void ExitGates(
            IReadOnlyList<KeyValuePair<int, object>> gates)
        {
            for (var index = gates.Count - 1; index >= 0; index--)
                Monitor.Exit(gates[index].Value);
        }

        private static bool IsCurrentLeaseLocked(InventoryLease lease)
        {
            return lease != null
                && SessionOwnership.TryGetValue(
                    lease.SessionId,
                    out var characterId)
                && characterId == lease.CharacterId
                && CharacterLeases.TryGetValue(
                    lease.CharacterId,
                    out var current)
                && ReferenceEquals(current, lease);
        }

        private static InventoryLease RemovePreviousSessionForCharacter(Guid sessionId, int characterId)
        {
            if (!CharacterLeases.TryGetValue(characterId, out var oldLease)
                || oldLease.IsOwnedBy(sessionId))
                return null;

            if (SessionOwnership.TryGetValue(oldLease.SessionId, out var oldOwnedCharacterId)
                && oldOwnedCharacterId == characterId)
                SessionOwnership.Remove(oldLease.SessionId);

            CharacterLeases.Remove(characterId);
            return oldLease;
        }

        private static InventoryLease RemoveCharacterLeaseIfOwnedBy(int characterId, Guid sessionId)
        {
            if (!CharacterLeases.TryGetValue(characterId, out var lease)
                || !lease.IsOwnedBy(sessionId))
                return null;

            CharacterLeases.Remove(characterId);
            return lease;
        }

        private static void SaveRemovedLease(InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
        }
    }
}
