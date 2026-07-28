using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Progression;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSharedServices
    {
        internal const string ProtocolLogName = "GameProtocol";

        internal string ConnectionString { get; }
        internal SqliteSelectCharacterDataSource SelectCharacterDataSource { get; }
        internal IRentalTimeProvider RentalTimeProvider { get; }
        internal InventoryRefreshSender InventoryRefresh { get; }
        internal IMercenaryRestrictionService MercenaryRestrictions { get; }

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal Game.DeathTower.DeathTowerHandler DeathTower { get; }
        internal Game.Quests.QuestDropService QuestDrops { get; }
        internal AntonNormalConquestNotifier AntonNormal { get; }

        // 副本域用到的仓储集中在这里构造一次, 各方法不再就地 new。
        internal SqliteCharacterRepository CharacterRepository { get; }
        internal SqliteSubtype1Repository Subtype1Repository { get; }
        internal SqliteCharacterStateRepository CharacterStateRepository { get; }
        internal SqliteCharacterProgressRepository ProgressRepository { get; }
        internal SqliteSubtype0FieldsRepository Subtype0FieldsRepository { get; }
        internal HonorLevelSyncService HonorLevel { get; }
        internal AccountExperienceProgressService AccountExperience { get; }
        internal GrowthCapsuleSyncService GrowthCapsuleSync { get; }
        internal CharacterExperienceService CharacterExperience { get; }
        internal Game.Dungeon.TowerOfDespairProgressService TowerOfDespairProgress { get; }
        internal Game.Dungeon.TowerOfDespairRewardGrantService TowerOfDespairRewards { get; }

        // 组队副本联机用: 检测队伍 + 定位队员会话(可空; 未接线时副本 fan-out 优雅跳过=单人不回归)。
        internal Game.Party.PartyManager PartyManager { get; }
        internal Game.Session.ISessionDirectory Sessions { get; }

        internal DungeonSharedServices(
            Game.ReviveCoin.ReviveCoinService reviveCoin,
            SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider,
            string connectionString,
            InventoryRefreshSender inventoryRefresh,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            Game.Quests.QuestDropService questDropService = null,
            AccountExperienceProgressService accountExperience = null,
            IMercenaryRestrictionService mercenaryRestrictions = null)
        {
            ReviveCoin = reviveCoin ?? throw new ArgumentNullException(nameof(reviveCoin));
            CharacterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            PartyManager = partyManager;
            Sessions = sessions;
            SelectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            InventoryRefresh = inventoryRefresh;
            MercenaryRestrictions = mercenaryRestrictions;
            RentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            QuestDrops = questDropService ?? new Game.Quests.QuestDropService(
                inventoryRefresh,
                ConnectionString);
            Subtype1Repository = new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            CharacterStateRepository = new SqliteCharacterStateRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            AntonNormal = new AntonNormalConquestNotifier(
                CharacterStateRepository);
            ProgressRepository = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            Subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            HonorLevel = new HonorLevelSyncService(CharacterRepository);
            AccountExperience = accountExperience
                ?? new AccountExperienceProgressService(CharacterRepository);
            GrowthCapsuleSync = new GrowthCapsuleSyncService(CharacterRepository);
            CharacterExperience = new CharacterExperienceService(AccountExperience);
            DeathTower = new Game.DeathTower.DeathTowerHandler(
                ConnectionString,
                sendExpGrantNotification: SendDeathTowerExpGrantNotificationAsync,
                accountExperience: AccountExperience,
                sendInDungeonLevelUpFollowups: SendInDungeonLevelUpFollowups,
                inventoryRefresh: inventoryRefresh);
            TowerOfDespairProgress = new Game.Dungeon.TowerOfDespairProgressService(
                new Game.Dungeon.TowerOfDespairProgressRepository(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath));
            TowerOfDespairRewards =
                new Game.Dungeon.TowerOfDespairRewardGrantService();
            CardRewards = new Game.Dungeon.CardRewardService();
            Drops = new Game.Dungeon.DropService();
            EntryCost = new Game.Dungeon.DungeonEntryCostService();
        }

        internal HonorLevelSummary ResolveHonorLevelForExp(
            EnhancedClientSession session,
            HonorLevelSummary summary = null)
        {
            var tail = session?.Player?.Subtype0Tail;
            if (summary == null && tail != null)
            {
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, tail.ProgressA),
                    HonorExp = tail.ProgressB,
                };
            }

            summary = summary ?? HonorLevel.LoadSummary(session?.Account?.AccountId ?? 0);
            if (session?.Player != null)
            {
                tail = tail ?? new UserInfoMinimumTailSnapshot();
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, summary);
                session.Player.Subtype0Tail = tail;
            }

            return summary;
        }

        internal GrowthCapsuleSummary ResolveGrowthCapsuleForExp(
            EnhancedClientSession session,
            GrowthCapsuleSummary summary = null)
        {
            if ((session?.Player?.Level ?? 0) < Game.Dungeon.ExpTableProvider.MaxLevel)
                return summary ?? GrowthCapsuleDataProvider.Calculate(0);

            return summary ?? AccountExperience.LoadGrowthCapsule(session?.Account?.AccountId ?? 0);
        }

        internal Game.Dungeon.CardRewardService CardRewards { get; }
        internal Game.Dungeon.DropService Drops { get; }
        internal Game.Dungeon.DungeonEntryCostService EntryCost { get; }

        internal (SkillInfoSnapshot Skills, SkillPointState Points) LoadSyncedSkillState(
            int characterId,
            byte currentLevel,
            bool persist = false)
        {
            var record = CharacterRepository.GetById(characterId);

            if (record == null)
                return (ProgressRepository.LoadSkills(characterId), null);

            CharacterStatComputer.DecodeGrowType(record.GrowType, out var firstGrow, out var secondGrow);
            return SkillStateService.LoadAndSync(
                ProgressRepository,
                characterId,
                record.Job,
                currentLevel > 0 ? currentLevel : record.Level,
                record.BonusSp,
                record.BonusTp,
                persist: persist,
                growType: firstGrow,
                secondGrowType: secondGrow);
        }

        // 经验入口共用：返回 0x0025 所需的两页 SP 和共享 TP 绝对状态。
        internal bool TryGetSkillPointProtocolState(
            EnhancedClientSession session,
            bool persist,
            string logTag,
            out SkillPointProtocolState skillPoints)
        {
            skillPoints = default;
            try
            {
                var synced = LoadSyncedSkillState(
                    session.Player.CharacterId,
                    session.Player.Level,
                    persist);
                if (synced.Points != null)
                {
                    skillPoints = SkillStateService.GetProtocolState(
                        synced.Skills,
                        synced.Points);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] {logTag} ERROR: skill-point protocol state refresh failed: {ex.Message}");
                return false;
            }

            FileLogger.Log($"[DungeonHandler] {logTag} ERROR: no verified skill-point protocol state is available");
            return false;
        }

        // 经验入口共用: Grant 之后的 0x0025 通知块(荣誉/胶囊快照解析 + SP 状态 + 组包发送)。
        // 升级后的后续包(任务列表/subtype1)时序因入口而异, 由调用方另行发送。
        internal async Task SendExpGrantNotificationAsync(
            EnhancedClientSession session,
            ExperienceGrantResult grant,
            string logTag,
            uint growthContractBonusExp = 0,
            bool reloadMissingAccountProgress = false)
        {
            if (grant == null
                || (grant.NormalExpGain == 0 && grant.HonorExpGain == 0 && !grant.LeveledUp))
                return;

            var honor = grant.Honor;
            var capsule = grant.GrowthCapsule;
            if (reloadMissingAccountProgress)
            {
                var accountId = session?.Account?.AccountId ?? 0;
                honor = honor ?? HonorLevel.LoadSummary(accountId);
                capsule = capsule ?? AccountExperience.LoadGrowthCapsule(accountId);
            }

            honor = ResolveHonorLevelForExp(session, honor);
            capsule = ResolveGrowthCapsuleForExp(session, capsule);
            if (!TryGetSkillPointProtocolState(session, persist: grant.LeveledUp, logTag, out var skillPoints))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                ExpNotificationBuilder.Build(
                    session.Player.Level, session.Player.Exp, skillPoints, honor,
                    growthContractBonusExp: growthContractBonusExp,
                    growthCapsuleExp: GrowthCapsuleDataProvider.GetDisplayProgress(
                        session.Player.Level, capsule))));
        }

        private Task SendDeathTowerExpGrantNotificationAsync(
            EnhancedClientSession session,
            Game.DeathTower.DeathTowerSettlementResult settlement)
        {
            return SendExpGrantNotificationAsync(
                session,
                settlement?.ExperienceGrant,
                "DEATH_TOWER_SETTLEMENT",
                reloadMissingAccountProgress: true);
        }

        // 副本内升级的后续通知: 刷新可接任务列表 + 补属性(subtype1)。
        // 绝不发角色状态包(subtype0) -- 它会打乱客户端的副本内角色状态,
        // 实测导致清房后无法进下一个门。
        internal async Task SendInDungeonLevelUpFollowups(EnhancedClientSession session)
        {
            await SendQuestListRefresh(session);
            await SendUserInfoBroadcast(session);
        }

        internal async Task SendQuestListRefresh(EnhancedClientSession session)
        {
            try
            {
                var rec = CharacterRepository.GetById(session.Player.CharacterId);
                if (rec == null) return;

                var clearedFlags = new Game.Quests.QuestRepository(
                    SqliteDatabaseBootstrap.BuildConnectionString(ServerPaths.DatabasePath))
                    .LoadClearedFlags(session.Player.CharacterId);
                var allowedCreatureKinds = InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                    ? PetCreatureEvolutionRuntimeService.LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                    : new HashSet<int>();

                var body = Builders.QuestListBodyBuilder.BuildBody(
                    session.Player.Level, rec.Job, rec.GrowType, clearedFlags, allowedCreatureKinds);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0015, body));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] SendQuestListRefresh ERROR: {ex.Message}");
            }
        }

        internal async Task SendUserInfoBroadcast(
            EnhancedClientSession session)
        {
            try
            {
                int cid = session.Player.CharacterId;
                var record = CharacterRepository.GetById(cid);
                var addition = Subtype1Repository.HasData(cid) ? Subtype1Repository.Load(cid) : null;
                if (record != null && addition != null)
                {
                    var accountId = session.Account?.AccountId ?? record.AccountId;
                    var accountCharacters = CharacterRepository.ListByAccount(accountId);
                    var honor = HonorLevel.LoadSummary(accountId, accountCharacters);
                    var skillSnap = LoadSyncedSkillState(cid, session.Player.Level).Skills;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002,
                        UserInfoBroadcastService.BuildSubtype1Body(
                            record, addition, accountCharacters, honor, skillSnap)));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        internal async Task SendUserInfoSubtype0Broadcast(EnhancedClientSession session)
        {
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                CharacterRepository,
                Subtype0FieldsRepository,
                HonorLevel,
                "SendUserInfoSubtype0Broadcast");
        }

    }

}
