using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Parsers.Party;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 组队 wire 层(Phase A: 本地三包 SET_PARTY_INFO 0x0C / LEAVE_PARTY 0x0D / WALKOUT 0x0E)。
    // 状态走 PartyManager(格式无关), 队伍窗口靠 PARTY_INFO(Noti 0x09) 重发整份名册刷新(df 新建/更新即如此)。
    // ⚠️ Phase A 只向请求者本会话下发 PARTY_INFO(单人队够用); 多人广播需 UserId→session 注册表, 留待 Phase B。
    // ⚠️ 响应帧照 df 逆向(PARTY_INFO 无活体样本), 需真机验证客户端渲染(参 compound #432 教训)。
    public sealed class PartyHandler
    {
        private readonly PartyManager _partyManager;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly Game.Session.ISessionDirectory _sessions;   // 跨会话定位(邀请/广播); 单人/自测时可为 null。采用上游会话注册表(按 charId 查, 抗重连)
        private readonly Game.Dungeon.DungeonInstanceRegistry _dungeonInstances;
        private const string ProtocolName = "GameProtocol";

        public PartyHandler(PartyManager partyManager, ICharacterRepository characterRepository,
            Game.Session.ISessionDirectory sessions = null)
            : this(
                partyManager,
                characterRepository,
                sessions,
                dungeonInstances: null)
        {
        }

        internal PartyHandler(
            PartyManager partyManager,
            ICharacterRepository characterRepository,
            Game.Session.ISessionDirectory sessions,
            Game.Dungeon.DungeonInstanceRegistry dungeonInstances)
        {
            _partyManager = partyManager;
            _characterRepository = characterRepository;
            _subtype0Repository = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            _honorLevel = new HonorLevelSyncService(characterRepository);
            _sessions = sessions;
            _dungeonInstances = dungeonInstances;
            // 断线清理走会话目录的生命周期事件: 断线者自动退队, 剩余成员收到名册刷新。
            // 不订阅就会产生幽灵队员(断线者永久留在名册里)。事件在 session 从目录移除前触发,
            // 只向【剩余成员】发包, 绝不向垂死会话本身发(其 socket 已在关闭流程中)。
            if (_sessions != null)
                _sessions.SessionEnding += OnSessionEndingAsync;
        }

        private async Task OnSessionEndingAsync(int characterId, EnhancedClientSession dying)
        {
            var uid = (ushort)characterId;
            var result = _partyManager.OnSessionDisconnected(uid);
            if (!result.Ok)
                return;   // 不在任何队伍, 无事可做

            FileLogger.Log($"[{ProtocolName}] PARTY disconnect cleanup: uid={uid} disbanded={result.Disbanded} leaderChanged={result.LeaderChanged} newLeader={result.NewLeaderUserId} remaining={result.RemainingMembers.Count}");
            if (!result.Disbanded && result.Party != null && result.Party.Count >= 1)
                await BroadcastPartyInfo(result.Party);
        }

        // 建队/入队时若目标玩家原本在别的队伍, 原队剩余成员需要收到名册刷新(否则那边永远显示旧名册)。
        private async Task NotifyPriorPartyAsync(PartyOpResult prior)
        {
            if (prior == null || !prior.Ok || prior.Disbanded || prior.Party == null || prior.Party.Count < 1)
                return;
            await BroadcastPartyInfo(prior.Party);
        }

        private PartyMember BuildMember(EnhancedClientSession session, int cid)
        {
            var rec = _characterRepository.GetById(cid);
            // P2P 端点: 用会话 TCP 远端的 LAN IP(局域网可达地址, 比客户端报的虚拟网卡 IP 更可靠)。
            byte[] ipBytes = new byte[] { 127, 0, 0, 1 };
            try
            {
                if (session.TcpClient?.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                    ipBytes = ep.Address.MapToIPv4().GetAddressBytes();   // 4 字节 octets a.b.c.d
            }
            catch { /* 端点不可用则回环兜底 */ }
            return new PartyMember
            {
                UserId = (ushort)cid,
                CharacterId = cid,
                SessionId = session.SessionId,
                Name = rec?.DisplayName ?? string.Empty,
                Level = rec?.Level ?? 1,
                Job = rec?.Job ?? 0,
                IpBytes = ipBytes,
                // 客户端真实 UDP 端口(SET_UDP 0x0002 上报, 每次开游戏动态变); 未上报回落 10000。
                P2pPort = (session.Player != null && session.Player.P2pPort != 0) ? session.Player.P2pPort : (ushort)10000,
                AccId = (uint)cid,
            };
        }

        // 0x000C 创建/更新队伍。无队则建(请求者=队长), 更新设置, 回整份 PARTY_INFO(type=0)。
        public async Task Handle_SET_PARTY_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] SET_PARTY_INFO recv({body?.Length ?? 0}B): {(body != null ? System.BitConverter.ToString(body) : "null")}");
            if (!SetPartyInfoRequest.TryParse(body, out var req))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000C, new byte[] { 0x00, 0x04 }));
                return;
            }

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;

            var party = _partyManager.GetPartyByUser(uid);
            if (party == null)
            {
                var createdResult = _partyManager.CreateParty(BuildMember(session, cid));
                party = createdResult.Party;
                await NotifyPriorPartyAsync(createdResult.PriorPartyLeave);
            }
            party.TitleIndex = 0;
            // 队名: 本包 7 字节不带自定义队名 → 默认用队长角色名(DNF 新队默认名), 否则客户端显示"无队伍名"。
            var leaderName = _characterRepository.GetById(cid)?.Name ?? System.Array.Empty<byte>();
            party.TitleBytes = (req.Title != null && req.Title.Length > 0) ? req.Title : leaderName;
            // SET_PARTY_INFO body(86jp, titleIndex==0 不带队名串)。真机: 选"4人"时 body=00-06-04-...,
            //   即 byte[1] 不是人数(选4却=6, 是别的选择项)、byte[2]=04 才是人数上限4。故上限恒取 4。
            //   "队伍人员已满"与上限无关(客户端本地按名册算), 见记忆。
            party.UserMax = 4;
            party.DungIndex = 0;
            party.DungDiffi = 0;

            FileLogger.Log($"[{ProtocolName}] SET_PARTY_INFO uid={uid} party={party.PartyId} titleIdx={req.TitleIndex} userMax={party.UserMax} dung={req.DungIndex}/{req.DungDiffi} members={party.Count}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0009, PartyInfoNotiBuilder.Build(party, 0)));
            // 主动补发实时信息(0x0099), 保证组队窗口 HP/MP 立即填充(不依赖客户端是否请求 0x00A6)。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0099, PartyRealtimeInfoBuilder.Build(party)));
        }

        // 0x000D 退队。df 成功无对本人回包(走广播刷新剩余成员); 失败回 0x12(不在队)。
        public async Task Handle_LEAVE_PARTY(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;

            var result = _partyManager.Leave(uid);
            if (!result.Ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000D, new byte[] { 0x00, 0x12 }));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] LEAVE_PARTY uid={uid} disbanded={result.Disbanded} leaderChanged={result.LeaderChanged} newLeader={result.NewLeaderUserId}");
            // 给【离队者本人】发 PARTY_INFO type=3(清空组队窗口)——本客户端不自己清(逆 sub_D1BD10: type==3→清所有槽)。
            if (result.Party != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0009, PartyInfoNotiBuilder.Build(result.Party, 3)));
            // ★副本内退队: 若退队者仍在副本实例里, 把他清 run + 拉回城镇(否则人被移出队伍却卡在本里)。
            await PullBackToTownIfInDungeonAsync(session, "LEAVE_PARTY 退队者");
            // 顺位继承(队长走→下一成员升队长)+ 广播更新后的 PARTY_INFO 给剩余成员, 让 B 那边名册/队长即时更新。
            if (!result.Disbanded && result.Party != null && result.Party.Count >= 1)
                await BroadcastPartyInfo(result.Party);
        }

        // 退队/被踢共用: 若该会话仍在副本实例里, 清 run + 发 4 包城镇序列拉回城镇(否则人离队却卡本里)。
        // 对应 df leave_user 的 set_state + sendInoutConditionDungeon。
        private async Task PullBackToTownIfInDungeonAsync(EnhancedClientSession s, string reason)
        {
            var run = s?.Player?.CurrentRun;
            if (run == null) return;
            var runIdentity = run.CaptureIdentity();
            if (!await Dungeon.DungeonRunLifecycle.EndRunAsync(
                    s,
                    Game.Dungeon.DungeonRunEndReason.PartyLeave,
                    runIdentity,
                    _dungeonInstances))
            {
                return;
            }
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            s.Player.UserState = 0x00;
            var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(s.Player);
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003, EnterSelectDungeonStateBuilder.BuildUserState(s.Player)));
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snap)));
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018, TownAreaNotificationBuilder.BuildAreaUsers(snap)));
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA, new byte[] { 0x00 }));
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            await UserInfoBroadcastService.SendSubtype0Async(
                s,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0");
            if (!Dungeon.DungeonRunLifecycle.CanProjectTownState(s, runIdentity))
                return;
            FileLogger.Log($"[{ProtocolName}] {reason}: cid={s.Player.CharacterId} 在副本内 → 清 run + 拉回城镇");
        }

        internal async Task<bool> TryRestoreDungeonParticipantAsync(
            EnhancedClientSession session,
            int partyId)
        {
            if (session?.Player == null
                || partyId <= 0
                || partyId > ushort.MaxValue)
            {
                return false;
            }

            var characterId = session.Player.CharacterId;
            var userId = session.Player.UserId;
            var current = _partyManager.GetPartyByUser(userId);
            if (current != null && current.PartyId == partyId)
                return true;

            var party = _partyManager.GetPartyById(partyId);
            if (party == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_REJOIN party missing: " +
                    $"cid={characterId} party={partyId}");
                return false;
            }

            var join = _partyManager.Join(
                partyId,
                BuildMember(session, characterId));
            if (!join.Ok)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_REJOIN party restore rejected: " +
                    $"cid={characterId} party={partyId} reason={join.Reason}");
                return false;
            }

            await NotifyPriorPartyAsync(join.PriorPartyLeave);
            await BroadcastPartyInfo(join.Party);
            FileLogger.Log(
                $"[{ProtocolName}] DUNGEON_REJOIN party restored: " +
                $"cid={characterId} uid={userId} party={partyId}");
            return true;
        }

        internal async Task RollbackDungeonParticipantRestoreAsync(
            EnhancedClientSession session,
            int partyId)
        {
            var userId = session?.Player?.UserId ?? 0;
            if (userId == 0)
                return;

            var current = _partyManager.GetPartyByUser(userId);
            if (current == null || current.PartyId != partyId)
                return;

            var leave = _partyManager.Leave(userId);
            if (leave.Ok
                && !leave.Disbanded
                && leave.Party != null
                && leave.Party.Count > 0)
            {
                await BroadcastPartyInfo(leave.Party);
            }
        }

        // 0x00A6 CALL_PARTY_MEMBER_REALTIME_INFO: 客户端请求成员实时信息(空 body)→ 回 0x0099(HP% + 成员列表)。
        // 该字节 = HP 百分比(不是体力), 客户端 HP 取到后再取 MP; 不回则组队窗口"信息异常"。
        public async Task Handle_CALL_PARTY_MEMBER_REALTIME_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var party = _partyManager.GetPartyByUser((ushort)cid);
            if (party == null)
                return;   // 不在队则不回(df: check_error 要求在队/正确状态)
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0099, PartyRealtimeInfoBuilder.Build(party)));
        }

        // 0x000E 踢人。请求 = byte 目标 slot。仅队长可踢; 失败回 0x13。成功后剩余成员重发 PARTY_INFO。
        public async Task Handle_WALKOUT_PARTY_MEMBER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;

            var party = _partyManager.GetPartyByUser(uid);
            PartyMember target = null;
            if (body != null && body.Length >= 1 && party != null)
                target = party.MembersBySlot().FirstOrDefault(m => m.SlotIndex == body[0]);

            if (party == null || target == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000E, new byte[] { 0x00, 0x13 }));
                return;
            }

            var result = _partyManager.Kick(uid, target.UserId);
            if (!result.Ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000E, new byte[] { 0x00, 0x13 }));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] WALKOUT uid={uid} kickedSlot={body[0]} target={target.UserId} disbanded={result.Disbanded}");

            // 1) 给【被踢者】发 PARTY_INFO type=3 清空其组队窗口(否则被踢者仍显示在队里, 不知已被踢)+ 副本内拉回城。
            var kickedSession = FindSessionByUserId(target.UserId);
            if (kickedSession != null)
            {
                await kickedSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0009, PartyInfoNotiBuilder.Build(result.Party ?? party, 3)));
                await PullBackToTownIfInDungeonAsync(kickedSession, "WALKOUT 被踢者");
            }
            // 2) 广播更新后的 PARTY_INFO 给剩余成员(含踢人者自己), 名册即时去掉被踢者。
            if (!result.Disbanded && result.Party != null && result.Party.Count >= 1)
                await BroadcastPartyInfo(result.Party);
            else if (result.Disbanded)
                // 踢后只剩队长自己→队伍解散: 也清队长自己的组队窗口。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0009, PartyInfoNotiBuilder.Build(party, 3)));
        }

        // ==== Phase B: 组队邀请流(需 ISessionDirectory 跨会话定位)。假人两连接可端到端联调状态机。====
        // 0x0079 CHANGE_HOST:【委托队长】(真机实测右键队伍成员→委托队长, 客户端发 0x0079 body=1字节槽位)。
        // ⚠️ 脱壳定论(sub_14A4AA0 有序 slot-diff):此客户端【不支持原地换队长】。队长由【slot0】判定(PARTY_INFO
        //    名册块无独立 leader 字段), 想让 B 当队长必须把 B 挪到 slot0。但客户端收到"某成员移到更低槽位"的 diff 时,
        //    会 clobber 该成员自身的回指指针(memberObj+1028)→ B 自己被踢出队伍;A 侧则卡"连接中"。真机已复现。
        // 修法(Option A 解散重建):先给两端发 PARTY_INFO type=3(sub_D1BD10: type==3→清所有槽), 把队伍窗口清空,
        //   再用 [B(新队长/slot0), 其余成员按原序] 重新组队并广播——因客户端是【从空重建】而非 diff, 不触发 clobber。
        //   全用初始组队时已验证过的成熟原语(type=3 清窗 + CreateParty/Join + BroadcastPartyInfo formation 序列)。
        // ⚠️ 客户端渲染结果须真机确认(晨测清单 ①); 若仍异常, 回退为"安全空操作"(不重排/不解散, 保持 A 队长不崩)。
        public async Task Handle_CHANGE_HOST(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 1) return;
            byte slot = body[0];
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var byUid = (ushort)cid;
            var party = _partyManager.GetPartyByUser(byUid);
            if (party == null)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} 无队伍, 忽略");
                return;
            }
            if (party.LeaderUserId != byUid)   // 仅当前队长可委托
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} 非队长(leader={party.LeaderUserId}), 忽略");
                return;
            }
            // 按 SlotIndex 映射(不是 list 下标!与 BroadcastPartyInfo 下发的槽位一致)。
            var target = party.MembersBySlot().FirstOrDefault(m => m.SlotIndex == slot);
            if (target == null || target.UserId == byUid)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} slot={slot} 空槽/无此成员/委托给自己, 忽略");
                return;
            }

            // --- Option A: 解散 + 以 [target(新队长), 其余原序] 重建 ---
            int oldPartyId = party.PartyId;
            var newLeader = target;                                   // B → slot0
            var rest = party.MembersBySlot().Where(m => m.UserId != newLeader.UserId).ToList(); // 其余(含 A)保持原序
            var oldMembers = party.MembersBySlot();                   // 清窗要发给全体(含离场重建前的所有成员会话)

            // 1) 先给全体在线成员发 PARTY_INFO type=3 清空组队窗口(避免客户端做 slot-diff)。
            foreach (var m in oldMembers)
            {
                EnhancedClientSession ms = null;
                if (_sessions != null) _sessions.TryGet(m.CharacterId, out ms);
                if (ms != null && ms.TcpClient.Connected)
                    await ms.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0009, PartyInfoNotiBuilder.Build(party, 3)));
            }

            // 2) 服务端解散旧队 + 以新队长为 slot0 重建(TryAddMember 会重新分配 SlotIndex)。
            _partyManager.Disband(oldPartyId);
            var newParty = _partyManager.CreateParty(newLeader).Party;   // newLeader.SlotIndex→0, LeaderUserId=newLeader
            foreach (var m in rest)
            {
                var jn = _partyManager.Join(newParty.PartyId, m);     // 依次 slot1,2,3
                if (!jn.Ok)
                    FileLogger.Log($"[{ProtocolName}] CHANGE_HOST rebuild: Join uid={m.UserId} 失败 {jn.Reason}");
            }

            FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: 委托队长 {byUid} → {newLeader.UserId}(slot={slot}); 解散旧队 {oldPartyId} → 重建 {newParty.PartyId} 成员=[{string.Join(",", newParty.MembersBySlot().Select(x => $"uid{x.UserId}@slot{x.SlotIndex}"))}]; 全体先 type=3 清窗再 formation 广播");
            // 3) 像初始组队一样广播(0x99→0x0B→0x09 formation 序列), 客户端从空重建, 不触发 slot-diff clobber。
            await BroadcastPartyInfo(newParty);
        }

        // 0x01A3 CREATE_GROUP: 86jp 真实"组队邀请"(右键玩家→组队邀请 / 组队窗口输名邀请)。
        // 真机抓包实证 body = int32 nameLen + name(GBK)。df 无此 cmd(86 版新增; df 的 MEMBER_ENTER 0x4C
        // 在此客户端渲染为【师徒】——之前接错了)。
        // 简化实现(待真机细化 accept 流程/组通知 0x016A/0x016D): 按名找目标 → 直接并入邀请者的队(目标视作已接受)
        // → 给全体成员广播 PARTY_INFO(0x09, 已验证真机渲染队伍窗口)+ 实时(0x99)。
        public async Task Handle_CREATE_GROUP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var targetName = ParseNameArg(body);
            FileLogger.Log($"[{ProtocolName}] CREATE_GROUP by={SessionOwnerResolver.Resolve(session).characterId} target({targetName?.Length ?? 0}B)={(targetName != null ? System.BitConverter.ToString(targetName) : "null")}");
            if (targetName == null || targetName.Length == 0 || _sessions == null)
                return;

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var inviterUid = (ushort)cid;

            var targetSession = FindSessionByCharacterName(targetName);
            if (targetSession == null)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_GROUP: 目标不在线/未找到");
                return;
            }
            var (tcid, _) = SessionOwnerResolver.Resolve(targetSession);
            var targetUid = (ushort)tcid;
            if (targetUid == inviterUid)
                return;

            var party = _partyManager.GetPartyByUser(inviterUid);
            if (party == null)
            {
                var createdResult = _partyManager.CreateParty(BuildMember(session, cid));
                party = createdResult.Party;
                await NotifyPriorPartyAsync(createdResult.PriorPartyLeave);
            }
            if (party.Contains(targetUid))
            {
                await BroadcastPartyInfo(party);   // 已在队, 重发名册刷新
                return;
            }
            if (party.IsFull)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_GROUP: 队伍已满");
                return;
            }
            var join = _partyManager.Join(party.PartyId, BuildMember(targetSession, tcid));
            if (!join.Ok)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_GROUP: 入队失败 {join.Reason}");
                return;
            }
            await NotifyPriorPartyAsync(join.PriorPartyLeave);
            FileLogger.Log($"[{ProtocolName}] CREATE_GROUP: uid={targetUid} 并入 party={party.PartyId} members={join.Party.Count}");
            await BroadcastPartyInfo(join.Party);
        }

        // 0x000A REQUEST_PEER: 真机实测——右键同屏玩家→组队邀请发的就是它(不是 0x01A3 按名, 是按 uid)。
        // 请求 body = 目标 uid(u16) + type(byte) + int32(peer id) [+尾]。
        // df DisPatcher_ReqPeer(cmd10, @0x081EED08): 按 uid find_from_world 找目标 → 给目标发 REQUEST_PEER(SC 0x0007,
        //   put_header(0,7)+put_short(uid)+put_byte(结果类型)+put_int+put_short×3) 让其弹"X 邀请你组队"框。
        // 布局/值先按 df 结构走, 用真客户端 2 迭代校准。
        public async Task Handle_REQUEST_PEER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (_sessions == null || body == null || body.Length < 3)
                return;
            ushort targetUid = System.BitConverter.ToUInt16(body, 0);
            byte reqType = body[2];
            int peerInt = body.Length >= 7 ? System.BitConverter.ToInt32(body, 3) : 0;
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            ushort inviterUid = (ushort)cid;
            FileLogger.Log($"[{ProtocolName}] REQUEST_PEER by={cid} targetUid={targetUid} type={reqType} peerInt={peerInt} body={System.BitConverter.ToString(body)}");

            var targetSession = FindSessionByUserId(targetUid);
            if (targetSession == null)
            {
                FileLogger.Log($"[{ProtocolName}] REQUEST_PEER: 目标 uid={targetUid} 不在线");
                return;
            }
            // ★交易 阶段1: reqType==1 = ENUM_PEER_REQUEST_TYPE TRADE → 给对方弹【交易确认窗】(而非组队框)。
            //   交易形态 body = 11B [A.uid:2][01][peer:4][createTime:4](含 peer, 漏了长度不符被客户端静默丢弃→不弹窗)。
            //   阶段2(放置道具窗/换物)待专项; 此处保证交易请求不弹成组队框, 且 accept 不误组队(见 RES_PEER)。
            if (reqType == 1)
            {
                var tw = new GamePacketWriter();
                tw.WriteUInt16(inviterUid);   // A.uid
                tw.WriteByte(1);              // ENUM_PEER_REQUEST_TYPE = 1 TRADE
                tw.WriteInt32(peerInt);       // peer(回传请求里的 peer)
                tw.WriteInt32(0);             // A.createTime(阶段1先填0)
                await targetSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0007, tw.ToArray()));
                FileLogger.Log($"[{ProtocolName}] TRADE REQUEST_PEER A={inviterUid}->B={targetUid} → SC 0x0007 交易形态(11B, ⚠️阶段2待实现)");
                return;
            }
            // 给目标发 REQUEST_PEER(0x0007) 弹邀请框。body 照 df type=7 包: 邀请者 uid + 结果类型0 + int + 3×u16。
            var w = new GamePacketWriter();
            w.WriteUInt16(inviterUid);   // 邀请者 uid(目标据此显示"谁邀请")
            w.WriteByte(0);              // 结果类型 0 = 请求
            w.WriteInt32(peerInt);       // 回传 peer id
            w.WriteUInt16(0);            // 疲劳(待真机校准)
            w.WriteUInt16(0);            // 体力
            w.WriteUInt16(0);
            await targetSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0007, w.ToArray()));
            FileLogger.Log($"[{ProtocolName}] REQUEST_PEER → 给 uid={targetUid} 发 REQUEST_PEER(0x0007) 邀请弹框");
        }

        // 0x000B RES_PEER: 被邀请者接受 REQUEST_PEER 邀请(真机抓包: body = 邀请者uid(u16) + 5B 尾)。
        // 接受 → 把接受者并入邀请者的队(邀请者=队长)+ 给全体广播 PARTY_INFO(0x09)+实时(0x99)。
        public async Task Handle_RES_PEER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (_sessions == null || body == null || body.Length < 2)
                return;
            ushort inviterUid = System.BitConverter.ToUInt16(body, 0);
            // ★body[2]=reqType(与 REQUEST_PEER 同域): 0=组队 1=交易。真机 ground truth:
            //   组队 accept body=EB-03-`00`-..., 交易 accept body=EB-03-`01`-...。
            //   之前无视 reqType 一律 party join → 交易【同意】被误组队(1v1交易窗点同意冒出组队 + 交易无后续)。
            byte reqType = body.Length >= 3 ? body[2] : (byte)0;
            var (acid, aaid) = SessionOwnerResolver.Resolve(session);
            ushort accepterUid = (ushort)acid;
            FileLogger.Log($"[{ProtocolName}] RES_PEER accept: accepter={accepterUid} inviter={inviterUid} type={reqType} body={System.BitConverter.ToString(body)}");

            if (inviterUid == accepterUid)
                return;
            var inviterSession = FindSessionByUserId(inviterUid);
            if (inviterSession == null)
            {
                FileLogger.Log($"[{ProtocolName}] RES_PEER: 邀请者 uid={inviterUid} 不在线");
                return;
            }

            // ★交易 accept(reqType==1)绝不组队。df 交易走独立 CTradeSpace 路径, 与 party join 无关。
            //   阶段2(开道具放置窗 + 换物)待专项; 此处止血: 交易同意不再误组队。
            if (reqType == 1)
            {
                FileLogger.Log($"[{ProtocolName}] RES_PEER TRADE accept: A={inviterUid} B={accepterUid} → 交易已确认(不组队); ⚠️阶段2放置窗/换物待实现");
                return;
            }
            var (icid, iaid) = SessionOwnerResolver.Resolve(inviterSession);

            var party = _partyManager.GetPartyByUser(inviterUid);
            if (party == null)
            {
                var createdResult = _partyManager.CreateParty(BuildMember(inviterSession, icid));
                party = createdResult.Party;
                await NotifyPriorPartyAsync(createdResult.PriorPartyLeave);
            }
            if (!party.Contains(accepterUid))
            {
                if (party.IsFull)
                {
                    FileLogger.Log($"[{ProtocolName}] RES_PEER: 队伍已满");
                    return;
                }
                var join = _partyManager.Join(party.PartyId, BuildMember(session, acid));
                if (!join.Ok)
                {
                    FileLogger.Log($"[{ProtocolName}] RES_PEER: 入队失败 {join.Reason}");
                    return;
                }
                await NotifyPriorPartyAsync(join.PriorPartyLeave);
                party = join.Party;
            }
            FileLogger.Log($"[{ProtocolName}] RES_PEER: 组队成功 party={party.PartyId} members={party.Count} leader={party.LeaderUserId}");

            // df 0x081F14D2: 接受成功后单发 SC 0x08 "peer已接受" 回执给【邀请者A】(body=B.uid + 0 + A.uid, 7B)。
            // 疑为让 A 客户端认领队伍单例(dword_3091F50), 使随后的 PARTY_INFO(0x09) 落到被渲染的那个队伍对象上。
            var w08 = new GamePacketWriter();
            w08.WriteUInt16(accepterUid);       // B.uid (responder)
            w08.WriteByte(0);
            w08.WriteInt32((int)inviterUid);    // A.uid (inviter, u32 LE)
            await inviterSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0008, w08.ToArray()));
            FileLogger.Log($"[{ProtocolName}] RES_PEER: 发 SC 0x08 接受回执 → 邀请者 uid={inviterUid} (B={accepterUid})");

            await BroadcastPartyInfo(party);
        }
        // 按 UserId 找在线会话。
        private EnhancedClientSession FindSessionByUserId(ushort uid)
        {
            foreach (var s in _sessions.GetAllGameSessions())
            {
                var (scid, _) = SessionOwnerResolver.Resolve(s);
                if (scid > 0 && (ushort)scid == uid)
                    return s;
            }
            return null;
        }

        // 向队伍全体在线成员广播整份 PARTY_INFO(0x09 type=0)+ 实时信息(0x99)+ P2P 端点(0x0B)。
        // includeP2p=false: 只发 0x09 名册刷新, 不重发 0x0B/0x99。委托队长用: 队伍已 P2P 连着,
        //   swap 槽位后再重发 0x0B 会触发客户端 P2P 重握手 → 崩溃/超时(真机实测 A 断连)。换队长只需名册刷新。
        private async Task BroadcastPartyInfo(Game.Party.Party party, bool includeP2p = true)
        {
            if (_sessions == null || party == null) return;
            var info0x09 = PartyInfoNotiBuilder.Build(party, 0);
            FileLogger.Log($"[{ProtocolName}] BroadcastPartyInfo party={party.PartyId} leader={party.LeaderUserId} includeP2p={includeP2p} members=[{string.Join(",", party.MembersBySlot().Select(m => $"uid{m.UserId}@slot{m.SlotIndex}"))}] recipients={party.MembersBySlot().Count} info0x09={System.BitConverter.ToString(info0x09)}");
            var infoPacket = GamePacketEnvelopeBuilder.Build(0x00, 0x0009, info0x09);
            var rtPacket = includeP2p ? GamePacketEnvelopeBuilder.Build(0x00, 0x0099, PartyRealtimeInfoBuilder.Build(party)) : null;
            // P2P 端点交换(SC 0x0B): 让队友互相 UDP 直连、清"连接中"。df 次序 0x99→0x0B→0x09。
            var ipPacket = includeP2p ? GamePacketEnvelopeBuilder.Build(0x00, 0x000B, PartyIpInfoBuilder.Build(party)) : null;
            if (includeP2p)
                FileLogger.Log($"[{ProtocolName}] PARTY_IP_INFO(0x0B) party={party.PartyId} body={System.BitConverter.ToString(PartyIpInfoBuilder.Build(party))}");
            foreach (var m in party.MembersBySlot())
            {
                _sessions.TryGet(m.CharacterId, out var s);
                if (s == null || !s.TcpClient.Connected) continue;
                if (includeP2p)
                {
                    await s.SendPacketAsync(rtPacket);    // df 次序: 先 realtime(0x99)
                    await s.SendPacketAsync(ipPacket);    // 再 P2P 端点(0x0B, 必须在 0x09 前)
                }
                await s.SendPacketAsync(infoPacket);  // 再 PARTY_INFO(0x09)
            }
        }

        // 在线会话里按"角色名字节"逐字节匹配目标(避开 GBK/字符串编码歧义)。
        private EnhancedClientSession FindSessionByCharacterName(byte[] nameBytes)
        {
            foreach (var s in _sessions.GetAllGameSessions())
            {
                var (scid, _) = SessionOwnerResolver.Resolve(s);
                if (scid <= 0) continue;
                var rec = _characterRepository.GetById(scid);
                if (rec?.Name != null && BytesEqual(rec.Name, nameBytes))
                    return s;
            }
            return null;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // 解析 [int32 len][len bytes] 的角色名参数(REQUEST_MEMBER_ENTER)。宽松容错。
        private static byte[] ParseNameArg(byte[] body)
        {
            if (body == null || body.Length < 4) return null;
            int len = System.BitConverter.ToInt32(body, 0);
            if (len < 0 || 4 + len > body.Length) return null;
            var name = new byte[len];
            System.Array.Copy(body, 4, name, 0, len);
            return name;
        }
    }
}
