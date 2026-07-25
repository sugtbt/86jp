using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Game.Dungeon
{
    internal sealed class CardRewardService
    {
        private enum CardRewardSide
        {
            Free,
            Paid,
        }

        internal CardRewardService()
        {
        }

        internal void ScheduleAutoFlow(EnhancedClientSession session, int layoutDelayMs, int autoFlipDelayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            // 旧服翻牌阶段使用队伍 timer key 防止过期回调误推进。
            // 当前项目保留同一安全边界: timer 只负责到点请求推进, 真正执行前仍要重查当前局和版本号。
            var version = NextAutoFlipVersion(run);
            var timerName = BuildAutoFlipTimerName(session);
            var handle = ClockService.Instance.ScheduleOneShotAfterAsync(
                timerName,
                TimeSpan.FromMilliseconds(layoutDelayMs),
                async _ =>
                {
                    if (!IsAutoFlipTimerCurrent(session, run, version)) return;
                    if (run.Phase != DungeonRunPhase.ResultShown) return;

                    FileLogger.Log("[CardReward] Auto-layout ClockService timer fired");
                    await SendCardLayout(session);
                    if (!IsAutoFlipTimerCurrent(session, run, version)) return;
                    run.Phase = DungeonRunPhase.CardsRevealed;

                    ScheduleAutoFlipTimer(session, run, autoFlipDelayMs, version, "Auto-flow");
                });
            StoreAutoFlipHandle(run, version, handle);
        }

        internal void StartDelayedAutoFlip(EnhancedClientSession session, int delayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            // 玩家已经看到翻牌布局后, 只需要保留 4s 自动翻免费卡这一段短 timer。
            var version = NextAutoFlipVersion(run);
            ScheduleAutoFlipTimer(session, run, delayMs, version, "Standalone");
        }

        internal async Task HandleSelectCard(EnhancedClientSession session, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null || body == null || body.Length < 2) return;
            byte cardType = body[0];
            byte cardIndex = body[1];

            if (run.Phase == DungeonRunPhase.ResultShown)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await SendCardLayout(session);
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, 4000);
                return;
            }

            if (cardType > 1 || cardIndex > 3) return;
            if (cardType == 0) DungeonRunLifecycle.CancelAutoFlip(session);

            if (cardType == 1
                && cardIndex == 0
                && !CanPayPaidCard(session, run))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));
                return;
            }

            if (!TrySelectCardSlot(run, cardType, cardIndex))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));

            if (cardIndex == 0)
                await DeliverCardRewards(session, run, cardType == 0 ? CardRewardSide.Free : CardRewardSide.Paid);
        }

        internal async Task HandleCardStartRequest(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.Phase != DungeonRunPhase.ResultShown) return;

            DungeonRunLifecycle.CancelAutoFlip(session);
            await SendCardLayout(session);
            run.Phase = DungeonRunPhase.CardsRevealed;
            StartDelayedAutoFlip(session, 4000);
        }

        // Returns true if caller should proceed to ReturnToVillage.
        internal async Task<bool> HandleEplpCommand(EnhancedClientSession session, byte[] body)
        {
            if (body == null || body.Length < 2) return false;
            byte state = body[0];
            byte option = body[1];
            var run = session.Player.CurrentRun;

            if (run != null && run.Phase == DungeonRunPhase.ResultShown)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await SendCardLayout(session);
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, 4000);
                return false;
            }

            DungeonRunLifecycle.CancelAutoFlip(session);

            // EPLP/再次挑战只负责结束当前结算界面, 不能替玩家自动翻付费卡或补发奖励。
            // 返城/重进发生在 timer 到期前时, 正常语义就是不获得翻牌奖励。

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0048,
                new byte[] { 0x01, state, option }));

            return state == 1;
        }

        private async Task AutoFlipFreeCard(EnhancedClientSession session, DungeonRun run)
        {
            if (!TrySelectCardSlot(run, cardType: 0, cardIndex: 0))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));
            await DeliverCardRewards(session, run, CardRewardSide.Free);
        }

        private static bool TrySelectCardSlot(DungeonRun run, byte cardType, byte cardIndex)
        {
            lock (run.SyncRoot)
            {
                if (run.CardRewards == null)
                    return false;

                var slots = cardType == 0 ? run.FreeCardSlots : run.PaidCardSlots;
                if (slots[cardIndex] != 0xFF)
                    return false;

                slots[cardIndex] = 0x00;
                run.CardFlipCount++;
                return true;
            }
        }

        private void ScheduleAutoFlipTimer(
            EnhancedClientSession session,
            DungeonRun run,
            int delayMs,
            int version,
            string source)
        {
            if (!IsAutoFlipTimerCurrent(session, run, version))
                return;

            var timerName = BuildAutoFlipTimerName(session);
            var handle = ClockService.Instance.ScheduleOneShotAfterAsync(
                timerName,
                TimeSpan.FromMilliseconds(delayMs),
                async _ =>
                {
                    if (!IsAutoFlipTimerCurrent(session, run, version)) return;
                    FileLogger.Log($"[CardReward] {source} auto-flip ClockService timer fired");
                    await AutoFlipFreeCard(session, run);
                });
            StoreAutoFlipHandle(run, version, handle);
        }

        private static int NextAutoFlipVersion(DungeonRun run)
        {
            var version = Interlocked.Increment(ref run.AutoFlipTimerVersion);
            if (version == 0)
                version = Interlocked.Increment(ref run.AutoFlipTimerVersion);
            return version;
        }

        private static bool IsAutoFlipTimerCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            int version)
            => session?.Player != null
               && ReferenceEquals(session.Player.CurrentRun, run)
               && run.AutoFlipTimerVersion == version;

        private static void StoreAutoFlipHandle(
            DungeonRun run,
            int version,
            ClockService.ClockTimerHandle handle)
        {
            // 注册和取消可能跨线程竞争: 若版本已经变化, 说明本局流程被玩家操作/返城/换局打断。
            if (run.AutoFlipTimerVersion != version)
            {
                handle.Cancel();
                return;
            }

            var previous = Interlocked.Exchange(ref run.AutoFlipTimerHandle, handle);
            if (previous != null && !ReferenceEquals(previous, handle))
                previous.Cancel();

            if (run.AutoFlipTimerVersion != version)
            {
                Interlocked.CompareExchange(ref run.AutoFlipTimerHandle, null, handle);
                handle.Cancel();
            }
        }

        private async Task DeliverCardRewards(
            EnhancedClientSession session,
            DungeonRun run,
            CardRewardSide side)
        {
            var cid = session.Player.CharacterId;
            var changes = new List<InventorySlotMutation>();
            if (!TryGetOwnedInventory(session, out var lease))
            {
                FileLogger.Log($"[CardReward] online inventory missing cid={cid} side={side}");
                return;
            }

            var cards = ReserveCardRewards(run, side);
            if (cards == null)
                return;

            var carryLimit = InventoryGoldCarryLimitLoader.Load(cid);
            lock (lease.SyncRoot)
            {
                if (side == CardRewardSide.Free)
                {
                    CollectGoldReward(lease.Inventory, carryLimit, cards, 0, changes);
                    CollectItemReward(lease.Inventory, cards, 1, changes);
                }
                else
                {
                    if (SpendPaidCardGold(lease.Inventory, cards, 4, changes))
                        CollectItemReward(lease.Inventory, cards, 5, changes);
                }
            }

            await SendItemUpdates(session, changes);
            ClearCardRewardsIfFinished(run);
            FileLogger.Log($"[CardReward] {side} rewards delivered: {changes.Count} entries");
        }

        private static List<ClearRewardGenerator.CardReward> ReserveCardRewards(
            DungeonRun run,
            CardRewardSide side)
        {
            lock (run.SyncRoot)
            {
                var cards = run.CardRewards;
                if (cards == null)
                    return null;

                if (side == CardRewardSide.Free)
                {
                    if (run.FreeCardRewardDelivered)
                        return null;

                    run.FreeCardRewardDelivered = true;
                    return cards;
                }

                if (!HasPaidCardReward(cards) || run.PaidCardRewardDelivered)
                    return null;

                run.PaidCardRewardDelivered = true;
                return cards;
            }
        }

        private static bool HasPaidCardReward(List<ClearRewardGenerator.CardReward> cards)
        {
            if (cards == null)
                return false;

            return (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0) ||
                   (cards.Count > 5 && !cards[5].IsGold && cards[5].ItemId > 0);
        }

        private static void ClearCardRewardsIfFinished(DungeonRun run)
        {
            lock (run.SyncRoot)
            {
                var cards = run.CardRewards;
                if (cards == null)
                    return;

                var paidDone = !HasPaidCardReward(cards) || run.PaidCardRewardDelivered;
                if (run.FreeCardRewardDelivered && paidDone)
                    run.CardRewards = null;
            }
        }

        private static bool CanPayPaidCard(EnhancedClientSession session, DungeonRun run)
        {
            var cost = GetPaidCardGoldCost(run);
            if (cost <= 0)
                return true;
            if (!TryGetOwnedInventory(session, out var lease))
                return false;

            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(0) >= cost;
        }

        private static void CollectGoldReward(
            InventoryService inventory,
            int carryLimit,
            List<ClearRewardGenerator.CardReward> cards,
            int index,
            List<InventorySlotMutation> changes)
        {
            if (cards.Count <= index || !cards[index].IsGold || cards[index].GoldAmount <= 0) return;
            try
            {
                if (!inventory.TryGrantGold(cards[index].GoldAmount, carryLimit, out _, out _))
                    return;

                AddChangedSlot(changes, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart);
            }
            catch (Exception ex) { FileLogger.Log($"[CardReward] CollectGoldReward ERROR: {ex.Message}"); }
        }

        private static bool SpendPaidCardGold(
            InventoryService inventory,
            List<ClearRewardGenerator.CardReward> cards,
            int index,
            List<InventorySlotMutation> changes)
        {
            var cost = GetGoldAmount(cards, index);
            if (cost <= 0)
                return true;

            try
            {
                if (!inventory.TryConsumeMainItem(0, cost, out var consumeResult)
                    || !consumeResult.Success)
                    return false;

                AddChangedSlots(changes, consumeResult.Changes);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CardReward] SpendPaidCardGold ERROR: {ex.Message}");
                return false;
            }
        }

        private static void CollectItemReward(
            InventoryService inventory,
            List<ClearRewardGenerator.CardReward> cards,
            int index,
            List<InventorySlotMutation> changes)
        {
            if (cards.Count <= index || cards[index].IsGold || cards[index].ItemId <= 0) return;
            var card = cards[index];
            try
            {
                if (!InventoryRewardGrantService.TryCreateAndInsert(
                        inventory,
                        card.ItemId,
                        ItemCreateReason.DungeonDrop,
                        card.StackCount,
                        out var grant)
                    || !grant.Success)
                    return;

                AddChangedSlots(changes, grant.Changes);
            }
            catch (Exception ex) { FileLogger.Log($"[CardReward] CollectItemReward ERROR: {ex.Message}"); }
        }

        private static async Task SendItemUpdates(EnhancedClientSession session, List<InventorySlotMutation> changes)
        {
            if (changes.Count == 0) return;
            foreach (var group in changes.GroupBy(change => change.ListType))
            {
                var slots = group.Select(change => change.SlotIndex).ToList();
                await InventoryRefreshSender.SendOnlineUpdateItemList(session, group.Key, slots);
            }
        }

        private static bool TryGetOwnedInventory(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            var cid = session?.Player?.CharacterId ?? 0;
            return cid > 0
                && InventoryContext.TryGetLease(cid, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private static int GetPaidCardGoldCost(DungeonRun run)
        {
            if (run == null)
                return 0;

            lock (run.SyncRoot)
                return GetGoldAmount(run.CardRewards, 4);
        }

        private static int GetGoldAmount(List<ClearRewardGenerator.CardReward> cards, int index)
        {
            return cards != null
                && cards.Count > index
                && cards[index].IsGold
                ? Math.Max(0, cards[index].GoldAmount)
                : 0;
        }

        private static void AddChangedSlots(
            List<InventorySlotMutation> changes,
            InventoryMutationSet mutation)
        {
            if (mutation == null)
                return;

            foreach (var slot in mutation.Slots)
                AddChangedSlot(changes, slot.ListType, slot.SlotIndex);
        }

        private static void AddChangedSlot(
            List<InventorySlotMutation> changes,
            InventoryListType listType,
            short slotIndex)
        {
            for (var index = 0; index < changes.Count; index++)
            {
                var existing = changes[index];
                if (existing.ListType == listType && existing.SlotIndex == slotIndex)
                    return;
            }

            changes.Add(new InventorySlotMutation(listType, slotIndex));
        }

        private static async Task SendCardLayout(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
        }

        private static byte[] BuildCardInfoAck(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            for (int i = 0; i < 8; i++)
            {
                if (i >= 4) { w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0xFF); continue; }
                bool freeSelected = run.FreeCardSlots[i] != 0xFF;
                bool paidSelected = run.PaidCardSlots[i] != 0xFF;
                if (i != 0) { w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0x00); w.WriteByte(0x00); continue; }
                w.WriteByte(freeSelected ? (byte)0x00 : (byte)0xFF);
                w.WriteByte(paidSelected ? (byte)0x00 : (byte)0xFF);
                if (paidSelected)
                {
                    var cards = run.CardRewards;
                    int paidGoldAmt = (cards != null && cards.Count > 4 && cards[4].IsGold) ? cards[4].GoldAmount : 0;
                    int paidItemId = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].ItemId : 0;
                    int paidItemCnt = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].StackCount : 0;
                    w.WriteByte(2);
                    w.WriteUInt32(0);
                    w.WriteInt32(paidGoldAmt);
                    w.WriteUInt32((uint)paidItemId);
                    w.WriteInt32(paidItemCnt);
                }
                else { w.WriteByte(0x00); }
                w.WriteByte(0x00);
            }
            return w.ToArray();
        }

        private static byte[] BuildCardLayoutAck()
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(0x0001);
            for (int i = 1; i < 8; i++) w.WriteUInt16(0xFFFF);
            return w.ToArray();
        }

        private static string BuildAutoFlipTimerName(EnhancedClientSession session)
            => "dungeon-card:" + session.SessionId.ToString("N") + ":auto";
    }
}
