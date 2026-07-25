using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Progression;
using DfoServer.Game.SecretShop;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSettlementHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonEntryHandler _entry;

        private const int SetPlayResultRankPointOffset = 10;
        private const int SetPlayResultSeizeMoneyHitCountOffset = 6;
        private const int SeizeMoneyGoldIngotItemId = 10089565;
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
            if (run.Phase != DungeonRunPhase.Cleared) return;
            await TrySendSeizeMoneyGoldIngotDropsAsync(session, body);
            run.Phase = DungeonRunPhase.ResultShown;

            var isTowerOfDespair = DungeonData.TryGetTowerOfDespairFloor(
                run.DungeonId,
                out var towerOfDespairFloor);
            var shouldScheduleCardRewardFlow =
                ShouldScheduleCardRewardFlow(run.DungeonId);
            if (isTowerOfDespair)
            {
                if (!_svc.TowerOfDespairProgress.TryRecordClear(
                        session.Player.CharacterId,
                        run.DungeonId,
                        out var nextFloor,
                        out var progressError))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"TOWER_OF_DESPAIR_PROGRESS rejected settlement before rewards: " +
                        $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                        $"error={progressError?.Message}");
                    run.Phase = DungeonRunPhase.Cleared;
                    return;
                }

                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"TOWER_OF_DESPAIR_PROGRESS: cid={session.Player.CharacterId} " +
                    $"dungeon={run.DungeonId} nextFloor={nextFloor}");
            }

            var clearRank = CalculateClearRank(body);
            var clearExp = CalculateClearRewardExp(session, clearRank.RankBonusIndex);
            var prevLevel = session.Player.Level;
            var grant = clearExp.Total > 0
                ? _svc.CharacterExperience.Grant(
                    session.Player,
                    session.Account?.AccountId ?? 0,
                    clearExp.Total,
                    ExperiencePersistMode.OnAnyChange,
                    "clear")
                : null;
            var leveledUp = grant != null && grant.LeveledUp;

            // Pre-generate card rewards (df_game_r: clear_reward generated before NOTI 35)
            int dungeonLevel = 85;
            try { dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId); } catch (Exception ex) { FileLogger.Log($"[DungeonHandler] SET_PLAY_RESULT ERROR: dungeon level fallback dungeon={run.DungeonId} default={dungeonLevel}, card rewards will use the fallback level: {ex.Message}"); }
            var lcg = run.RoomLcg ?? new DnfLcg(run.Seed);
            var freeGold = shouldScheduleCardRewardFlow
                ? ClearRewardGenerator.GenerateGoldCard(
                    dungeonLevel, run.Difficulty, lcg)
                : default;
            var freeItem = shouldScheduleCardRewardFlow
                ? ClearRewardGenerator.GenerateItemCard(
                    dungeonLevel, run.Difficulty, lcg)
                : default;
            var towerRewardCandidates = isTowerOfDespair
                ? BuildTowerOfDespairRewardCandidates(
                    towerOfDespairFloor,
                    () => ClearRewardGenerator.GenerateItemCard(
                        dungeonLevel,
                        run.Difficulty,
                        lcg))
                : Array.Empty<ClearRewardGenerator.CardReward>();
            var paidGold = default(ClearRewardGenerator.CardReward);
            var paidItem = default(ClearRewardGenerator.CardReward);
            if (ShouldGeneratePaidCardRewards(run.DungeonId))
            {
                paidGold = ClearRewardGenerator.GenerateGoldCard(
                    dungeonLevel, run.Difficulty, lcg);
                paidItem = ClearRewardGenerator.GenerateEquipmentCard(
                    dungeonLevel, run.Difficulty, lcg);
            }
            run.CardRewards = shouldScheduleCardRewardFlow
                ? new List<ClearRewardGenerator.CardReward>
                {
                    freeGold, freeItem, default, default,  // free: [0]gold [1]item [2-3]empty(solo)
                    paidGold, paidItem, default, default    // paid: [4]gold [5]item [6-7]empty(solo)
                }
                : null;

            var monsterTotalExp = run.TotalExp;
            var bossTotalExp = Math.Min(run.BossTotalExp, monsterTotalExp);
            var championTotalExp = Math.Min(run.ChampionTotalExp, monsterTotalExp);
            var superChampionTotalExp = Math.Min(run.SuperChampionTotalExp, monsterTotalExp);
            var namedMonsterTotalExp = Math.Min(run.NamedMonsterTotalExp, monsterTotalExp);
            var monsterGrowthContractBonus = run.MonsterGrowthContractBonusExp;

            // Settlement 3 packets: NOTI 34, NOTI 37, NOTI 35
            var clearTimeMs = CalculateClearTimeMs(run);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0022,
                DungeonNotificationBuilder.BuildPlayResult(
                    session.Player.UserId,
                    clearTimeMs,
                    rankIndex: (byte)clearRank.RankBonusIndex,
                    timeBonusPoint: (byte)Math.Max(0, Math.Min(255, clearRank.TimeBonusPoint)),
                    clientRankPoint: clearRank.ClientRankPoint)));
            await _svc.SendExpGrantNotificationAsync(session, grant, "SET_PLAY_RESULT");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0023,
                DungeonNotificationBuilder.BuildClearDungeonReward(
                    clearExp.Base, scoreBonusExp: ToInt32Saturated(clearExp.ScoreBonus), clearBonusExp: 0,
                    blackDiamondExp: ToInt32Saturated(clearExp.BlackDiamondBonus),
                    growthContractExp: ToInt32Saturated(clearExp.GrowthContractBonus),
                    monsterGrowthContractExp: ToInt32Saturated(monsterGrowthContractBonus),
                    adventureGroupExp: ToInt32Saturated(clearExp.AdventureGroupBonus),
                    monsterExp: monsterTotalExp, bossExp: ToInt32Saturated(bossTotalExp),
                    championExp: ToInt32Saturated(championTotalExp),
                    superChampionExp: 0,
                    freeCardGold: freeGold.GoldAmount,
                    freeCardItemId: freeItem.ItemId, freeCardItemCount: freeItem.StackCount)));

            var clearTimeMilliseconds = (uint)Math.Max(0, clearTimeMs);
            var towerRewards = isTowerOfDespair
                ? GrantTowerOfDespairRewards(
                    session,
                    towerRewardCandidates)
                : Array.Empty<TowerOfDespairGrantedReward>();
            if (towerRewards.Count > 0)
            {
                await SendTowerOfDespairInventoryUpdates(
                    session,
                    towerRewards);
            }
            if (TryBuildTowerOfDespairClearRewardWithTime(
                    run.DungeonId,
                    clearTimeMilliseconds,
                    towerRewards.Select(reward => reward.Reward).ToArray(),
                    out var towerClearReward))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x015C, towerClearReward));
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] TOD_CLEAR_REWARD: " +
                    $"dungeon={run.DungeonId} clearTimeMs={clearTimeMilliseconds} " +
                    $"generated={towerRewardCandidates.Count} granted={towerRewards.Count}");
            }

            // 符合判断使用结算前等级，奖励通知放在结算三包之后。
            await GrantSuitableDungeonLuckyStar(session, prevLevel);
            _svc.AntonNormal.ConfigureLinkedChallenge(run);
            await SendLinkedDungeonInfoAsync(session, run);

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP: dungeon={run.DungeonId} diff={run.Difficulty} clientRank={clearRank.ClientRankPoint} rankPoint={clearRank.RankPoint} rankGrade={clearRank.RankGrade} rankBonusIndex={clearRank.RankBonusIndex} base={clearExp.Base} scoreBonus={clearExp.ScoreBonus} growthContract={clearExp.GrowthContractBonus} blackDiamond={clearExp.BlackDiamondBonus} adventureGroup={clearExp.AdventureGroupBonus} bonus={clearExp.Bonus} total={clearExp.Total} monsterTotalExp={monsterTotalExp} monsterGrowthContract={monsterGrowthContractBonus} bossTotalExp={bossTotalExp} championTotalExp={championTotalExp} superChampionTotalExp={superChampionTotalExp} namedMonsterTotalExp={namedMonsterTotalExp} charExp={session.Player.Exp}");

            if (leveledUp)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] LEVEL UP from dungeon clear: cid={session.Player.CharacterId} {prevLevel}->{session.Player.Level} exp={session.Player.Exp}");
                await _svc.SendInDungeonLevelUpFollowups(session);
            }

            // 翻牌布局延后: 2 秒 timer 发布局, 再 4 秒 timer 自动翻免费卡。
            // Phase 已在方法入口置为 ResultShown; 懒布局分支都以它作为业务校验。
            if (shouldScheduleCardRewardFlow)
            {
                run.CardFlipCount = 0;
                run.FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                run.PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                run.FreeCardRewardDelivered = false;
                run.PaidCardRewardDelivered = false;

                _svc.CardRewards.ScheduleAutoFlow(
                    session,
                    layoutDelayMs: 2000,
                    autoFlipDelayMs: 4000);
            }

            await UpdateDungeonPermission(session, run.DungeonId, run.Difficulty);
            await _svc.AntonNormal.ApplyClearAsync(session, run);
        }

        private static async Task TrySendSeizeMoneyGoldIngotDropsAsync(
            EnhancedClientSession session,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.SeizeMoney)
            {
                return;
            }

            var config = special.Config.SeizeMoney;
            var unitValue = Math.Max(1, config.GaugeSubOnDamage);
            var maxUnits = Math.Max(1, config.GaugeMax / unitValue);
            var hitCount = Math.Max(
                0,
                ReadInt32(body, SetPlayResultSeizeMoneyHitCountOffset));
            var remainingUnits =
                Math.Max(0, maxUnits - Math.Min(maxUnits, hitCount));
            var bossSeq = special.SeizeMoneyBossSeq;
            if (bossSeq == 0
                || !special.TryReserveSeizeMoneyClearReward(
                    remainingUnits,
                    out var count,
                    out var gauge))
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"bossSeq={bossSeq} hitCount={hitCount} " +
                    $"remainingUnits={remainingUnits} gauge={special.SeizeMoneyGauge}");
                return;
            }

            var drops = new List<DropInfo>();
            lock (run.SyncRoot)
            {
                for (var i = 0; i < count; i++)
                {
                    run.SceneSlotCounter++;
                    var drop = new DropInfo
                    {
                        SceneSlot = run.SceneSlotCounter,
                        TemplateId = SeizeMoneyGoldIngotItemId,
                        StackCount = 1,
                    };
                    drops.Add(drop);
                    run.Drops[drop.SceneSlot] = drop;
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0026,
                DungeonNotificationBuilder.BuildMonsterDie(
                    bossSeq,
                    drops,
                    session.Player.UserId)));
            FileLogger.Log(
                $"[SpecialDungeonModule] SEIZE_MONEY drops sent: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"bossSeq={bossSeq} item={SeizeMoneyGoldIngotItemId} " +
                $"count={count} hitCount={hitCount} " +
                $"remainingUnits={remainingUnits} gauge={gauge}/{config.GaugeMax}");
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

        private IReadOnlyList<TowerOfDespairGrantedReward>
            GrantTowerOfDespairRewards(
                EnhancedClientSession session,
                IReadOnlyList<ClearRewardGenerator.CardReward> candidates)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(
                    characterId,
                    out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"TOD_CLEAR_REWARD: online inventory missing " +
                    $"cid={characterId}");
                return Array.Empty<TowerOfDespairGrantedReward>();
            }

            IReadOnlyList<TowerOfDespairGrantedReward> granted;
            lock (lease.SyncRoot)
            {
                granted = _svc.TowerOfDespairRewards.Grant(
                    lease.Inventory,
                    candidates);
            }
            if (granted.Count > 0
                && !InventoryPersistenceService.SaveDirty(lease))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"TOD_CLEAR_REWARD: SaveDirty failed " +
                    $"cid={characterId}");
            }

            return granted;
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

        private ClearExpParts CalculateClearRewardExp(EnhancedClientSession session, int rankBonusIndex)
        {
            var run = session.Player.CurrentRun;
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
            var linkedNextId = run?.LinkedDungeonNextId ?? 0;
            var difficulty = run?.Difficulty ?? 0;
            var shouldReturnToTown = await _svc.CardRewards.HandleEplpCommand(session, body);
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
                await ReturnToVillage(session);
        }

        internal async Task HandleCardStartRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await _svc.CardRewards.HandleCardStartRequest(session);
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), advances phase to Cleared
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC: settlement mystery merchant NPC ID
        internal async Task TryClearDungeon(EnhancedClientSession session, string reason, int bossCode = 0)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            if (run.Phase != DungeonRunPhase.InProgress) return;
            run.Phase = DungeonRunPhase.Cleared;
            if (bossCode != 0) run.BossCode = bossCode;

            var offer = CreateSecretShopOffer(run);
            run.SecretShopOffer = offer;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001F, DungeonNotificationBuilder.BuildEnableClearDungeon()));
            foreach (var packet in SecretShopClearPacketBuilder.Build(offer))
                await session.SendPacketAsync(packet);
            await _svc.QuestDrops.CheckDungeonClearReward(session);
            var currentMapId = ResolveCurrentMapId(session);
            await DungeonClearMapQuestSync.SyncAsync(
                session,
                run.DungeonId,
                currentMapId,
                "dungeon_clear");
            if (ShouldSyncQuestConnectedStartMapOnDungeonClear(session, currentMapId))
            {
                FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync deferred quest-connected start map: dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
                await DungeonClearMapQuestSync.SyncAsync(
                    session,
                    0,
                    run.MazeStartMapId,
                    "dungeon_clear_deferred_start_map");
            }
            var itemSummary = string.Join(",", offer.Items.Select(x => $"{x.ItemId}:price={x.Price}:count={x.Count}"));
            FileLogger.Log($"[DungeonHandler] ClearDungeon: {reason} secretShopNpc={offer.NpcId} items=[{itemSummary}]");
        }

        internal async Task TryClearQuestNpcDungeonAsync(
            EnhancedClientSession session,
            Game.Quests.QuestSetTriggerResult result)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || run.Phase != DungeonRunPhase.InProgress
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

            if (!ShouldClearQuestNpcDungeon(
                    run,
                    dungeonFile.QuestNpcDungeon,
                    GameWorld.QuestData.IsMeetNpcQuest(result.QuestId),
                    result))
            {
                return;
            }

            FileLogger.Log(
                $"[DungeonHandler] quest NPC clear matched: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"quest={result.QuestId} " +
                $"trigger={result.PreviousTriggerValue}->{result.TriggerValue}");
            await TryClearDungeon(
                session,
                $"quest NPC completed quest={result.QuestId}");
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

        private static int ResolveCurrentMapId(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
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

        private static bool ShouldSyncQuestConnectedStartMapOnDungeonClear(EnhancedClientSession session, int currentMapId)
        {
            var run = session?.Player?.CurrentRun;
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
        private async Task ReturnToVillage(EnhancedClientSession session)
        {
            await DungeonRunLifecycle.EndRunToTownAsync(session);
            session.Player.UserState = 0x00;

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));
            await _svc.SendUserInfoSubtype0Broadcast(session);

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ReturnToVillage: town state + subtype0 sent");
        }

        private async Task UpdateDungeonPermission(EnhancedClientSession session, int dungeonId, int difficulty)
        {
            if (dungeonId <= 0) return;
            int characterId = session.Player.CharacterId;
            int maxClearState = GameWorld.Dungeon.GetMaxDifficultyCount(dungeonId) - 1;
            if (maxClearState <= 0) return;
            byte newClearState = (byte)(difficulty + 1);
            if (newClearState < 1) newClearState = 1;
            if (newClearState > maxClearState) newClearState = (byte)maxClearState;

            try
            {
                if (!_svc.CharacterStateRepository.UpsertDungeonPermission(characterId, dungeonId, newClearState))
                    return;

                var w = new GamePacketWriter();
                w.WriteUInt16(1);
                w.WriteUInt16((ushort)dungeonId);
                w.WriteByte(newClearState);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0005, w.ToArray()));
                FileLogger.Log($"[DungeonHandler] DungeonPermission: dungeon={dungeonId} diff={difficulty} -> clearState={newClearState}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] DungeonPermission ERROR: {ex.Message}");
            }
        }

        private async Task GrantSuitableDungeonLuckyStar(EnhancedClientSession session, int clearLevel)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !GameWorld.Dungeon.IsSuitableLevelDungeon(run.DungeonId, clearLevel))
                return;

            var characterId = session.Player.CharacterId;
            var accountId = session.Account?.AccountId ?? 0;
            if (characterId <= 0 || accountId <= 0)
                return;

            ushort luckyStar;
            try
            {
                using (var connection = new SqliteConnection(_svc.ConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                        if (wallet.LuckyStar >= RentalCatalogCodec.MaxLuckyStar)
                        {
                            FileLogger.Log($"[DungeonHandler] SUITABLE_LUCKY_STAR skipped: cap reached char={characterId} dungeon={run.DungeonId} level={clearLevel}");
                            return;
                        }

                        CurrencyService.GrantLuckyStar(connection, transaction, accountId, 1);
                        luckyStar = (ushort)Math.Min(RentalCatalogCodec.MaxLuckyStar, wallet.LuckyStar + 1);
                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SUITABLE_LUCKY_STAR ERROR: char={characterId} dungeon={run.DungeonId} level={clearLevel} {ex.Message}");
                return;
            }

            FileLogger.Log($"[DungeonHandler] SUITABLE_LUCKY_STAR grant: char={characterId} dungeon={run.DungeonId} level={clearLevel} stars={luckyStar}");
            try
            {
                await LuckyStarClientNotifier.NotifyRewardAsync(
                    session,
                    _svc.SelectCharacterDataSource,
                    characterId,
                    1,
                    luckyStar,
                    _svc.RentalTimeProvider);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SUITABLE_LUCKY_STAR sync ERROR: char={characterId} dungeon={run.DungeonId} stars={luckyStar} {ex.Message}");
            }
        }
    }
}
