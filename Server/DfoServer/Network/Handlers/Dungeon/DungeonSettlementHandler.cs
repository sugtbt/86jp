using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Progression;
using DfoServer.Game.SecretShop;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSettlementHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonEntryHandler _entry;

        private const int SetPlayResultRankPointOffset = 10;
        // 成长之契约经验加成从 PVF premiumlist_new.etc 读取(PremiumEffectProvider)。
        private const float BlackDiamondBonusRate = 0.10f;
        private static readonly int[] BlackDiamondPremiumTypes = { 1, 17 };

        internal DungeonSettlementHandler(
            DungeonSharedServices svc,
            DungeonEntryHandler entry)
        {
            _svc = svc;
            _entry = entry;
        }

        // Settlement result.
        // df_game_r CParty::CheckPlayResult -> CParty::SetPlayResult
        // Sends 3 NOTI packets (34, 37, 35) to show the settlement screen.
        // Card layout is deferred: a 2 s server timer sends it automatically
        // so the player sees the settlement summary first, then the cards appear.
        // After the card layout, a 4 s timer auto-flips the free card
        // (the client shows a 3 s countdown; 4 s on the server gives it room to finish).
        // If the player presses a key before the layout timer fires, the layout
        // is sent immediately and a fresh 3 s auto-flip timer starts.
        internal async Task HandleSetPlayResult(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (!run.RewardPolicy.AllowsSettlement) return;
            if (run.RunState != DungeonRunState.Cleared) return;
            if (run.SettlementState != DungeonSettlementState.NotStarted
                && run.SettlementState != DungeonSettlementState.Preparing)
            {
                return;
            }

            var settlementEffectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                "settlement-preparation",
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(
                    settlementEffectId,
                    out var settlementReservation))
            {
                return;
            }
            if (!run.TryBeginSettlementPreparation()
                && !run.CanResumeSettlementPreparation())
            {
                run.Effects.TryFail(settlementReservation);
                return;
            }

            var identity = run.CaptureIdentity();
            try
            {
                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "settlement-mechanism-preparing",
                        async () => await DungeonMechanismCoordinator
                            .OnResultPreparingAsync(session, run, body)))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                var settlement = run.SettlementRuntime;
                if (settlement == null)
                {
                    settlement = BuildSettlementRuntime(session, run, body);
                    run.SettlementRuntime = settlement;
                }

                if (settlement.IsTowerOfDespair
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "tower-of-despair-progress",
                        () =>
                        {
                            if (!_svc.TowerOfDespairProgress.TryRecordClear(
                                    session.Player.CharacterId,
                                    run.DungeonId,
                                    out var nextFloor,
                                    out var progressError))
                            {
                                throw progressError
                                    ?? new InvalidOperationException(
                                        "Tower of Despair progress was rejected.");
                            }

                            FileLogger.Log(
                                $"[{DungeonSharedServices.ProtocolLogName}] " +
                                $"TOWER_OF_DESPAIR_PROGRESS: " +
                                $"cid={session.Player.CharacterId} " +
                                $"dungeon={run.DungeonId} nextFloor={nextFloor}");
                            return Task.CompletedTask;
                        }))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "settlement-experience-grant",
                        () =>
                        {
                            settlement.ExperienceGrant =
                                GrantSettlementExperienceInTransaction(
                                    session,
                                    run,
                                    settlement);
                            return Task.CompletedTask;
                        })
                    || !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "play-result-notification",
                        async () => await session.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                0x0022,
                                DungeonNotificationBuilder.BuildPlayResult(
                                    session.Player.UserId,
                                    settlement.ClearTimeMilliseconds,
                                    rankIndex: (byte)settlement.RankBonusIndex,
                                    timeBonusPoint: (byte)Math.Max(
                                        0,
                                        Math.Min(255, settlement.TimeBonusPoint)),
                                    clientRankPoint:
                                        settlement.ClientRankPoint))))
                    || !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "experience-notification",
                        async () => await _svc.ProgressNotifications.SendExpGrantNotificationAsync(
                            session,
                            settlement.ExperienceGrant,
                            "SET_PLAY_RESULT",
                            reloadMissingAccountProgress: true))
                    || !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "clear-reward-notification",
                        async () => await session.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                0x0023,
                                DungeonNotificationBuilder
                                    .BuildClearDungeonReward(
                                        settlement.ClearBaseExp,
                                        scoreBonusExp: ToInt32Saturated(
                                            settlement.ScoreBonusExp),
                                        clearBonusExp: 0,
                                        blackDiamondExp: ToInt32Saturated(
                                            settlement.BlackDiamondBonusExp),
                                        growthContractExp: ToInt32Saturated(
                                            settlement.GrowthContractBonusExp),
                                        monsterGrowthContractExp:
                                            ToInt32Saturated(
                                                settlement
                                                    .MonsterGrowthContractBonusExp),
                                        adventureGroupExp: ToInt32Saturated(
                                            settlement.AdventureGroupBonusExp),
                                        monsterExp: settlement.MonsterTotalExp,
                                        bossExp: ToInt32Saturated(
                                            settlement.BossTotalExp),
                                        championExp: ToInt32Saturated(
                                            settlement.ChampionTotalExp),
                                        superChampionExp: 0,
                                        freeCardGold:
                                            settlement.FreeGold.GoldAmount,
                                         freeCardItemId:
                                             settlement.FreeItem.ItemId,
                                         freeCardItemCount:
                                             settlement.FreeItem.StackCount,
                                         paidCardCost:
                                             settlement.PaidCardCost)))))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (settlement.IsTowerOfDespair)
                {
                    if (!await ExecuteSettlementEffectAsync(
                            session,
                            run,
                            identity,
                            "tower-of-despair-reward-persistence",
                            () =>
                            {
                                GrantAndPersistTowerOfDespairRewards(
                                    session,
                                    settlement);
                                return Task.CompletedTask;
                            })
                        || !await ExecuteSettlementEffectAsync(
                            session,
                            run,
                            identity,
                            "tower-of-despair-inventory-notification",
                            async () => await SendTowerOfDespairInventoryUpdates(
                                session,
                                settlement.TowerGrantedRewards)))
                    {
                        run.Effects.TryFail(settlementReservation);
                        return;
                    }

                    if (TryBuildTowerOfDespairClearRewardWithTime(
                            run.DungeonId,
                            (uint)Math.Max(
                                0,
                                settlement.ClearTimeMilliseconds),
                            settlement.TowerGrantedRewards
                                .Select(reward => reward.Reward)
                                .ToArray(),
                            out var towerClearReward)
                        && !await ExecuteSettlementEffectAsync(
                            session,
                            run,
                            identity,
                            "tower-of-despair-clear-notification",
                            async () => await session.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    0x015C,
                                    towerClearReward))))
                    {
                        run.Effects.TryFail(settlementReservation);
                        return;
                    }
                }

                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "suitable-dungeon-lucky-star",
                        async () =>
                        {
                            if (!await GrantSuitableDungeonLuckyStar(
                                    session,
                                    run,
                                    settlement.PreviousLevel))
                            {
                                throw new InvalidOperationException(
                                    "Suitable-dungeon reward persistence failed.");
                            }
                        }))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                _svc.PersistentMechanisms.ConfigureLinkedChallenge(run);
                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "linked-dungeon-notification",
                        async () => await SendLinkedDungeonInfoAsync(
                            session,
                            run)))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP: " +
                    $"dungeon={run.DungeonId} diff={run.Difficulty} " +
                    $"clientRank={settlement.ClientRankPoint} " +
                    $"rankPoint={settlement.RankPoint} " +
                    $"rankGrade={settlement.RankGrade} " +
                    $"rankBonusIndex={settlement.RankBonusIndex} " +
                    $"base={settlement.ClearBaseExp} " +
                    $"scoreBonus={settlement.ScoreBonusExp} " +
                    $"growthContract={settlement.GrowthContractBonusExp} " +
                    $"blackDiamond={settlement.BlackDiamondBonusExp} " +
                    $"adventureGroup={settlement.AdventureGroupBonusExp} " +
                    $"bonus={settlement.ClearBonusExp} " +
                    $"total={settlement.ClearTotalExp} " +
                    $"monsterTotalExp={settlement.MonsterTotalExp} " +
                    $"monsterGrowthContract=" +
                    $"{settlement.MonsterGrowthContractBonusExp} " +
                    $"bossTotalExp={settlement.BossTotalExp} " +
                    $"championTotalExp={settlement.ChampionTotalExp} " +
                    $"superChampionTotalExp=" +
                    $"{settlement.SuperChampionTotalExp} " +
                    $"namedMonsterTotalExp={settlement.NamedMonsterTotalExp} " +
                    $"charExp={session.Player.Exp}");

                if (settlement.ExperienceGrant?.LeveledUp == true
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "level-up-followup-notification",
                        async () =>
                        {
                            FileLogger.Log(
                                $"[{DungeonSharedServices.ProtocolLogName}] " +
                                $"LEVEL UP from dungeon clear: " +
                                $"cid={session.Player.CharacterId} " +
                                $"{settlement.PreviousLevel}->" +
                                $"{session.Player.Level} " +
                                $"exp={session.Player.Exp}");
                            await _svc.ProgressNotifications.SendInDungeonLevelUpFollowups(session);
                        }))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "dungeon-permission-persistence",
                        () =>
                        {
                            if (!EnsureDungeonPermissionPlan(
                                    session,
                                    run.DungeonId,
                                    run.Difficulty,
                                    settlement))
                            {
                                throw new InvalidOperationException(
                                    "Dungeon permission persistence failed.");
                            }
                            return Task.CompletedTask;
                        })
                    || (settlement.DungeonPermissionChanged
                        && !await ExecuteSettlementEffectAsync(
                            session,
                            run,
                            identity,
                            "dungeon-permission-notification",
                            () => SendDungeonPermissionUpdateAsync(
                                session,
                                settlement.DungeonPermissionEntries)))
                    || !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "persistent-dungeon-mechanisms",
                        async () => await _svc.PersistentMechanisms
                            .ApplyDungeonClearAsync(session, run)))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (settlement.ShouldScheduleCardRewardFlow
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "card-flow-schedule",
                        () =>
                        {
                            run.CardFlipCount = 0;
                            run.FreeCardSlots =
                                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                            run.PaidCardSlots =
                                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                            run.FreeCardRewardDelivered = false;
                            run.PaidCardRewardDelivered = false;
                            _svc.CardRewards.ScheduleAutoFlow(
                                session,
                                layoutDelayMs: 2000,
                                autoFlipDelayMs: 4000);
                            return Task.CompletedTask;
                        }))
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (!session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryMarkResultShown())
                {
                    run.Effects.TryFail(settlementReservation);
                    return;
                }

                if (!settlement.ShouldScheduleCardRewardFlow)
                    run.TryCompleteSettlement();

                run.Effects.TryCommit(settlementReservation);
            }
            catch (Exception ex)
            {
                run.Effects.TryFail(settlementReservation);
                FileLogger.Log(
                    $"[DungeonHandler] SET_PLAY_RESULT effect failed: " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"event={settlementEffectId.SourceEventId:N} error={ex.Message}");
                throw;
            }
        }

        private DungeonSettlementRuntime BuildSettlementRuntime(
            EnhancedClientSession session,
            DungeonRun run,
            byte[] body)
        {
            var isTowerOfDespair = DungeonData.TryGetTowerOfDespairFloor(
                run.DungeonId,
                out var towerOfDespairFloor);
            var shouldScheduleCardRewardFlow =
                ShouldScheduleCardRewardFlow(run.DungeonId);
            var clearRank = CalculateClearRank(body);
            var clearExp = CalculateClearRewardExp(
                session,
                run,
                clearRank.RankBonusIndex);

            var dungeonLevel = 85;
            try
            {
                dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SET_PLAY_RESULT ERROR: " +
                    $"dungeon level fallback dungeon={run.DungeonId} " +
                    $"default={dungeonLevel}, card rewards will use the " +
                    $"fallback level: {ex.Message}");
            }

            var lcg = run.RoomLcg ?? new DnfLcg(run.Seed);
            var instance = run.Instance;
            var selection = instance?.Selection;
            var killStatistics = instance != null
                ? instance.KillStatistics
                : default(DungeonKillStatistics);
            var rewardContext = new ClearRewardGenerationContext(
                dungeonLevel,
                run.Difficulty,
                partyMemberCount: selection?.PartyMemberCount
                    ?? run.EntryPartyMemberCount,
                rankBonusRate: MonsterRewardTable.GetClearRankExpBonusRate(
                    clearRank.RankBonusIndex),
                normalKillCount: killStatistics.NormalKillCount,
                championKillCount: killStatistics.ChampionKillCount,
                bossKillCount: killStatistics.BossKillCount,
                visitedRoomCount: Math.Max(
                    1,
                    instance?.VisitedRoomCount ?? run.RoomStates.Count),
                totalRoomCount: Math.Max(
                    1,
                    selection?.TotalRoomCount ?? run.TotalRoomCount));
            var freeGold = shouldScheduleCardRewardFlow
                ? ClearRewardGenerator.GenerateFreeGoldCard(rewardContext, lcg)
                : default;
            var freeItem = shouldScheduleCardRewardFlow
                ? ClearRewardGenerator.GenerateFreeItemCard(rewardContext, lcg)
                : default;
            var towerRewardCandidates = isTowerOfDespair
                ? BuildTowerOfDespairRewardCandidates(
                    towerOfDespairFloor,
                    () => ClearRewardGenerator.GenerateItemCard(
                        dungeonLevel,
                        run.Difficulty,
                        lcg))
                : Array.Empty<ClearRewardGenerator.CardReward>();
            var paidGold = new ClearRewardGenerator.CardReward
            {
                IsGold = true,
                GoldAmount = 0,
            };
            var paidItem = default(ClearRewardGenerator.CardReward);
            var paidCardCost = 0;
            if (ShouldGeneratePaidCardRewards(run.DungeonId))
            {
                paidCardCost = ClearRewardGenerator.GetPaidCardCost(dungeonLevel);
                paidItem = ClearRewardGenerator.GeneratePaidItemCard(
                    rewardContext,
                    lcg);
            }

            run.PaidCardCost = paidCardCost;
            run.CardRewards = shouldScheduleCardRewardFlow
                ? new List<ClearRewardGenerator.CardReward>
                {
                    freeGold,
                    freeItem,
                    default,
                    default,
                    paidGold,
                    paidItem,
                    default,
                    default,
                }
                : null;

            if (shouldScheduleCardRewardFlow)
            {
                FileLogger.Log(
                    $"[ClearReward] dungeon={run.DungeonId} level={dungeonLevel} " +
                    $"difficulty={run.Difficulty} party={rewardContext.PartyMemberCount} " +
                    $"rooms={rewardContext.VisitedRoomCount}/{rewardContext.TotalRoomCount} " +
                    $"kills={rewardContext.NormalKillCount}/" +
                    $"{rewardContext.ChampionKillCount}/" +
                    $"{rewardContext.BossKillCount} " +
                    $"freeGold={freeGold.GoldAmount} freeItem={freeItem.ItemId} " +
                    $"paidCost={paidCardCost} paidItem={paidItem.ItemId}");
            }

            var monsterTotalExp = run.TotalExp;
            return new DungeonSettlementRuntime
            {
                IsTowerOfDespair = isTowerOfDespair,
                TowerOfDespairFloor = towerOfDespairFloor,
                ShouldScheduleCardRewardFlow = shouldScheduleCardRewardFlow,
                ClientRankPoint = clearRank.ClientRankPoint,
                TimeBonusPoint = clearRank.TimeBonusPoint,
                RankPoint = clearRank.RankPoint,
                RankGrade = clearRank.RankGrade,
                RankBonusIndex = clearRank.RankBonusIndex,
                ClearBaseExp = clearExp.Base,
                ScoreBonusExp = clearExp.ScoreBonus,
                GrowthContractBonusExp = clearExp.GrowthContractBonus,
                BlackDiamondBonusExp = clearExp.BlackDiamondBonus,
                AdventureGroupBonusExp = clearExp.AdventureGroupBonus,
                ClearBonusExp = clearExp.Bonus,
                ClearTotalExp = clearExp.Total,
                PreviousLevel = session.Player.Level,
                PreviousExp = session.Player.Exp,
                DungeonLevel = dungeonLevel,
                PaidCardCost = paidCardCost,
                FreeGold = freeGold,
                FreeItem = freeItem,
                TowerRewardCandidates = towerRewardCandidates,
                MonsterTotalExp = monsterTotalExp,
                BossTotalExp = Math.Min(run.BossTotalExp, monsterTotalExp),
                ChampionTotalExp = Math.Min(
                    run.ChampionTotalExp,
                    monsterTotalExp),
                SuperChampionTotalExp = Math.Min(
                    run.SuperChampionTotalExp,
                    monsterTotalExp),
                NamedMonsterTotalExp = Math.Min(
                    run.NamedMonsterTotalExp,
                    monsterTotalExp),
                MonsterGrowthContractBonusExp =
                    run.MonsterGrowthContractBonusExp,
                ClearTimeMilliseconds = CalculateClearTimeMs(run),
            };
        }

        private ExperienceGrantResult GrantSettlementExperienceInTransaction(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonSettlementRuntime settlement)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                throw new InvalidOperationException(
                    "Settlement experience belongs to a stale dungeon run.");
            }
            if (settlement.ClearTotalExp == 0)
                return null;

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                DungeonEffectScope.Player,
                run.RunId);
            if (!_svc.PersistentEffects.TryApplySettlementExperience(
                    effectId,
                    session.Player.CharacterId,
                    session.Account?.AccountId ?? 0,
                    settlement.PreviousLevel,
                    settlement.PreviousExp,
                    settlement.ClearTotalExp,
                    out var grant,
                    out var error))
            {
                throw new InvalidOperationException(
                    "Settlement experience persistent effect failed: " + error);
            }

            session.Player.Level = grant.NewLevel;
            session.Player.Exp = grant.NewExp;
            return grant;
        }

        internal static async Task<bool> ExecuteSettlementEffectAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            string effectKind,
            Func<Task> execute)
        {
            if (session?.Player == null
                || run == null
                || execute == null
                || !session.Player.IsCurrentDungeonRun(identity))
            {
                return false;
            }

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(effectId, out var reservation))
            {
                return run.Effects.GetState(effectId)
                    == DungeonEffectState.Committed;
            }

            try
            {
                await execute();
                if (!run.Effects.TryCommit(reservation))
                    return false;
                return session.Player.IsCurrentDungeonRun(identity);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        private static bool TryBuildTowerOfDespairClearRewardWithTime(
            int dungeonId,
            uint clearTimeMilliseconds,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards,
            out byte[] body)
        {
            body = null;
            if (!DungeonData.TryGetTowerOfDespairFloor(dungeonId, out var floor))
                return false;

            body = DungeonNotificationBuilder.BuildTowerOfDespairClearReward(
                clearTimeMilliseconds,
                floor,
                rewards);
            return true;
        }

        private static IReadOnlyList<ClearRewardGenerator.CardReward>
            BuildTowerOfDespairRewardCandidates(
                int floor,
                Func<ClearRewardGenerator.CardReward> randomRewardFactory)
        {
            if (randomRewardFactory == null)
                throw new ArgumentNullException(nameof(randomRewardFactory));

            var isPlayerMirrorFloor =
                floor >= 10
                && floor <= 90
                && floor % 10 == 0;
            var randomRewardCount = isPlayerMirrorFloor ? 9 : 5;
            var rewards = new List<ClearRewardGenerator.CardReward>(10);
            for (var i = 0; i < randomRewardCount; i++)
            {
                var reward = randomRewardFactory();
                if (!reward.IsGold
                    && reward.ItemId > 0
                    && reward.StackCount > 0)
                {
                    rewards.Add(reward);
                }
            }

            if (isPlayerMirrorFloor)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 1252,
                    StackCount = 1,
                });
            }
            else if (floor == 100)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 3314,
                    StackCount = 1,
                });
            }

            return rewards;
        }

        private async Task SendTowerOfDespairInventoryUpdates(
            EnhancedClientSession session,
            IReadOnlyList<TowerOfDespairGrantedReward> granted)
        {
            if (_svc.InventoryRefresh == null
                || granted == null
                || granted.Count == 0)
                return;

            try
            {
                foreach (var group in granted.GroupBy(
                             reward => reward.ListType))
                {
                    await _svc.InventoryRefresh.SendUpdateItemList(
                        session,
                        group.Key,
                        group.Select(reward => reward.Slot));
                }
            }
            catch (Exception ex)
            {
                // The rewards are already applied. A refresh failure must not
                // suppress TOD_CLEAR_REWARD or abort the remaining settlement flow.
                FileLogger.Log(
                    $"[TowerOfDespair] inventory refresh failed after reward grant: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        private void GrantAndPersistTowerOfDespairRewards(
            EnhancedClientSession session,
            DungeonSettlementRuntime settlement)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(
                    characterId,
                    out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                throw new InvalidOperationException(
                    $"Tower reward inventory lease is unavailable for " +
                    $"character {characterId}.");
            }

            if (!settlement.TowerRewardsGranted)
            {
                lock (lease.SyncRoot)
                {
                    settlement.TowerGrantedRewards =
                        _svc.TowerOfDespairRewards.Grant(
                            lease.Inventory,
                            settlement.TowerRewardCandidates);
                    settlement.TowerRewardsGranted = true;
                }
            }

            if (settlement.TowerGrantedRewards.Count > 0
                && !InventoryPersistenceService.SaveDirty(lease))
            {
                throw new InvalidOperationException(
                    $"Tower reward persistence failed for character " +
                    $"{characterId}.");
            }
        }

        private static bool ShouldGeneratePaidCardRewards(int dungeonId)
        {
            return ShouldScheduleCardRewardFlow(dungeonId);
        }

        private static bool ShouldScheduleCardRewardFlow(int dungeonId)
        {
            return !DungeonData.TryGetTowerOfDespairFloor(dungeonId, out _);
        }

        private static ClearRankParts CalculateClearRank(byte[] body)
        {
            var clientRankPoint = ExtractClientRankPoint(body);
            var timeBonusPoint = 0;
            var rankPoint = Math.Min(255, clientRankPoint + timeBonusPoint);
            var rankGrade = MonsterRewardTable.GetClearRankGrade(rankPoint);
            var rankBonusIndex = MonsterRewardTable.GetClearRankBonusIndex(rankPoint);

            return new ClearRankParts(
                (byte)clientRankPoint,
                timeBonusPoint,
                rankPoint,
                (byte)rankGrade,
                rankBonusIndex);
        }

        private static int ExtractClientRankPoint(byte[] body)
        {
            if (body == null || body.Length == 0)
                return 0;

            if (body.Length > SetPlayResultRankPointOffset)
                return body[SetPlayResultRankPointOffset];

            return body[0];
        }

        private static int ReadInt32(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 3 >= body.Length)
                return 0;

            return BitConverter.ToInt32(body, offset);
        }

        private static int CalculateClearTimeMs(DungeonRun run)
        {
            if (run == null || run.StartedUtc == DateTime.MinValue)
                return 0;

            var elapsed = DateTime.UtcNow - run.StartedUtc;
            if (elapsed <= TimeSpan.Zero)
                return 0;
            if (elapsed.TotalMilliseconds >= int.MaxValue)
                return int.MaxValue;
            return (int)Math.Round(elapsed.TotalMilliseconds);
        }

        private ClearExpParts CalculateClearRewardExp(
            EnhancedClientSession session,
            DungeonRun run,
            int rankBonusIndex)
        {
            int dungeonLevel;
            try { dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId); }
            catch (Exception ex) { dungeonLevel = session.Player.Level; FileLogger.Log($"[DungeonHandler] CLEAR_EXP ERROR: dungeon level fallback to player level {dungeonLevel}: {ex.Message}"); }

            var baseExp = ExpTableProvider.GetExpRewardBase(dungeonLevel);
            if (baseExp <= 0)
                return default;

            float expWeight;
            try { expWeight = DungeonData.GetExperienceWeight(run.DungeonId); }
            catch { expWeight = 1.0f; }

            var scaledBase = baseExp * expWeight * MonsterRewardTable.GetDifficultyExpRate(run.Difficulty);
            var clearBaseExp = ToUInt32Floor(scaledBase);
            if (clearBaseExp == 0)
                return default;

            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            // Account 缺失时传 0(查不到契约, 无加成), 不能回退到账号 1 借用其契约效果。
            var accountId = session.Account?.AccountId ?? 0;
            var scoreBonusRate = MonsterRewardTable.GetClearRankExpBonusRate(rankBonusIndex);
            var scoreBonus = ToUInt32Floor(clearBaseExp * scoreBonusRate);
            var premiumEffects = Game.Premium.PremiumEffectProvider.GetCombinedEffects(connStr, accountId);
            var growthContractBonus = premiumEffects.ComputeBonusExp(clearBaseExp);
            var blackDiamondBonus = PremiumService.HasActivePremium(connStr, accountId, BlackDiamondPremiumTypes)
                ? ToUInt32Floor(clearBaseExp * BlackDiamondBonusRate)
                : 0;
            var adventureGroupBonus = CalculateAdventureGroupClearExpBonus(session, accountId, clearBaseExp);

            return new ClearExpParts(clearBaseExp, scoreBonus, growthContractBonus, blackDiamondBonus, adventureGroupBonus);
        }

        private uint CalculateAdventureGroupClearExpBonus(EnhancedClientSession session, int accountId, uint clearBaseExp)
        {
            if (session == null || clearBaseExp == 0)
                return 0;

            try
            {
                var characters = _svc.CharacterRepository.ListByAccount(accountId);
                var summary = AdventureGroupDataProvider.Calculate(characters);
                if (summary.ExpBonusPercent == 0 || IsHighestLevelCharacter(session, characters))
                    return 0;

                return ToUInt32Floor(clearBaseExp * (summary.ExpBonusPercent / 100.0f));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP adventure group bonus skipped: {ex.Message}");
                return 0;
            }
        }

        private static bool IsHighestLevelCharacter(EnhancedClientSession session, IReadOnlyList<Game.Characters.CharacterRecord> characters)
        {
            if (session?.Player == null || characters == null || characters.Count == 0)
                return true;

            var highestLevel = 0;
            foreach (var character in characters)
            {
                if (character == null || character.Deleted)
                    continue;
                if (character.Level > highestLevel)
                    highestLevel = character.Level;
            }

            return session.Player.Level >= highestLevel;
        }

        private static uint ToUInt32Floor(float value)
        {
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static int ToInt32Saturated(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private readonly struct ClearRankParts
        {
            internal ClearRankParts(byte clientRankPoint, int timeBonusPoint, int rankPoint, byte rankGrade, int rankBonusIndex)
            {
                ClientRankPoint = clientRankPoint;
                TimeBonusPoint = timeBonusPoint;
                RankPoint = rankPoint;
                RankGrade = rankGrade;
                RankBonusIndex = rankBonusIndex;
            }

            internal byte ClientRankPoint { get; }
            internal int TimeBonusPoint { get; }
            internal int RankPoint { get; }
            internal byte RankGrade { get; }
            internal int RankBonusIndex { get; }
        }

        private readonly struct ClearExpParts
        {
            internal ClearExpParts(uint baseExp, uint scoreBonus, uint growthContractBonus, uint blackDiamondBonus, uint adventureGroupBonus)
            {
                Base = baseExp;
                ScoreBonus = scoreBonus;
                GrowthContractBonus = growthContractBonus;
                BlackDiamondBonus = blackDiamondBonus;
                AdventureGroupBonus = adventureGroupBonus;
            }

            internal uint Base { get; }
            internal uint ScoreBonus { get; }
            internal uint GrowthContractBonus { get; }
            internal uint BlackDiamondBonus { get; }
            internal uint AdventureGroupBonus { get; }
            internal uint Bonus => CharacterExperienceService.AddSaturating(CharacterExperienceService.AddSaturating(CharacterExperienceService.AddSaturating(ScoreBonus, GrowthContractBonus), BlackDiamondBonus), AdventureGroupBonus);
            internal uint Total => CharacterExperienceService.AddSaturating(Base, Bonus);
        }

        internal async Task HandleSelectCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await _svc.CardRewards.HandleSelectCard(session, body);
        }

        internal async Task HandleEplpCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();
            var linkedNextId = run?.LinkedDungeonNextId ?? 0;
            var difficulty = run?.Difficulty ?? 0;
            var shouldReturnToTown = await _svc.CardRewards.HandleEplpCommand(session, body);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;
            if (IsLinkedChallengeCommand(body) && linkedNextId > 0)
            {
                FileLogger.Log(
                    $"[DungeonHandler] LINKED_DUNGEON continue selected: " +
                    $"current={run.DungeonId} next={linkedNextId} " +
                    $"diff={difficulty}");
                await _entry.EnterLinkedDungeonAsync(
                    session,
                    header,
                    linkedNextId,
                    difficulty);
                return;
            }
            if (shouldReturnToTown)
                await ReturnToVillage(session, runIdentity);
        }

        internal async Task HandleCardStartRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await _svc.CardRewards.HandleCardStartRequest(session);
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), advances phase to Cleared
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC: settlement mystery merchant NPC ID
        internal async Task SubmitClearIntentAsync(
            EnhancedClientSession session,
            DungeonClearIntent intent)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || intent == null)
                return;
            if (!run.Matches(intent.Source.RunIdentity)
                || (intent.Source.RoomInstanceId.HasValue
                    && run.CurrentRoomInstanceId != intent.Source.RoomInstanceId.Value))
                return;
            if (!run.RewardPolicy.AllowsClearCommit)
                return;

            DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    intent.Source,
                    DungeonEncounterDirectiveKind.Succeed,
                    cause: intent.Reason));
            run.Instance.Diagnostics.Record(
                DungeonDiagnosticRecordKind.ClearIntent,
                intent.Source,
                "dungeon-clear-intent",
                "accepted",
                intent.Reason);

            var clearFact = run.Instance.GetOrCreateClearedFact(
                intent,
                out var factCreated);
            if (!run.TryBeginClearCommit(clearFact)
                && !run.CanResumeClearCommit(clearFact))
                return;

            var clearProjectionId = new DungeonEffectId(
                clearFact.SourceEventId,
                "dungeon-clear-projection",
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(clearProjectionId, out var clearReservation))
                return;

            var identity = run.CaptureIdentity();
            try
            {
                if (clearFact.BossCode != 0)
                    run.BossCode = clearFact.BossCode;

                var offer = run.SecretShopOffer ?? CreateSecretShopOffer(run);
                run.SecretShopOffer = offer;

                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        "enable-clear-notification",
                        async () => await session.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                0x001F,
                                DungeonNotificationBuilder.BuildEnableClearDungeon()))))
                {
                    run.Effects.TryFail(clearReservation);
                    return;
                }

                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        "secret-shop-notification",
                        async () =>
                        {
                            // This protocol has no ACK. A packet failure retries the
                            // group; a connection loss may therefore replay a prefix.
                            foreach (var packet in SecretShopClearPacketBuilder.Build(offer))
                                await session.SendPacketAsync(packet);
                        }))
                {
                    run.Effects.TryFail(clearReservation);
                    return;
                }

                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        "quest-clear-drop",
                        async () => await _svc.QuestDrops.CheckDungeonClearReward(session)))
                {
                    run.Effects.TryFail(clearReservation);
                    return;
                }

                var currentMapId = ResolveCurrentMapId(run);
                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        $"quest-clear-map:{run.DungeonId}:{currentMapId}",
                        async () => await DungeonClearMapQuestSync.SyncAsync(
                            session,
                            run.DungeonId,
                            currentMapId,
                            "dungeon_clear",
                            clearFact.Source)))
                {
                    run.Effects.TryFail(clearReservation);
                    return;
                }

                if (ShouldSyncQuestConnectedStartMapOnDungeonClear(run, currentMapId))
                {
                    FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync deferred quest-connected start map: dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
                    if (!await ExecuteClearEffectAsync(
                            session,
                            run,
                            identity,
                            clearFact,
                            $"quest-clear-map:0:{run.MazeStartMapId}",
                            async () => await DungeonClearMapQuestSync.SyncAsync(
                                session,
                                0,
                                run.MazeStartMapId,
                                "dungeon_clear_deferred_start_map",
                                clearFact.Source)))
                    {
                        run.Effects.TryFail(clearReservation);
                        return;
                    }
                }

                if (!session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryCompleteClearCommit(clearFact))
                {
                    run.Effects.TryFail(clearReservation);
                    return;
                }

                run.Effects.TryCommit(clearReservation);
                run.Instance.Diagnostics.Record(
                    DungeonDiagnosticRecordKind.ClearCommit,
                    clearFact.Source,
                    "dungeon-clear-commit",
                    "committed",
                    clearFact.Reason);
                var itemSummary = string.Join(",", offer.Items.Select(x => $"{x.ItemId}:price={x.Price}:count={x.Count}"));
                FileLogger.Log(
                    $"[DungeonHandler] ClearDungeon: {clearFact.Reason} " +
                    $"event={clearFact.SourceEventId:N} factCreated={factCreated} " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"secretShopNpc={offer.NpcId} items=[{itemSummary}]");
            }
            catch (Exception ex)
            {
                run.Effects.TryFail(clearReservation);
                run.Instance.Diagnostics.Record(
                    DungeonDiagnosticRecordKind.ClearCommit,
                    clearFact.Source,
                    "dungeon-clear-commit",
                    "failed",
                    ex.Message);
                FileLogger.Log(
                    $"[DungeonHandler] ClearDungeon effect failed: " +
                    $"event={clearFact.SourceEventId:N} instance={run.PartyDungeonInstanceId} " +
                    $"run={run.RunId} error={ex.Message}");
                throw;
            }
        }

        private static async Task<bool> ExecuteClearEffectAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            DungeonClearedFact clearFact,
            string effectKind,
            Func<Task> execute)
        {
            var effectId = new DungeonEffectId(
                clearFact.SourceEventId,
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(effectId, out var reservation))
                return run.Effects.GetState(effectId) == DungeonEffectState.Committed;

            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity))
                {
                    run.Effects.TryFail(reservation);
                    return false;
                }

                await execute();
                if (!session.Player.IsCurrentDungeonRun(identity))
                {
                    run.Effects.TryFail(reservation);
                    return false;
                }

                return run.Effects.TryCommit(reservation);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        internal async Task TryClearQuestNpcDungeonAsync(
            EnhancedClientSession session,
            Game.Quests.QuestSetTriggerResult result,
            DungeonEventEnvelope sourceEvent)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsQuestCompletionSourceCurrent(run, sourceEvent)
                || result == null)
            {
                return;
            }

            PvfLib.DungeonFile dungeonFile;
            try
            {
                dungeonFile = DungeonData.GetDungeonFile(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] quest NPC clear config failed: " +
                    $"dungeon={run.DungeonId} quest={result.QuestId} " +
                    $"error={ex.Message}");
                return;
            }

            var questNpcMatched = ShouldClearQuestNpcDungeon(
                run,
                dungeonFile.QuestNpcDungeon,
                GameWorld.QuestData.IsMeetNpcQuest(result.QuestId),
                result);
            var currentMapId = ResolveCurrentMapId(run);
            var connectedQuestId = ResolveSelectedMazeQuestConnection(
                dungeonFile,
                run.MazeIndex);
            var questConnectedClearMapMatched =
                ShouldClearQuestConnectedClearMapDungeon(
                    run,
                    connectedQuestId,
                    currentMapId,
                    GameWorld.QuestData.IsClearMapQuest(result.QuestId),
                    result);
            if (!questNpcMatched && !questConnectedClearMapMatched)
            {
                return;
            }

            FileLogger.Log(
                $"[DungeonHandler] quest completion clear matched: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"maze={run.MazeIndex} map={currentMapId} " +
                $"quest={result.QuestId} source=" +
                $"{(questNpcMatched ? "quest-npc" : "quest-connected-clear-map")} " +
                $"trigger={result.PreviousTriggerValue}->{result.TriggerValue}");
            await SubmitClearIntentAsync(
                session,
                new DungeonClearIntent(
                    sourceEvent,
                    $"quest completion quest={result.QuestId}",
                    bossCode: 0));
        }

        internal static bool IsQuestCompletionSourceCurrent(
            DungeonRun run,
            DungeonEventEnvelope sourceEvent)
        {
            if (run == null
                || sourceEvent == null
                || run.Phase != DungeonRunPhase.InProgress
                || !run.Matches(sourceEvent.RunIdentity))
            {
                return false;
            }

            return !sourceEvent.RoomInstanceId.HasValue
                || run.CurrentRoomInstanceId == sourceEvent.RoomInstanceId.Value;
        }

        internal static bool ShouldClearQuestNpcDungeon(
            DungeonRun run,
            int questNpcDungeon,
            bool isMeetNpcQuest,
            Game.Quests.QuestSetTriggerResult result)
        {
            if (run == null
                || run.Phase != DungeonRunPhase.InProgress
                || questNpcDungeon != 1
                || !isMeetNpcQuest
                || result == null
                || !result.Success
                || result.PreviousTriggerValue == 0
                || result.TriggerValue != 0
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2)
            {
                return false;
            }

            return run.RoomKey.X == run.BossMapPos[0]
                && run.RoomKey.Y == run.BossMapPos[1];
        }

        internal static bool ShouldClearQuestConnectedClearMapDungeon(
            DungeonRun run,
            int connectedQuestId,
            int currentMapId,
            bool isClearMapQuest,
            Game.Quests.QuestSetTriggerResult result)
        {
            if (run == null
                || run.Phase != DungeonRunPhase.InProgress
                || !run.MazeQuestConnected
                || connectedQuestId <= 0
                || currentMapId <= 0
                || !isClearMapQuest
                || result == null
                || !result.Success
                || result.QuestId != connectedQuestId
                || result.PreviousTriggerValue == 0
                || result.TriggerValue != 0
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2
                || run.RoomKey.X != run.BossMapPos[0]
                || run.RoomKey.Y != run.BossMapPos[1])
            {
                return false;
            }

            return GameWorld.QuestData.MatchesClearMapTarget(
                result.QuestId,
                run.DungeonId,
                currentMapId);
        }

        private static int ResolveSelectedMazeQuestConnection(
            PvfLib.DungeonFile dungeonFile,
            int mazeIndex)
        {
            if (dungeonFile?.Mazes == null
                || mazeIndex < 0
                || mazeIndex >= dungeonFile.Mazes.Count)
            {
                return -1;
            }

            var connection = dungeonFile.Mazes[mazeIndex].QuestConnection;
            if (connection == null || connection.Length < 2)
                connection = dungeonFile.QuestConnection;
            if (connection == null
                || connection.Length < 2
                || connection[0] != 0)
            {
                return -1;
            }

            return connection[1];
        }

        private static SecretShopOffer CreateSecretShopOffer(DungeonRun run)
        {
            try
            {
                var dungeonBasisLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
                return SecretShopOfferFactory.Create(
                    SecretShopCatalogProvider.Current,
                    run.DungeonId,
                    dungeonBasisLevel,
                    partySize: 1,
                    ServerRandom.Next);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SecretShop] offer creation failed closed: dungeon={run.DungeonId} error={ex.Message}");
                return new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>());
            }
        }

        private static int ResolveCurrentMapId(DungeonRun run)
        {
            if (run == null)
                return 0;

            RoomState state;
            if (run.RoomStates != null
                && run.RoomStates.TryGetValue(run.RoomKey, out state)
                && state != null
                && state.Maze.Index > 0)
                return state.Maze.Index;

            return 0;
        }

        private static bool ShouldSyncQuestConnectedStartMapOnDungeonClear(
            DungeonRun run,
            int currentMapId)
        {
            if (run == null || !run.MazeQuestConnected)
                return false;
            if (run.MazeStartMapId <= 0 || run.MazeStartMapId == currentMapId)
                return false;
            return true;
        }

        internal static bool IsLinkedChallengeCommand(byte[] body)
            => body != null
                && body.Length >= 2
                && body[0] == 1
                && body[1] == 3;

        private static async Task SendLinkedDungeonInfoAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || run == null
                || run.LinkedDungeonNextId <= 0)
            {
                return;
            }

            var difficulty = Math.Min(4, (int)run.Difficulty);
            var body = DungeonNotificationBuilder.BuildLinkedDungeonInfo(
                run.LinkedDungeonNextId,
                difficulty);
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.LINKED_DUNGEON_INFO,
                    body));
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;
            LinkedDungeonEntryAuthorizationStore.Grant(
                session.Player,
                run.DungeonId,
                run.LinkedDungeonNextId,
                (byte)difficulty);
            FileLogger.Log(
                $"[DungeonHandler] LINKED_DUNGEON_INFO sent: " +
                $"current={run.DungeonId} " +
                $"next={run.LinkedDungeonNextId} " +
                $"difficulty={difficulty} " +
                $"rate={run.LinkedDungeonNextRate} " +
                $"condition={run.LinkedDungeonNextCondition} " +
                $"body={BitConverter.ToString(body)}");
        }

        // Synchronous return-to-town: mirrors DungeonTutorialHandler.ReturnToVillage packet sequence.
        // Key points: UserState=0x00 (not 0x01), sync await (not fire-and-forget), includes NOTI 0x00CA.
        private async Task ReturnToVillage(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity)
        {
            if (!await DungeonRunLifecycle.EndRunAsync(
                    session,
                    DungeonRunEndReason.ReturnToTown,
                    runIdentity,
                    _svc.InstanceRegistry))
            {
                return;
            }
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            session.Player.UserState = 0x00;

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await _svc.ProgressNotifications.SendUserInfoSubtype0Broadcast(session);
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ReturnToVillage: town state + subtype0 sent");
        }

        private bool EnsureDungeonPermissionPlan(
            EnhancedClientSession session,
            int dungeonId,
            int difficulty,
            DungeonSettlementRuntime settlement)
        {
            if (settlement == null)
                return false;
            try
            {
                if (!DungeonPermissionScopePolicy.IsAccountDifficulty(dungeonId))
                {
                    settlement.DungeonPermissionPlanReady = true;
                    settlement.DungeonPermissionChanged = false;
                    FileLogger.Log(
                        $"[DungeonHandler] DungeonPermission account update skipped " +
                        $"dungeon={dungeonId} " +
                        $"scope={DungeonPermissionScopePolicy.Resolve(dungeonId)}");
                    return true;
                }

                var accountId = session?.Account?.AccountId ?? 0;
                if (accountId <= 0)
                    throw new InvalidOperationException(
                        "Dungeon difficulty permission requires an account identity.");

                if (settlement.DungeonPermissionPlanReady)
                {
                    if (!settlement.DungeonPermissionChanged)
                        return true;
                    if (settlement.DungeonPermissionAccountId != accountId)
                        return false;
                    var replaySnapshot = _svc.DungeonDifficultyPermissions
                        .ApplyBatch(
                            accountId,
                            settlement.DungeonPermissionEntries,
                            out _);
                    return DungeonPermissionProjector.IsApplied(
                        replaySnapshot,
                        settlement.DungeonPermissionEntries);
                }

                if (dungeonId <= 0)
                {
                    settlement.DungeonPermissionPlanReady = true;
                    return true;
                }
                int maxClearState = GameWorld.Dungeon.GetMaxDifficultyCount(dungeonId) - 1;
                if (maxClearState <= 0)
                {
                    settlement.DungeonPermissionPlanReady = true;
                    return true;
                }
                byte newClearState = (byte)(difficulty + 1);
                if (newClearState < 1) newClearState = 1;
                if (newClearState > maxClearState) newClearState = (byte)maxClearState;

                var plan = _svc.DungeonDifficultyPermissions
                    .BuildProgressionPlan(
                    accountId,
                    dungeonId,
                    newClearState);
                settlement.DungeonPermissionEntries = plan.Entries;
                settlement.DungeonPermissionChanged = plan.RequiresPersistence;
                settlement.DungeonPermissionAccountId = accountId;
                settlement.DungeonPermissionPlanReady = true;
                if (!settlement.DungeonPermissionChanged)
                    return true;

                var snapshot = _svc.DungeonDifficultyPermissions
                    .ApplyBatch(
                        accountId,
                        settlement.DungeonPermissionEntries,
                        out _);
                return DungeonPermissionProjector.IsApplied(
                    snapshot,
                    settlement.DungeonPermissionEntries);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] DungeonPermission ERROR: {ex.Message}");
                return false;
            }
        }

        private Task SendDungeonPermissionUpdateAsync(
            EnhancedClientSession session,
            IReadOnlyList<DungeonPermissionEntrySnapshot> entries)
        {
            if (entries == null || entries.Count == 0)
                return Task.CompletedTask;

            FileLogger.Log(
                $"[DungeonHandler] DungeonPermission: " +
                $"entries={string.Join(",", entries.Select(
                    entry => $"{entry.DungeonId}:{entry.ClearState}"))}");
            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0005,
                    DungeonPermissionBodyBuilder.BuildEntries(entries)));
        }

        private async Task<bool> GrantSuitableDungeonLuckyStar(
            EnhancedClientSession session,
            DungeonRun run,
            int clearLevel)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || !GameWorld.Dungeon.IsSuitableLevelDungeon(
                    run.DungeonId,
                    clearLevel))
            {
                return true;
            }

            var characterId = session.Player.CharacterId;
            var accountId = session.Account?.AccountId ?? 0;
            if (characterId <= 0 || accountId <= 0)
                return true;

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.SuitableDungeonLuckyStar,
                DungeonEffectScope.Player,
                run.RunId);
            if (!_svc.PersistentEffects.TryApplySuitableDungeonLuckyStar(
                    effectId,
                    characterId,
                    accountId,
                    run.DungeonId,
                    clearLevel,
                    out var result,
                    out var error))
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR ERROR: " +
                    $"char={characterId} dungeon={run.DungeonId} " +
                    $"level={clearLevel} {error}");
                return false;
            }

            if (!result.Granted)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR skipped: " +
                    $"cap reached char={characterId} dungeon={run.DungeonId} " +
                    $"level={clearLevel}");
                return true;
            }

            FileLogger.Log(
                $"[DungeonHandler] SUITABLE_LUCKY_STAR grant: " +
                $"char={characterId} dungeon={run.DungeonId} " +
                $"level={clearLevel} stars={result.NewTotal}");
            try
            {
                if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                    return false;
                await LuckyStarClientNotifier.NotifyRewardAsync(
                    session,
                    _svc.SelectCharacterDataSource,
                    characterId,
                    1,
                    result.NewTotal,
                    _svc.RentalTimeProvider);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR sync ERROR: " +
                    $"char={characterId} dungeon={run.DungeonId} " +
                    $"stars={result.NewTotal} {ex.Message}");
            }
            return true;
        }
    }
}
