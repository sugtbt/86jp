using DfoServer.Game.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class SpecialDungeonEffectRouter
    {
        private readonly SpecialDungeonNotificationSender _sender;

        internal SpecialDungeonEffectRouter(
            SpecialDungeonNotificationSender sender = null)
        {
            _sender = sender ?? new SpecialDungeonNotificationSender();
        }

        internal async Task RouteAsync(
            EnhancedClientSession session,
            DungeonRun run,
            IReadOnlyList<SpecialDungeonEffectIntent> effects,
            bool allowEndingRun = false)
        {
            if (session == null
                || run == null
                || effects == null
                || effects.Count == 0)
            {
                return;
            }

            for (var index = 0; index < effects.Count; index++)
            {
                if (!CanProject(session, run, allowEndingRun))
                    return;

                var effect = effects[index];
                if (effect == null)
                    continue;

                if (ApplyStateEffect(run, effect))
                    continue;

                await _sender.SendAsync(session, effect);
            }
        }

        private static bool ApplyStateEffect(
            DungeonRun run,
            SpecialDungeonEffectIntent effect)
        {
            switch (effect.Kind)
            {
                case SpecialDungeonEffectKind.CancelMechanismTimer:
                    DungeonMechanismTimerCoordinator.Cancel(run);
                    return true;

                case SpecialDungeonEffectKind.ResetTimeCrackGauge:
                    lock (run.SyncRoot)
                        run.Mechanisms.SpecialDungeon?.ResetTimeCrackGauge();
                    return true;

                case SpecialDungeonEffectKind.RecordSeaChaseBuffs:
                    lock (run.SyncRoot)
                    {
                        run.Mechanisms.SpecialDungeon
                            ?.NoteSeaChaseBuffsApplied(effect.BuffIds);
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static bool CanProject(
            EnhancedClientSession session,
            DungeonRun run,
            bool allowEndingRun)
        {
            var player = session?.Player;
            if (player == null || run == null)
                return false;
            if (player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return true;

            return allowEndingRun
                && player.CurrentRun == null
                && player.CurrentDungeonRunGeneration == run.RunGeneration;
        }
    }
}
