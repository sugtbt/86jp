using System;
using System.Collections.Generic;

namespace DfoServer.Game.CraneMiniGame
{
    internal sealed class CraneMiniGameSessionCoordinator
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, CraneMiniGameStartResult> _pending = new();

        internal void Set(Guid sessionId, CraneMiniGameStartResult state)
        {
            if (sessionId == Guid.Empty || state == null)
                return;
            lock (_syncRoot)
                _pending[sessionId] = state;
        }

        internal bool TryGet(Guid sessionId, out CraneMiniGameStartResult state)
        {
            lock (_syncRoot)
                return _pending.TryGetValue(sessionId, out state);
        }

        internal bool TryTake(Guid sessionId, out CraneMiniGameStartResult state)
        {
            lock (_syncRoot)
            {
                if (!_pending.TryGetValue(sessionId, out state))
                    return false;
                _pending.Remove(sessionId);
                return true;
            }
        }

        internal void Clear(Guid sessionId)
        {
            lock (_syncRoot)
                _pending.Remove(sessionId);
        }
    }
}
