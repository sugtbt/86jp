using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class CardRewardCoordinator
    {
        private readonly CardRewardService _application;
        private readonly ICardRewardNotificationSender _sender;

        internal CardRewardCoordinator(
            CardRewardService application = null,
            ICardRewardNotificationSender sender = null)
        {
            _application = application ?? new CardRewardService();
            _sender = sender ?? new CardRewardNotificationSender();
        }

        internal void ScheduleAutoFlow(
            EnhancedClientSession session,
            int layoutDelayMs,
            int autoFlipDelayMs)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            var identity = run.CaptureIdentity();
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow);
            var handle = ClockService.Instance.ScheduleOneShotAfterAsync(
                BuildAutoFlipTimerName(session, run, ticket),
                TimeSpan.FromMilliseconds(layoutDelayMs),
                async _ => await OnLayoutTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket,
                    autoFlipDelayMs));
            run.Timers.Attach(ticket, handle);
        }

        internal void StartDelayedAutoFlip(
            EnhancedClientSession session,
            int delayMs)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow);
            ScheduleAutoFlipTimer(
                session,
                run,
                run.CaptureIdentity(),
                delayMs,
                ticket,
                "Standalone");
        }

        internal async Task HandleSelectCard(
            EnhancedClientSession session,
            byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null || body == null || body.Length < 2)
                return;
            var runIdentity = run.CaptureIdentity();
            var requestedLayout = run.Phase == DungeonRunPhase.ResultShown;
            var cardType = body[0];
            var cardIndex = body[1];

            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;

                if (requestedLayout)
                {
                    if (run.Phase != DungeonRunPhase.ResultShown)
                        return;
                    DungeonRunLifecycle.CancelAutoFlip(session);
                    await _sender.SendLayoutAsync(session);
                    if (!session.Player.IsCurrentDungeonRun(runIdentity)
                        || !run.TryMarkCardsRevealed())
                    {
                        return;
                    }
                    StartDelayedAutoFlip(session, 4000);
                    return;
                }

                if (run.Phase != DungeonRunPhase.CardsRevealed
                    || cardType > 1
                    || cardIndex > 3)
                {
                    return;
                }
                if (cardType == 0)
                    DungeonRunLifecycle.CancelAutoFlip(session);

                if (cardType == 1
                    && cardIndex == 0
                    && (!TryGetOwnedInventory(session, out var paymentLease)
                        || !_application.CanPayPaidCard(paymentLease, run)))
                {
                    await _sender.SendCardInfoAsync(session, run);
                    return;
                }

                if (!CardRewardRules.TrySelectCardSlot(run, cardType, cardIndex))
                {
                    await _sender.SendCardInfoAsync(session, run);
                    return;
                }

                await _sender.SendCardInfoAsync(session, run);
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;
                if (cardIndex == 0)
                {
                    await DeliverCardRewards(
                        session,
                        run,
                        cardType == 0
                            ? CardRewardSide.Free
                            : CardRewardSide.Paid);
                }
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        internal async Task HandleCardStartRequest(
            EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.Phase != DungeonRunPhase.ResultShown)
                return;
            var identity = run.CaptureIdentity();
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity)
                    || run.Phase != DungeonRunPhase.ResultShown)
                {
                    return;
                }
                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendLayoutAsync(session);
                if (!session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryMarkCardsRevealed())
                {
                    return;
                }
                StartDelayedAutoFlip(session, 4000);
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        internal async Task<bool> HandleEplpCommand(
            EnhancedClientSession session,
            byte[] body)
        {
            if (body == null || body.Length < 2)
                return false;
            var state = body[0];
            var option = body[1];
            var run = session.Player.CurrentRun;
            var identity = run?.CaptureIdentity() ?? default;
            if (run == null)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendExitAsync(session, state, option);
                return state == 1;
            }

            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return false;
                if (run.Phase == DungeonRunPhase.ResultShown
                    && run.CardRewards != null)
                {
                    DungeonRunLifecycle.CancelAutoFlip(session);
                    await _sender.SendLayoutAsync(session);
                    if (!session.Player.IsCurrentDungeonRun(identity)
                        || !run.TryMarkCardsRevealed())
                    {
                        return false;
                    }
                    StartDelayedAutoFlip(session, 4000);
                    return false;
                }

                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendExitAsync(session, state, option);
                return state == 1;
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private async Task AutoFlipFreeCard(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var identity = run?.CaptureIdentity() ?? default;
            if (!session.Player.IsCurrentDungeonRun(identity)
                || !CardRewardRules.TrySelectCardSlot(run, 0, 0))
            {
                return;
            }
            if (!session.Player.IsCurrentDungeonRun(identity))
                return;
            await _sender.SendCardInfoAsync(session, run);
            if (session.Player.IsCurrentDungeonRun(identity))
                await DeliverCardRewards(session, run, CardRewardSide.Free);
        }

        private async Task DeliverCardRewards(
            EnhancedClientSession session,
            DungeonRun run,
            CardRewardSide side)
        {
            if (!TryGetOwnedInventory(session, out var lease))
            {
                FileLogger.Log(
                    $"[CardRewardCoordinator] online inventory missing " +
                    $"cid={session.Player.CharacterId} side={side}");
                return;
            }
            var identity = run.CaptureIdentity();
            var result = _application.Deliver(
                session.Player.CharacterId,
                lease,
                run,
                side);
            if (result.Committed
                && session.Player.IsCurrentDungeonRun(identity))
            {
                await _sender.SendItemUpdatesAsync(session, result.Changes);
            }
        }

        private void ScheduleAutoFlipTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            int delayMs,
            RunTimerTicket ticket,
            string source)
        {
            if (!IsAutoFlipTimerCurrent(session, run, identity, ticket))
                return;
            var handle = ClockService.Instance.ScheduleOneShotAfterAsync(
                BuildAutoFlipTimerName(session, run, ticket),
                TimeSpan.FromMilliseconds(delayMs),
                async _ => await OnAutoFlipTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket,
                    source));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnLayoutTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            int autoFlipDelayMs)
        {
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket)
                    || run.Phase != DungeonRunPhase.ResultShown)
                {
                    return;
                }
                FileLogger.Log("[CardRewardCoordinator] Auto-layout timer fired");
                await _sender.SendLayoutAsync(session);
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket)
                    || !run.TryMarkCardsRevealed())
                {
                    return;
                }
                ScheduleAutoFlipTimer(
                    session,
                    run,
                    identity,
                    autoFlipDelayMs,
                    ticket,
                    "Auto-flow");
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private async Task OnAutoFlipTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            string source)
        {
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket)
                    || run.Phase != DungeonRunPhase.CardsRevealed)
                {
                    return;
                }
                FileLogger.Log(
                    $"[CardRewardCoordinator] {source} auto-flip timer fired");
                await AutoFlipFreeCard(session, run);
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private static bool IsAutoFlipTimerCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
            => session?.Player != null
               && session.Player.IsCurrentDungeonRun(identity)
               && run.Matches(identity)
               && run.Timers.IsCurrent(ticket);

        private static bool TryGetOwnedInventory(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private static string BuildAutoFlipTimerName(
            EnhancedClientSession session,
            DungeonRun run,
            RunTimerTicket ticket)
            => "dungeon-card:" + session.SessionId.ToString("N")
               + ":" + run.RunId
               + ":" + ticket.Generation;
    }
}
