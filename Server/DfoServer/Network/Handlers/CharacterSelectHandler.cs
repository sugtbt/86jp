using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Names;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CharacterSelectHandler
    {
        private readonly ISelectCharacterDataSource _selectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly GetUserInfoTemplate _getUserInfoTemplate;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly Game.Session.ISessionDirectory _sessions;   // 他人外观 PULL: 按 uid 找目标在线会话; 可空(上游注册表)
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly IMercenaryRestrictionService _mercenaryRestrictions;
        private readonly Game.Dungeon.DungeonPersistentEffectApplicationService
            _dungeonPersistentEffects;
        private readonly Game.Dungeon.DungeonInstanceRegistry _dungeonInstances;

        public string ProtocolName => "GameProtocol";

        /// <summary>
        /// 创建选角处理器；公开入口用于不需要副本恢复上下文的调用方。
        /// </summary>
        public CharacterSelectHandler(
            ISelectCharacterDataSource selectCharacterDataSource,
            ICharacterRepository characterRepository,
            GetUserInfoTemplate getUserInfoTemplate,
            Game.Session.ISessionDirectory sessions = null,
            IMercenaryRestrictionService mercenaryRestrictions = null)
            : this(
                null,
                selectCharacterDataSource,
                characterRepository,
                getUserInfoTemplate,
                sessions,
                null,
                mercenaryRestrictions)
        {
        }

        /// <summary>
        /// 创建完整选角处理器，并共享副本、佣兵和团队频道状态，避免切换角色时丢失跨系统约束。
        /// </summary>
        internal CharacterSelectHandler(
            Game.Dungeon.DungeonPersistentEffectApplicationService
                dungeonPersistentEffects,
            ISelectCharacterDataSource selectCharacterDataSource,
            ICharacterRepository characterRepository,
            GetUserInfoTemplate getUserInfoTemplate,
            Game.Session.ISessionDirectory sessions = null,
            Game.Dungeon.DungeonInstanceRegistry dungeonInstances = null,
            IMercenaryRestrictionService mercenaryRestrictions = null)
        {
            _selectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _getUserInfoTemplate = getUserInfoTemplate;
            _honorLevel = new HonorLevelSyncService(_characterRepository);
            _sessions = sessions;
            _growthCapsule = new GrowthCapsuleSyncService(_characterRepository);
            _mercenaryRestrictions = mercenaryRestrictions;
            _dungeonPersistentEffects = dungeonPersistentEffects;
            _dungeonInstances = dungeonInstances;
        }

        // 按 UserId 找在线会话(他人外观拉取用)。
        private EnhancedClientSession FindOnlineByUserId(ushort uid)
        {
            if (_sessions == null) return null;
            foreach (var s in _sessions.GetAllGameSessions())
                if (s?.Player != null && s.Player.CharacterId > 0 && s.Player.UserId == uid)
                    return s;
            return null;
        }

        /// <summary>
        /// 构造在线目标的查看信息 USERINFO 最小包与明细包。
        /// 客户端必须先接收远端路由 0x00 的 subtype0 建立目标上下文，再接收 subtype1 才会登记远程资料。
        /// </summary>
        /// <param name="target">被查询的在线目标会话。</param>
        /// <param name="requesterUserId">发起查看信息请求的客户端 UserId；用于日志关联与运行时核对。</param>
        /// <param name="minimumPacket">目标角色的 subtype0 最小资料包。</param>
        /// <param name="detailedPacket">目标角色的 subtype1 明细包。</param>
        /// <returns>两个包均构造成功时返回 true。</returns>
        private bool TryBuildOnlineUserInfoSequence(
            EnhancedClientSession target,
            ushort requesterUserId,
            out byte[] minimumPacket,
            out byte[] detailedPacket)
        {
            minimumPacket = null;
            detailedPacket = null;

            if (target?.Player == null || target.Player.CharacterId <= 0 || target.Player.UserId == 0)
                return false;

            try
            {
                var accountId = target.Account?.AccountId ?? 1;
                var snapshot = _selectCharacterDataSource.Load(target.Player.CharacterId, accountId);
                var addition = snapshot?.InitializationSnapshot?.UserInfoAddition;
                if (addition == null)
                    return false;

                // 客户端必须先建立 subtype0 最小资料上下文，后续 subtype1 明细才会进入远程资料解析器。
                byte[] minimumBody = null;
                if (snapshot?.CharacterRecord == null
                    || !new UserInfoBodyBuilder().TryBuild(snapshot, 0, out minimumBody)
                    || minimumBody == null
                    || !UserInfoBodyBuilder.TryRewriteSubtype0UserId(minimumBody, target.Player.UserId))
                {
                    return false;
                }

                // 0x01 是选角/入场时的本机资料通道，会把 subtype1 应用到主界面角色；
                // 城镇查看他人资料必须使用 0x00，才能保留独立的远端资料上下文。
                const byte remoteUserInfoRoutingByte = 0x00;
                minimumPacket = BuildPacketWithRouting(0x00, 0x0002, minimumBody, remoteUserInfoRoutingByte);

                // 远程查看信息必须沿用选角时的完整 subtype1 布局。
                // 客户端按装备数量计算后续字段偏移；压缩或清空装备会使整个资料包在解析入口被丢弃。
                // 宠物条目依赖客户端本地 Creature.lst；旧宠物 UID 会触发 Failed Create Creature 0，
                // 使远程资料登记中断。因此远程查看只发送普通装备和时装，保持 subtype1 其余字段布局不变。
                var removedUnsupportedEquipment = 0;
                for (var index = addition.EquippedEntries.Count - 1; index >= 0; index--)
                {
                    var core = addition.EquippedEntries[index]?.Core;
                    if (core == null
                        || addition.EquippedEntries[index].Slot == 24
                        || (core.ItemKind != ItemCore.KindEquipment && core.ItemKind != ItemCore.KindAvatar))
                    {
                        addition.EquippedEntries.RemoveAt(index);
                        removedUnsupportedEquipment++;
                    }
                }
                var equipmentCount = addition.EquippedEntries.Count;
                // 这里不能清空真实装备列表：客户端要先用至少一个可解析的装备条目
                // 建立远程资料 entry，Type2 面板后续才能通过 UID 找到该资料。
                // 过滤后的条目仍来自目标角色的独立 Load 快照，不会修改在线目标库存。
                equipmentCount = addition.EquippedEntries.Count;
                if (snapshot?.CharacterRecord == null
                    || !new UserInfoBodyBuilder().TryBuild(snapshot, 1, out var detailedBody)
                    || detailedBody == null
                    || detailedBody.Length < 5)
                {
                    return false;
                }

                // 不要用零字节把短资料包硬补到固定长度。subtype1 后半段是多个变长列表，
                // 客户端会按列表计数继续读取；补零会把不存在的 creature/任务条目伪造成有效计数，
                // 在 Failed Create Creature 之后中断资料登记，导致 Type2 查不到 entry。
                // UserInfoSubtype1Builder 已按实际列表数量写出完整结构，保留其真实长度即可。

                // subtype1 必须保留目标 UID。真机比较点 0x00F54CCB 将它与本机 UID 比较；
                // 不相等时只跳过“本机角色更新”块，随后仍进入公共资料提交，属于正确的远端分支。
                // 若写成请求方 UID，客户端会记录 My Character Info 并把对方资料刷新到本机主界面。
                var detailCorrelationUid = target.Player.UserId;
                if (!UserInfoBodyBuilder.TryRewriteUserId(detailedBody, detailCorrelationUid))
                {
                    return false;
                }

                // mode=3 的最小资料与明细必须沿用同一个远端路由，避免上下文在两包之间切回本机角色。
                detailedPacket = BuildPacketWithRouting(0x00, 0x0002, detailedBody, remoteUserInfoRoutingByte);
                FileLogger.Log(
                    $"[{ProtocolName}] GET_USERINFO remote compatibility minimumSize={minimumBody.Length} detailBody: " +
                    $"cid={target.Player.CharacterId} targetUid={target.Player.UserId} requesterUid={requesterUserId} " +
                    $"detailHeaderUid={detailCorrelationUid} " +
                    $"equipmentCount={equipmentCount} removedUnsupported={removedUnsupportedEquipment} bodySize={detailedBody.Length} " +
                    // 固定记录前 160 字节，便于和历史成功包逐字段比对，不泄露完整技能和任务列表。
                    $"prefix={BitConverter.ToString(detailedBody, 0, Math.Min(160, detailedBody.Length))}");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TryBuildOnlineUserInfoSequence cid={target.Player.CharacterId} 失败: {ex.Message}");
                return false;
            }
        }

        private static int ResolveAccountId(EnhancedClientSession session, CharacterRecord record)
        {
            if (session?.Account?.AccountId > 0)
                return session.Account.AccountId;

            return record?.AccountId ?? 0;
        }

        private InventoryService TryLoadInventoryForLease(int characterId, int accountId)
        {
            if (characterId <= 0 || accountId <= 0)
                return null;

            try
            {
                var connectionString = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                    Infrastructure.ServerPaths.DatabasePath,
                    Infrastructure.ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    return InventoryService.LoadFromDb(connection, characterId, accountId);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] inventory lease load failed cid={characterId} aid={accountId}: {ex}");
                return null;
            }
        }

        private static void SaveExistingInventoryLeaseBeforeReload(EnhancedClientSession session, int characterId)
        {
            if (session == null || characterId <= 0)
                return;

            if (InventoryContext.TryGetLease(characterId, out var lease)
                && lease.IsOwnedBy(session.SessionId))
                InventoryPersistenceService.SaveDirty(lease);
        }

        private void TryRegisterInventoryLease(
            EnhancedClientSession session,
            CharacterRecord record,
            InventoryService inventory)
        {
            if (session == null || record == null || inventory == null)
                return;

            try
            {
                InventoryContext.Register(session.SessionId, record.CharacterId, inventory);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] inventory lease register failed cid={record.CharacterId} aid={inventory.AccountId}: {ex}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SELECT_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                // 换角色前丢弃上一个角色的副本局: PlayerContext 实例跨角色复用, 不丢会把
                // 上个角色的副本状态带给下个角色。
                Dungeon.DungeonRunLifecycle.EndRunOnTeardown(
                    session,
                    "select_character",
                    _dungeonInstances);

                int slot = 0;
                if (body != null && body.Length >= 2)
                {
                    slot = BitConverter.ToUInt16(body, 0);
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Select character body too short ({body?.Length ?? 0}B), defaulting slot=0");
                }

                CharacterRecord record = null;
                if (session.Account != null)
                {
                    var list = _characterRepository.ListByAccount(session.Account.AccountId);
                    if (list.Count == 0)
                    {
                        FileLogger.Log($"[{ProtocolName}] Select character: account_id={session.Account.AccountId} has 0 characters, falling back to seed character_id={_selectCharacterDataSource.GetSeedCharacterId()}");
                    }
                    else
                    {
                        if (slot < 0 || slot >= list.Count)
                        {
                            FileLogger.Log($"[{ProtocolName}] Select character slot={slot} out of range (count={list.Count}), clamping to 0");
                            slot = 0;
                        }
                        record = list[slot];
                    }
                }
                if (record == null)
                {
                    record = _characterRepository.GetById(_selectCharacterDataSource.GetSeedCharacterId());
                }

                if (record != null)
                {
                    if (_dungeonPersistentEffects != null)
                    {
                        var recovery = _dungeonPersistentEffects
                            .RecoverCharacter(record.CharacterId);
                        if (recovery.CommittedCount > 0)
                        {
                            record = _characterRepository.GetById(
                                record.CharacterId) ?? record;
                        }
                        if (recovery.CommittedCount > 0
                            || recovery.DeadLetterCount > 0
                            || recovery.FailedCount > 0
                            || recovery.HasRemaining)
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] dungeon effect recovery: " +
                                $"cid={record.CharacterId} " +
                                $"committed={recovery.CommittedCount} " +
                                $"dead={recovery.DeadLetterCount} " +
                                $"failed={recovery.FailedCount} " +
                                $"pages={recovery.PagesScanned} " +
                                $"scanned={recovery.RecordsScanned} " +
                                $"remaining={recovery.RemainingCount} " +
                                $"pageLimit={recovery.ReachedPageLimit} " +
                                $"timeLimit={recovery.ReachedTimeLimit}");
                        }
                    }

                    SaveExistingInventoryLeaseBeforeReload(session, record.CharacterId);
                    var inventory = TryLoadInventoryForLease(
                        record.CharacterId,
                        ResolveAccountId(session, record));
                    session.Player.HydrateFrom(record);
                    TryRegisterInventoryLease(session, record, inventory);

                    try
                    {
                        var tail = new Game.CharacterData.SqliteSubtype0FieldsRepository(
                            Infrastructure.ServerPaths.DatabasePath,
                            Infrastructure.ServerPaths.SchemaFilePath).Load(record.CharacterId);
                        var skillTreeIndex = new Game.CharacterData.SqliteSubtype1Repository(
                            Infrastructure.ServerPaths.DatabasePath,
                            Infrastructure.ServerPaths.SchemaFilePath).LoadSkillTreeIndex(record.CharacterId);
                        if (skillTreeIndex.HasValue)
                        {
                            tail = tail ?? new UserInfoMinimumTailSnapshot();
                            tail.SkillTreeIndex = skillTreeIndex.Value;
                        }
                        if (tail == null && session.Account != null)
                            tail = new UserInfoMinimumTailSnapshot();
                        if (tail != null && session.Account != null)
                        {
                            _honorLevel.ApplyToSubtype0Tail(tail, session.Account.AccountId, null);
                        }
                        if (tail != null)
                        {
                            record.Subtype0Tail = tail;
                            session.Player.Subtype0Tail = tail;

                        }

                        // 城镇模型使用会话内的 AppearanceEntries；不要使用可能过期/空的 characters.appearance_blob，
                        // 每次选角都从当前穿戴栏重建，避免角色选人/副本正确但城镇武器外观错误。
                        record.Appearance = AppearanceService.LoadOnlineAppearanceFromInventory(
                            record.CharacterId,
                            record.Job,
                            record.GrowType);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[{ProtocolName}] Select character subtype0 load failed: {ex.Message}");
                    }

                    session.Player.AppearanceEntries = record.Appearance ?? Array.Empty<CharacterAppearanceEntry>();
                    _characterRepository.UpdatePosition(
                        session.Player.CharacterId,
                        session.Player.CurTownId,
                        session.Player.CurAreaId,
                        session.Player.CurPosX,
                        session.Player.CurPosY,
                        session.Player.CurDirection,
                        session.Player.CurAreaState);
                    FileLogger.Log($"[{ProtocolName}] Select character hydrated session {session.SessionId} slot={slot} <- character_id={record.CharacterId} name={record.DisplayName} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Select character: no record resolved, keeping in-memory defaults");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Select character DB load failed: {ex.Message}");
            }

            var ownerCharId = session.Player.CharacterId > 0 ? session.Player.CharacterId : _selectCharacterDataSource.GetSeedCharacterId();
            var ownerAcctId = session.Account?.AccountId ?? 1;
            var characterList = BuildCharacterList(ownerAcctId);
            var routingByte = _getUserInfoTemplate != null ? _getUserInfoTemplate.Pkt0RoutingByte7 : (byte)0;

            // Character selection rebuilds the current character's complete skill state.
            // Keep reconciliation explicit because shared snapshot loads also serve non-skill refreshes.
            if (_selectCharacterDataSource is SqliteSelectCharacterDataSource sqliteDataSource)
            {
                sqliteDataSource.PrepareForSkillSynchronization(
                    ownerCharId,
                    ownerAcctId);
            }

            foreach (var packet in SelectCharacterPacketBuilder.BuildPacketStream(_selectCharacterDataSource, ownerCharId, ownerAcctId))
                await session.SendPacketAsync(packet);

            var visibilityBits = session.Player.Subtype0Tail?.UserStateBits ?? (byte)3;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.CHARAC_INVISIBLE_FALGS,
                CharacterVisibilityBodyBuilder.Build(session.Player.UserId, visibilityBits)));

            var cloneTitle = AppearanceService.LoadCloneTitleItemId(ownerCharId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0239,
                AppearanceService.BuildCloneTitleAckBody(cloneTitle, suppressMessage: 1)));
            FileLogger.Log($"[{ProtocolName}] SELECT_CHARACTER clone title restore: char={ownerCharId} cloneTitle=0x{cloneTitle:X8}");

            // 切角色可能跳过 GET_USERINFO，主选角流后补发账号 subtype2。
            await session.SendPacketAsync(BuildPacketWithRouting(0x00, 0x0002, characterList.Body, routingByte));
            await SendHonorLevelInfoAsync(session, "select-character-ready", characterList.Honor);
            await _growthCapsule.SendExpProgressAsync(
                session, "select-character-ready", honor: characterList.Honor);
        }

        /// <summary>
        /// 处理客户端的角色资料请求；查询在线他人时返回历史兼容的 subtype1 明细包。
        /// </summary>
        /// <param name="session">发起资料查询的在线会话。</param>
        /// <param name="header">当前游戏包头。</param>
        /// <param name="body">请求体，在线他人查询格式为 u16 UserId 加 mode=0x03。</param>
        /// <returns>异步发送资料包的任务。</returns>
        public async Task Handle_ENUM_CMDPACKET_GET_USERINFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                // 他人外观(同屏 PULL 模型): body = {u16 uid, byte mode}(见 docs/df_game_r/06-otheruser-appearance.md)。
                // mode!=2 且 uid 有效且目标在线 → 回目标 USERINFO(0x0002, 复用自身版 BuildNoti2Body 换数据源)。
                // 自身/选角 roster(mode==2 或 body<3B)走下面既有分支。⚠️ 真机需确认客户端是否用 0x0008 发他人请求 + mode 取值。
                // 诊断: 查看信息(inspect)真机排查用。记录客户端发的完整 body, 好核对 reqUid 映射。
                FileLogger.Log($"[{ProtocolName}] GET_USERINFO body={(body != null ? BitConverter.ToString(body) : "null")} selfUid={session.Player?.UserId} selfCid={session.Player?.CharacterId}");
                if (_sessions != null && body != null && body.Length >= 3)
                {
                    ushort reqUid = BitConverter.ToUInt16(body, 0);
                    byte mode = body[2];
                    if (mode != 0x02 && reqUid != 0xFFFF && reqUid != session.Player.UserId)
                    {
                        var target = FindOnlineByUserId(reqUid);
                        if (target != null)
                        {
                            // mode=3 是查看他人资料的按需查询。城镇同屏 Creature 不等同于资料窗口上下文，
                            // 必须先发送远端路由的 subtype0 建立目标资料对象，再用 subtype1 填充完整明细。
                            var sequenceBuilt = TryBuildOnlineUserInfoSequence(
                                target,
                                session?.Player?.UserId ?? 0,
                                out var minimumPacket,
                                out var detailedPacket);
                            if (sequenceBuilt)
                            {
                                // 两个资料包使用相同目标 UID 和远端路由 0x00；先建立上下文，避免 subtype1
                                // 被当作普通同屏资料刷新。客户端处理完明细后再结束 CMD 8 命令事务。
                                await session.SendPacketAsync(minimumPacket);
                                await session.SendPacketAsync(detailedPacket);

                                // ACK 必须放在资料包之后，避免提前结束查询状态。
                                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                                    0x01,
                                    (ushort)CmdPacketType.GET_USERINFO,
                                    CommonPacketBodyBuilder.BuildSuccessAck()));
                            }
                            FileLogger.Log(
                                $"[{ProtocolName}] GET_USERINFO other MATCH reqUid={reqUid} mode={mode} " +
                                $"-> USERINFO subtype0+subtype1+cmd8Ack={(sequenceBuilt ? 1 : 0)} sent " +
                                $"(targetCid={target.Player.CharacterId} targetUid={target.Player.UserId} " +
                                $"requesterUid={session?.Player?.UserId ?? 0} detailHeaderUid={(sequenceBuilt && detailedPacket.Length >= 20 ? BitConverter.ToUInt16(detailedPacket, 18) : 0)} " +
                                $"routingByte7=0x{(sequenceBuilt ? detailedPacket[7] : (byte)0):X2})");
                            return;
                        }
                        // 未匹配 → 枚举在线 uid, 让真机日志直接显示 reqUid 是否=某在线目标的 UserId(诊断 uid 映射)
                        var sb = new System.Text.StringBuilder();
                        foreach (var s in _sessions.GetAllGameSessions())
                            if (s?.Player != null && s.Player.CharacterId > 0)
                                sb.Append($"uid{s.Player.UserId}/cid{s.Player.CharacterId} ");
                        FileLogger.Log($"[{ProtocolName}] GET_USERINFO other reqUid={reqUid} mode={mode} 未匹配在线目标, 回退 roster(⚠️信息窗无反应根因候选=uid映射). 在线=[{sb.ToString().Trim()}]");
                    }
                }

                var accountId = session.Account?.AccountId ?? 1;
                var characterList = BuildCharacterList(accountId);
                byte routingByte = _getUserInfoTemplate != null ? _getUserInfoTemplate.Pkt0RoutingByte7 : (byte)0;
                await session.SendPacketAsync(BuildPacketWithRouting(0x00, 0x0002, characterList.Body, routingByte));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0286, new byte[] { 0x00, 0x04 }));
                await SendHonorLevelInfoAsync(session, "get-userinfo-ready", characterList.Honor);
                await _growthCapsule.SendExpProgressAsync(
                    session, "get-userinfo-ready", honor: characterList.Honor);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] GET_USERINFO EXCEPTION: {ex}");
            }
        }

        private static bool NameBytesEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// 构造带外层第 7 字节路由标志的游戏包，供 USERINFO 等依赖客户端分发状态的通知复用。
        /// </summary>
        /// <param name="command">游戏包命令字节。</param>
        /// <param name="type">游戏包类型。</param>
        /// <param name="body">待封装的包体。</param>
        /// <param name="routingByte7">客户端使用的第 7 字节路由标志。</param>
        /// <returns>包含固定 15 字节外层头的完整游戏包。</returns>
        internal static byte[] BuildPacketWithRouting(byte command, ushort type, byte[] body, byte routingByte7)
        {
            int totalLen = 15 + (body != null ? body.Length : 0);
            var packet = new byte[totalLen];
            packet[0] = command;
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 1, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalLen), 0, packet, 3, 4);
            packet[7] = routingByte7;
            if (body != null && body.Length > 0)
                Buffer.BlockCopy(body, 0, packet, 15, body.Length);
            return packet;
        }

        public async Task Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x02 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 0);
            if (nameLen <= 0 || nameLen > 30 || 4 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x14 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 4, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 30, out var name, out var failure))
            {
                FileLogger.Log($"[{ProtocolName}] CHECK_NAME: invalid name reason={failure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x02B5,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var existing = _characterRepository.GetByName(name);
            if (existing != null)
            {
                // 20/24 公告 已存在的角色名
                // 159 公告 包含无法使用的文字
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x02B5,
                    CommonPacketBodyBuilder.BuildCmdError(24)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] CHECK_NAME: '{name}' is available");
        }

        public async Task Handle_ENUM_CMDPACKET_CREATE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var job = body[0];
            if (job > 12)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 1);
            if (nameLen < 2 || nameLen > 18 || 5 + nameLen + 1 > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x12 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 5, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 18, out var nameStr, out var nameFailure))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: invalid name reason={nameFailure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0005,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var accountId = session.Account?.AccountId ?? 1;

            var count = _characterRepository.CountByAccount(accountId);
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(_getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2);
            if (!CharacterSlotPolicy.HasAvailableSlot(count, _getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: account_id={accountId} has no free character slot (count={count}, limit={slotLimit})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            if (_characterRepository.GetByName(nameStr) != null)
            {
                // 与 CHECK_DOUBLE_CHARACTER_NAME 一致：24 = 已存在的角色名
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0005,
                    CommonPacketBodyBuilder.BuildCmdError(24)));
                return;
            }

            try
            {
                var record = new CharacterRecord
                {
                    AccountId = accountId,
                    Name = nameRaw,
                    Job = job,
                    GrowType = 0,
                    Level = 1,
                    TownId = 1,
                    AreaId = 0,
                    PosX = 474,
                    PosY = 234,
                    Direction = 5,
                    AreaState = 3,
                };

                var newCharId = _characterRepository.Create(record);
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: created character_id={newCharId} name='{nameStr}' job={job} for account_id={accountId}");

                _selectCharacterDataSource.InitializeNewCharacter(newCharId, accountId, job);

                // 1. CMD ACK success
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, CommonPacketBodyBuilder.BuildSuccessAck()));

                // 2. NOTI 2 subtype 2 — character list refresh
                var characterList = BuildCharacterList(accountId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0002, characterList.Body));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_DELETE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var slotIndex = body[0];
            var nameLen = BitConverter.ToInt32(body, 1);
            if (nameLen <= 0 || nameLen > 30 || 5 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var name = Encoding.UTF8.GetString(body, 5, nameLen);
            var accountId = session.Account?.AccountId ?? 1;

            var list = _characterRepository.ListByAccount(accountId);
            if (slotIndex >= list.Count)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var target = list[slotIndex];
            if (!NameBytesEqual(target.Name, Encoding.UTF8.GetBytes(name)))
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: name mismatch slot={slotIndex} expected='{target.DisplayName}' got='{name}'");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x15 }));
                return;
            }

            if (_mercenaryRestrictions != null && !_mercenaryRestrictions.CanDelete(target.CharacterId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DELETE_CHARACTER blocked: character_id={target.CharacterId} is on mercenary expedition");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0006,
                    new byte[] { 0x28 }));
                return;
            }

            try
            {
                _characterRepository.SoftDelete(target.CharacterId);
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: soft-deleted character_id={target.CharacterId} name='{name}'");

                var writer = new GamePacketWriter();
                writer.WriteByte(0x00);
                writer.WriteUInt16((ushort)target.CharacterId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, writer.ToArray()));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x28 }));
            }
        }

        /// <summary>
        /// 处理返回选角请求；团队频道会退出到频道选择场景，普通频道仍返回当前连接的角色列表。
        /// </summary>
        /// <param name="session">当前游戏连接。</param>
        /// <param name="header">客户端请求头。</param>
        /// <param name="body">客户端请求包体。</param>
        public async Task Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var shieldReset = BuildKnightShieldReturnSelectReset(session?.Player);
            if (shieldReset != null)
            {
                // 423 窗口会跨角色保留 catalog。离开角色时先把它的五槽归一为空，
                // 避免下一守护者的真实物品 ID 被旧 growType catalog 反向清零。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    KnightShieldDeckBodyBuilder.DeckNotificationType,
                    shieldReset));
                FileLogger.Log(
                    $"[{ProtocolName}] RETURN_SELECT_CHARACTER: cleared client knight-shield deck " +
                    $"for character_id={session.Player.CharacterId}");
            }

            Dungeon.DungeonRunLifecycle.EndRunOnTeardown(
                session,
                "return_select_character",
                _dungeonInstances);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0007, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] RETURN_SELECT_CHARACTER: sent ACK for session {session.SessionId}");

            await SendCharacterListAsync(session);
        }

        internal static byte[] BuildKnightShieldReturnSelectReset(Game.Session.PlayerContext player)
        {
            if (player == null
                || player.CharacterId <= 0
                || !KnightShieldDataProvider.IsEligibleCharacter(player.Job))
            {
                return null;
            }

            return KnightShieldDeckBodyBuilder.BuildDeck(new KnightShieldDeckSnapshot());
        }

        public async Task Handle_CHANGE_CHARAC_SLOT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 8)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_CHARAC_SLOT body too short ({body?.Length ?? 0}B)");
                return;
            }

            var slotA = BitConverter.ToUInt32(body, 0);
            var slotB = BitConverter.ToUInt32(body, 4);
            var accountId = session.Account?.AccountId ?? 1;

            _characterRepository.SwapSlotIndexes(accountId, (byte)slotA, (byte)slotB);
            FileLogger.Log($"[{ProtocolName}] CHANGE_CHARAC_SLOT swapped slot {slotA} <-> {slotB} for account_id={accountId}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0127, CommonPacketBodyBuilder.BuildSuccessAck()));
            await SendCharacterListAsync(session);
        }

        public async Task SendCharacterListAsync(EnhancedClientSession session)
        {
            var accountId = session.Account?.AccountId ?? 1;
            var characterList = BuildCharacterList(accountId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, characterList.Body));
            await SendHonorLevelInfoAsync(session, "character-list-ready", characterList.Honor);
            FileLogger.Log($"[{ProtocolName}] Sent character list for account_id={accountId}");
        }

        private Task SendHonorLevelInfoAsync(
            EnhancedClientSession session,
            string reason,
            HonorLevelSummary summary)
        {
            return _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
        }

        private (byte[] Body, HonorLevelSummary Honor) BuildCharacterList(int accountId)
        {
            var characters = _characterRepository.ListByAccount(accountId);
            var honorLevel = _honorLevel.LoadSummary(accountId, characters);
            var body = AccountCharacterListBodyBuilder.Build(
                characters, _getUserInfoTemplate, out _, honorLevel, accountId);
            return (body, honorLevel);
        }
    }
}
