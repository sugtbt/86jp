using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Lottery;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    public class GameProtocolHandler : BaseProtocolHandler
    {
        private readonly LoginHandler _loginHandler;
        private readonly CharacterSelectHandler _characterSelectHandler;
        private readonly InventoryHandler _inventoryHandler;
        private readonly LotteryItemHandler _lotteryItemHandler;
        private readonly KnightShieldHandler _knightShieldHandler;
        private readonly TownHandler _townHandler;
        private readonly DungeonHandler _dungeonHandler;
        private readonly SecretShopHandler _secretShopHandler;
        private readonly StaminaHandler _staminaHandler;
        private readonly SkillHandler _skillHandler;
        private readonly SettingsHandler _settingsHandler;
        private readonly CeraShopHandler _ceraShopHandler;
        private readonly LuckyStarHandler _luckyStarHandler;
        private readonly RentalHandler _rentalHandler;
        private readonly MailboxHandler _mailboxHandler;
        private readonly CollectionBoxHandler _collectionBoxHandler;
        private readonly ShopCoinEventHandler _shopCoinEventHandler;
        private readonly InventoryRefreshSender _inventoryRefreshSender;
        private readonly PetCreatureHandler _petCreatureHandler;
        private readonly MercenaryHandler _mercenaryHandler;
        private readonly GrowthCapsuleHandler _growthCapsuleHandler;
        private readonly GoldLimitHandler _goldLimitHandler;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly ISessionDirectory _sessionDirectory;
        // 组队与城镇/副本共享同一个 PartyManager 实例: 副本 fan-out 与跟随退出都要看到同一份队伍状态。
        private readonly Game.Party.PartyManager _partyManager;
        private readonly PartyHandler _partyHandler;
        private readonly Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> _cmdDispatch;

        public override string ProtocolName => "GameProtocol";

        public GameProtocolHandler(ISessionDirectory sessionDirectory, Func<byte[], Task> broadcastGamePacket = null)
        {
            var databasePath = ServerPaths.DatabasePath;
            var schemaFilePath = ServerPaths.SchemaFilePath;

            var characterRepository = new SqliteCharacterRepository(databasePath, schemaFilePath);
            var accountRepository = new SqliteAccountRepository(databasePath, schemaFilePath);
            // 租赁全链路共用同一时间源，保持绝对 Unix 到期时间模型。
            var rentalTimeProvider = SystemRentalTimeProvider.Instance;
            var dailyResetService = new Game.DailyReset.DailyResetService(databasePath, schemaFilePath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            var inventoryLifecycle = new InventoryCharacterLifecycleService(
                databasePath,
                schemaFilePath,
                rentalTimeProvider);
            var experienceItemCooldowns = new ExperienceItemCooldownTracker();
            var experienceItemUseService = new ExperienceItemUseService(
                databasePath,
                schemaFilePath,
                rentalTimeProvider,
                experienceItemCooldowns);
            var sqliteSelectCharacterDataSource = new SqliteSelectCharacterDataSource(
                databasePath,
                schemaFilePath,
                characterRepository,
                inventoryLifecycle,
                rentalTimeProvider,
                dailyResetService);

            var userInfoBlobRepository = new Game.CharacterData.SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            var getUserInfoTemplate = userInfoBlobRepository.LoadGetUserInfoTemplate();

            _characterRepository = characterRepository;
            _selectCharacterDataSource = sqliteSelectCharacterDataSource;
            _sessionDirectory = sessionDirectory;
            _loginHandler = new LoginHandler(accountRepository, characterRepository);
            _characterSelectHandler = new CharacterSelectHandler(sqliteSelectCharacterDataSource, characterRepository, getUserInfoTemplate, sessionDirectory);
            _inventoryRefreshSender = new InventoryRefreshSender(sqliteSelectCharacterDataSource, characterRepository);
            var knightShieldRepository = new KnightShieldDeckRepository(databasePath, schemaFilePath);
            var knightShieldService = new KnightShieldService(knightShieldRepository);
            _knightShieldHandler = new KnightShieldHandler(
                knightShieldService,
                characterRepository);
            var experienceItemNotifications = new ExperienceItemNotificationService(
                characterRepository,
                databasePath,
                schemaFilePath);
            _inventoryHandler = new InventoryHandler(
                experienceItemUseService,
                sqliteSelectCharacterDataSource,
                characterRepository,
                _inventoryRefreshSender,
                experienceItemNotifications,
                broadcastGamePacket);
            var lotteryDoubleRewardPolicy = new LotteryDoubleRewardPolicy(
                dailyResetService,
                connectionString);
            var lotteryOpenService = new LotteryItemOpenService(
                connectionString,
                new LotteryItemDefinitionProvider(),
                lotteryDoubleRewardPolicy);
            var lotteryResponses = new LotteryItemResponseSender(
                lotteryDoubleRewardPolicy,
                _inventoryRefreshSender,
                connectionString,
                broadcastGamePacket);
            _lotteryItemHandler = new LotteryItemHandler(
                lotteryOpenService,
                new LotteryOpenPlanner(lotteryDoubleRewardPolicy),
                new LotteryOpenSessionCoordinator(),
                lotteryResponses);
            _petCreatureHandler = new PetCreatureHandler(sqliteSelectCharacterDataSource, _inventoryRefreshSender);
            // 组队与城镇/副本共享同一个 PartyManager 实例: 跟随退出/副本 fan-out 都要看到同一份队伍状态。
            _partyManager = new Game.Party.PartyManager();
            _townHandler = new TownHandler(characterRepository, sqliteSelectCharacterDataSource, _partyManager, sessionDirectory, _inventoryRefreshSender);
            var reviveCoinService = new Game.ReviveCoin.ReviveCoinService(dailyResetService);
            _dungeonHandler = new DungeonHandler(
                reviveCoinService,
                characterRepository,
                sqliteSelectCharacterDataSource,
                rentalTimeProvider,
                connectionString,
                _inventoryRefreshSender,
                _partyManager,
                sessionDirectory);
            _secretShopHandler = new SecretShopHandler(_inventoryRefreshSender);
            _staminaHandler = new StaminaHandler(_inventoryRefreshSender);
            _settingsHandler = new SettingsHandler(sessionDirectory);
            _ceraShopHandler = new CeraShopHandler(sqliteSelectCharacterDataSource, _inventoryRefreshSender);
            _skillHandler = new SkillHandler(characterRepository, _inventoryRefreshSender);
            _luckyStarHandler = new LuckyStarHandler(sqliteSelectCharacterDataSource, rentalTimeProvider, _inventoryRefreshSender);
            _rentalHandler = new RentalHandler(sqliteSelectCharacterDataSource, rentalTimeProvider, _inventoryRefreshSender);
            _mailboxHandler = new MailboxHandler();
            _collectionBoxHandler = new CollectionBoxHandler(_inventoryRefreshSender);
            _shopCoinEventHandler = new ShopCoinEventHandler(reviveCoinService, _inventoryRefreshSender);
            _mercenaryHandler = new MercenaryHandler(characterRepository);
            _partyHandler = new PartyHandler(_partyManager, characterRepository, sessionDirectory);
            PetCreatureRuntimeService.EnsureClockRegistered();
            _growthCapsuleHandler = new GrowthCapsuleHandler(
                _inventoryRefreshSender, characterRepository);
            _goldLimitHandler = new GoldLimitHandler(
                new Game.Currency.CharacterGoldLimitRepository(databasePath, schemaFilePath),
                _inventoryRefreshSender);

            _cmdDispatch = new Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>>();
            RegisterLoginHandlers(_cmdDispatch);
            RegisterCharacterHandlers(_cmdDispatch);
            RegisterInventoryHandlers(_cmdDispatch);
            RegisterPetHandlers(_cmdDispatch);
            RegisterSortItemLockHandlers(_cmdDispatch);
            RegisterEquipmentItemLockHandlers(_cmdDispatch);
            RegisterEquipmentSocketHandlers(_cmdDispatch);
            RegisterEquipmentEmblemHandlers(_cmdDispatch);
            RegisterAvatarSocketHandlers(_cmdDispatch);
            RegisterAvatarEmblemHandlers(_cmdDispatch);
            RegisterDungeonHandlers(_cmdDispatch);
            RegisterSkillHandlers(_cmdDispatch);
            RegisterTownHandlers(_cmdDispatch);
            RegisterSettingsHandlers(_cmdDispatch);
            RegisterQuestHandlers(_cmdDispatch);
            RegisterMailboxHandlers(_cmdDispatch);
            RegisterCollectionBoxHandlers(_cmdDispatch);
            RegisterMercenaryHandlers(_cmdDispatch);
            RegisterPartyHandlers(_cmdDispatch);
            RegisterMiscHandlers(_cmdDispatch);
            _cmdDispatch[0x00CF] = _shopCoinEventHandler.HandleShopCoinEvent;   // 207 SHOP_COIN_EVENT 每日免费复活币
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client connected: {session.SessionId}");
            PetCreatureRuntimeService.RegisterSession(session);
            await _loginHandler.Handle_ClientFirstConnected(session);
        }

        public override async Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client disconnected: {session.SessionId}");
            // 联机同屏: 通知同区域其它玩家移除该玩家分身(USER_LEAVE 0x0006)。须在状态清理前发。
            await _townHandler.NotifyLeaveAsync(session);
            var charId = session.Player?.CharacterId ?? 0;
            if (charId > 0) await _sessionDirectory.UnregisterAsync(charId);
            if (charId > 0) InventoryContext.Unregister(session.SessionId, charId);
            Handlers.Dungeon.DungeonRunLifecycle.EndRunOnTeardown(session, "disconnect");
            _townHandler.PersistPosition(session, forceImmediate: true, source: "disconnect");
            _lotteryItemHandler.ClearSession(session.SessionId);
            PetCreatureRuntimeService.UnregisterSession(session);
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<GamePacketHeader>();
            var body = packet.BodyData;

            PacketFileLogger.Log("RECV", packet.GetBytes());

            try
            {
                await OnPacketReceived_86JP(session, header, body);
            }
            catch (Exception ex)
            {
                FileLogger.Log(ex.ToString());
                throw;
            }
        }

        public async Task OnPacketReceived_86JP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (header.cmd == 0)
            {

            }

            if (header.cmd == 1)
            {
                if (_cmdDispatch.TryGetValue(header.type, out var handler))
                    await handler(session, header, body);
                else
                    FileLogger.Log($"[GameProtocol] Unhandled CMD type=0x{header.type:X4} body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            }
        }

        #region CMD Dispatch Registration

        private void RegisterLoginHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0001] = _loginHandler.Handle_ENUM_CMDPACKET_LOGIN;
        }

        private void RegisterCharacterHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0004] = async (s, h, b) =>
            {
                var prevCharId = s.Player?.CharacterId ?? 0;
                await _characterSelectHandler.Handle_ENUM_CMDPACKET_SELECT_CHARACTER(s, h, b);
                if (s.Player != null && s.Player.CharacterId > 0)
                {
                    if (prevCharId > 0 && prevCharId != s.Player.CharacterId)
                    {
                        await _sessionDirectory.UnregisterAsync(prevCharId);
                        InventoryContext.Unregister(s.SessionId, prevCharId);
                    }
                    _sessionDirectory.Register(s.Player.CharacterId, s);
                    var gsConnStr = SqliteDatabaseBootstrap.Initialize(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    s.GameSession = new Game.Session.GameSession(s, gsConnStr);
                    await _inventoryRefreshSender.SendAllEquipmentItemLockListRefresh(s);
                    await s.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(null);
                    await PetCreatureRuntimeService.BeginTownAsync(s, "select_character");
                }
            };
            d[0x0005] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER;
            d[0x0006] = _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER;
            d[0x0007] = async (s, h, b) =>
            {
                var charId = s.Player?.CharacterId ?? 0;
                if (charId > 0) await _sessionDirectory.UnregisterAsync(charId);
                if (charId > 0) InventoryContext.Unregister(s.SessionId, charId);
                _townHandler.PersistPosition(s, forceImmediate: true, source: "return_select");
                s.GameSession = null;
                await _characterSelectHandler.Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(s, h, b);
            };
            d[0x0008] = _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO;
            d[0x0009] = _staminaHandler.Handle_ENUM_CMDPACKET_RECOVER_STAMINA;
            d[0x02B5] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME;
            d[0x0127] = _characterSelectHandler.Handle_CHANGE_CHARAC_SLOT;
        }

        private void RegisterPartyHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x000C] = _partyHandler.Handle_SET_PARTY_INFO;        // 12 创建/更新队伍
            d[0x000D] = _partyHandler.Handle_LEAVE_PARTY;           // 13 退队
            d[0x000E] = _partyHandler.Handle_WALKOUT_PARTY_MEMBER;  // 14 踢人
            d[0x000A] = _partyHandler.Handle_REQUEST_PEER;          // 10 右键同屏玩家→组队/交易邀请(按uid)→给目标发 SC 0x0007 弹框
            d[0x000B] = _partyHandler.Handle_RES_PEER;              // 11 被邀请者应答(body=邀请者uid+reqType)→组队并广播 PARTY_INFO
            d[0x01A3] = _partyHandler.Handle_CREATE_GROUP;          // 419 组队邀请(按名)
            d[0x00A6] = _partyHandler.Handle_CALL_PARTY_MEMBER_REALTIME_INFO;  // 166 请求成员实时信息(HP%)
            d[0x0079] = _partyHandler.Handle_CHANGE_HOST;           // 121 委托队长(body=1字节槽位)
            // P2P 上报类: df 只喂统计计数器, 不回包不转发。收下即忽略, 消掉 Unhandled 日志。
            d[0x0351] = (s, h, b) => Task.CompletedTask;            // P2P_HOLE_PUNCHING_SUCCESS_RATE
            d[0x0061] = (s, h, b) => Task.CompletedTask;            // PEER_CONNECT_RESULT
            d[0x0031] = (s, h, b) => Task.CompletedTask;            // REPORT_BAD_P2P_USER
            d[0x01DF] = (s, h, b) => Task.CompletedTask;            // P2P_STATISTICS
        }

        private void RegisterInventoryHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0012] = _inventoryHandler.Handle_ENUM_CMDPACKET_DELETE_ITEM;       //18
            d[0x0013] = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerMoveItem(s, h, b))
                    return;
                if (await _knightShieldHandler.TryHandleMoveItemSpace(s, h, b))
                    return;
                await _inventoryHandler.Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE(s, h, b);
            };                                                                    //19
            d[0x0014] = _inventoryHandler.Handle_ENUM_CMDPACKET_SORT_ITEM;         //20
            d[0x0015] = _inventoryHandler.Handle_ENUM_CMDPACKET_BUY_ITEM;          //21
            d[0x0016] = _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM;         //22
            d[0x0017] = _inventoryHandler.Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT;  //23 装备修理
            d[0x0019] = _inventoryHandler.Handle_ENUM_CMDPACKET_COMPOUND_ITEM;     //25 compound item / recipe
            d[0x001A] = _inventoryHandler.Handle_ENUM_CMDPACKET_DISJOINT_ITEM;     //26 系统分解
            d[0x00CA] = _inventoryHandler.Handle_DISJOINT_AVATAR;                  //202 时装分解
            d[0x001B] = _lotteryItemHandler.HandleUseLotteryItem;                 //27
            d[(ushort)CmdPacketType.INCREASE_STATUS] = _inventoryHandler.Handle_ENUM_CMDPACKET_INCREASE_STATUS;
            d[0x002C] = _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE;
            d[0x00CC] = _inventoryHandler.Handle_ENUM_CMDPACKET_PURIFY_ITEM;
            d[0x00CD] = _inventoryHandler.Handle_ENUM_CMDPACKET_INVEST_ITEM_AMPLIFY_OPTION;
            d[0x00D0] = _inventoryHandler.Handle_OPEN_MAGIC_BOX_SINGLE;
            d[0x00D9] = _lotteryItemHandler.HandleOverflowInfo;
            d[0x0050] = _inventoryHandler.Handle_ENUM_CMDPACKET_UPGRADE_ITEM;      //80
            d[0x0051] = _inventoryHandler.Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR;   //81 装备品级调整箱(万花镜)
            d[0x00A0] = _inventoryHandler.Handle_OPEN_SELECTABLE_PACKAGE;
            d[(ushort)CmdPacketType.UPGRADE_CHRONICLE] = _inventoryHandler.Handle_UPGRADE_CHRONICLE;
            d[(ushort)CmdPacketType.ENCHANT_3RD_CHRONICLE_ITEM] = _inventoryHandler.Handle_ENCHANT_3RD_CHRONICLE_ITEM;
            d[0x0110] = _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD;   //272
            d[0x0191] = _inventoryHandler.Handle_UNSEAL_RANDOM_OPTION;             //401
            d[0x019C] = _inventoryHandler.Handle_TITLE_BOOK;                       //412
            d[0x01B6] = _inventoryHandler.Handle_CHANGE_RANDOM_OPTION;             //438
            d[0x019D] = _inventoryHandler.Handle_TITLE_BOOK;                       //413
            d[0x0207] = _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE;
            d[0x0218] = _inventoryHandler.Handle_USE_BOOSTER_ITEM;
            d[0x0239] = _inventoryHandler.Handle_SET_CLONE_TITLE;                  //569
            d[0x03F3] = _inventoryHandler.Handle_OPEN_MAGIC_BOX;
            d[0x0063] = _inventoryHandler.Handle_COMPOUND_AVATAR;                  //99 合并装扮(时装合成)
            d[0x0100] = _inventoryHandler.Handle_COMPOUND_EMBLEM;                  //256 徽章合成
            d[0x03EA] = _inventoryHandler.Handle_COMPOUND_AVATAR_SET;              //1002 8件高级装扮100%合成稀有装扮(克隆装扮合成器)
            d[0x0342] = _inventoryHandler.Handle_ADD_EQUIPMENT_EFFECT;             //834 武器特效符文添加
            d[0x0131] = _inventoryHandler.Handle_CREATE_ACCOUNT_CARGO;               //305 开通金库
            d[0x0132] = _inventoryHandler.Handle_UPGRADE_ACCOUNT_CARGO;             //306 扩容金库
            d[0x0133] = _inventoryHandler.Handle_DEPOSIT_MONEY;                    //307 金库存金币
            d[0x0134] = _inventoryHandler.Handle_WITHDRAW_MONEY;                   //308 金库取金币
            d[0x0198] = _inventoryHandler.Handle_UPGRADE_CARGO;                    //408 扩容个人仓库
            d[KnightShieldDeckBodyBuilder.ChangeDeckCommandType] = _knightShieldHandler.HandleChangeDeckInfo;
        }

        private void RegisterPetHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x002C] = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerUseStackable(s, h, b))
                    return;
                if (await _petCreatureHandler.TryHandleUseStackable(s, h, b))
                    return;

                await _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE(s, h, b);
            };
            d[0x0064] = _petCreatureHandler.HandleRenameCreature;
            d[0x0066] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x007A] = _petCreatureHandler.HandleCreatureScriptMessage;
            d[0x00AD] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x00AE] = _petCreatureHandler.HandleRequestHatchedCreature;
            d[0x01E0] = _petCreatureHandler.HandleVerifyCreatureQuest;
        }

        private void RegisterSortItemLockHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x02CA] = _inventoryHandler.Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK;
            d[0x02CB] = _inventoryHandler.Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK;
        }

        private void RegisterEquipmentItemLockHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x010B] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK;
            d[0x010C] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK;
            d[0x010D] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL;
        }

        private void RegisterEquipmentSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x031D] = _inventoryHandler.Handle_EQUIPMENT_SOCKET_OPEN;
        }

        private void RegisterEquipmentEmblemHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x031C] = _inventoryHandler.Handle_EQUIPMENT_EMBLEM_ATTACH;
        }

        private void RegisterAvatarSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00CE] = _inventoryHandler.Handle_AVATAR_SOCKET_OPEN;
        }

        private void RegisterAvatarEmblemHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00C9] = _inventoryHandler.Handle_AVATAR_EMBLEM_ATTACH;
        }

        private void RegisterDungeonHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x000F] = _dungeonHandler.Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON;
            d[0x0010] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_DUNGEON;
            d[0x0027] = _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_MONSTER;
            d[0x0028] = _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_CHARACTER;       //40
            d[0x0029] = _dungeonHandler.Handle_ENUM_CMDPACKET_USE_COIN;
            d[0x002B] = _dungeonHandler.Handle_ENUM_CMDPACKET_GET_ITEM;
            d[0x002D] = _dungeonHandler.Handle_ENUM_CMDPACKET_MOVE_MAP;
            d[0x002E] = _dungeonHandler.Handle_SET_PLAY_RESULT;                    //46
            d[0x002F] = _dungeonHandler.Handle_ENUM_CMDPACKET_DROP_ITEM;
            d[0x0045] = _dungeonHandler.Handle_CARD_START_REQUEST;
            d[0x0047] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_CARD;
            d[0x0048] = _dungeonHandler.Handle_ENUM_CMDPACKET_EPLP_COMMAND;
            d[0x0075] = _dungeonHandler.Handle_BOSS_DIE_CHECK;
            d[0x007B] = _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_RESPAWN;       //123
            d[0x00EB] = _dungeonHandler.Handle_ENUM_CMDPACKET_HELLPARTY_START;     //235
            d[0x008F] = _dungeonHandler.Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG; //143
            d[0x00BF] = _dungeonHandler.Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE; //191
            d[0x0128] = _secretShopHandler.HandleBuyRequest;
            d[0x0129] = _secretShopHandler.HandleOpenClose;
            d[0x013C] = _dungeonHandler.Handle_SPECIAL_SEA_CHASE_OBSERVE;
            d[0x01E4] = _dungeonHandler.Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP;   //484
            d[0x0211] = _dungeonHandler.Handle_SPECIAL_SUMMON_MONSTER;
            d[0x026B] = _dungeonHandler.Handle_SPECIAL_TIMER_MODIFY_INFO;
            d[0x026D] = _dungeonHandler.Handle_SPECIAL_SEA_CHASE_RESULT;
            d[0x0270] = _dungeonHandler.Handle_SPECIAL_SEA_CHASE_OBSERVE;
            d[0x0312] = PremiumQueryHandler.Handle_PREMIUM_SERVICE;                //786
            d[0x03B6] = _dungeonHandler.Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE;
            d[0x03AB] = _dungeonHandler.Handle_BREAK_TRAP_RESULT;                  //939
            d[0x009F] = _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD; // 159
        }

        private void RegisterSkillHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x001C] = _skillHandler.Handle_CHANGE_SKILLSLOT;                     //28
            d[0x001D] = _skillHandler.Handle_BUY_SKILL;                            //29
            d[0x0104] = _skillHandler.Handle_CHANGE_ANOTHER_SKILL_TREE;            //260
            d[0x014B] = _skillHandler.Handle_CHANGE_SKILL_COMMAND;                 //331
            d[0x014C] = _skillHandler.Handle_RESET_ALL_SKILL_COMMANDS;             //332
            d[0x01EC] = _skillHandler.Handle_SKILL_INIT;                           //492
            d[0x01FD] = _skillHandler.Handle_COMBO_SKILL_INFO;                     //509
            d[0x01FF] = _skillHandler.Handle_COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET; //511
        }

        private void RegisterTownHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0023] = _townHandler.Handle_ENUM_CMDPACKET_SET_USER_POSITION;
            d[0x0024] = _townHandler.Handle_ENUM_CMDPACKET_SET_USER_AREA;
            d[0x0025] = _townHandler.Handle_ENUM_CMDPACKET_FINISH_LOADING;
            d[0x002A] = _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME;
            d[0x0084] = _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME;
            d[0x00ED] = _townHandler.Handle_ENUM_CMDPACKET_TELEPORT;
        }

        private void RegisterSettingsHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00C5] = _settingsHandler.Handle_SAVE_GAME_OPTION_1;
            d[0x00C6] = (s, h, b) => { _settingsHandler.Handle_SAVE_GAME_OPTION_2(s, h, b); return Task.CompletedTask; };
            d[0x0170] = (s, h, b) => { _settingsHandler.Handle_SAVE_QUICKCHAT(s, h, b); return Task.CompletedTask; };
            d[0x00FE] = _settingsHandler.Handle_CHANGE_EMOTION;
            d[0x01C0] = (s, h, b) => { _settingsHandler.Handle_SAVE_CHARACTER_OPTION(s, h, b); return Task.CompletedTask; };
        }

        private void RegisterQuestHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x001F] = async (s, h, b) => //31
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleAcceptQuestAsync(h.type, b);
            };
            d[0x0020] = async (s, h, b) => //32
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleGiveupQuestAsync(h.type, b);
            };
            d[0x0021] = async (s, h, b) => //33
            {
                if (s.GameSession != null)
                {
                    var result = await s.GameSession.QuestManager.HandleSetTriggerAsync(h.type, b);
                    await _dungeonHandler.HandleQuestSetTriggerResultAsync(s, result);
                }
            };
            d[0x0022] = async (s, h, b) => //34
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleFinishQuestAsync(h.type, b);
            };
        }

        private void RegisterMailboxHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0060] = _mailboxHandler.HandleOpenMailbox;
        }

        private void RegisterCollectionBoxHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0388] = _collectionBoxHandler.HandleQueryCollectionBox;
            d[0x0389] = _collectionBoxHandler.HandleInsertCollectBoxItem;
            d[0x038A] = _collectionBoxHandler.HandleRemoveCollectBoxItem;
        }

        private void RegisterMercenaryHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x01E5] = _mercenaryHandler.HandleMercenaryRequest;                  //485 支援兵技能列表
            d[0x01E8] = _mercenaryHandler.HandleMercenaryRequest;                  //488 支援兵选择
        }

        private void RegisterMiscHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0003] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0003, CommonPacketBodyBuilder.BuildSuccessAck()));
            d[0x0040] = _ceraShopHandler.HandleCeraShopPurchase;                   //64
            d[0x01A1] = _inventoryHandler.Handle_ACHIEVEMENT_TRIGGER;              //417
            d[0x01DE] = _dungeonHandler.HandleDungeonSceneUniqueIdReport;           //478
            d[0x02A8] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02A8, new byte[] { 0x00, 0x00 }));
            d[0x0372] = _rentalHandler.HandleRentWeapon;
            d[0x0373] = _luckyStarHandler.HandleShopPurchasePacket;
            d[(ushort)CmdPacketType.GET_EXPAND_EXP_GAGE_REWARD] = _growthCapsuleHandler.HandleClaimAsync;
            d[(ushort)CmdPacketType.UPGRADE_CARRY_GOLD] = _goldLimitHandler.HandleUpgradeAsync;
        }

        #endregion
    }
}
