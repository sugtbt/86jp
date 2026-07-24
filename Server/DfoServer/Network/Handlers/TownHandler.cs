using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class TownHandler
    {
        private static readonly TimeSpan PositionPersistThrottle = TimeSpan.FromSeconds(5);

        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly Game.SelectCharacter.SqliteSelectCharacterDataSource _selectDataSource;
        private readonly Game.Party.PartyManager _partyManager;   // 可空: 副本退出/回城时把队员一起拉回城(跟随退出)
        // 可空: 会话目录(charId→session)。同屏区域查询与队员定位共用这一份注册表, 不另设区域广播器。
        private readonly Game.Session.ISessionDirectory _sessions;

        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public TownHandler(ICharacterRepository characterRepository, Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource = null, Game.Party.PartyManager partyManager = null, Game.Session.ISessionDirectory sessions = null, InventoryRefreshSender refresh = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _honorLevel = new HonorLevelSyncService(_characterRepository);
            _growthCapsule = new GrowthCapsuleSyncService(_characterRepository);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(
                Infrastructure.ServerPaths.DatabasePath,
                Infrastructure.ServerPaths.SchemaFilePath);
            _refresh = refresh;
            _selectDataSource = selectDataSource;  // 可空: 用于同屏推送他人完整 USERINFO(subtype1, 让客户端认其可组队邀请)
            _partyManager = partyManager;          // 可空: 组队副本收尾 fan-out(跟随退出); 与副本共享同一 PartyManager
            _sessions = sessions;                  // 可空: 未注入时退化为单人(不广播)
        }

        // 构建某在线会话玩家的【完整 USERINFO subtype1】(0x0002 occ1, ~1458B: 属性/装备/技能)。
        // 同屏时仅推 subtype0(精简外观)客户端能渲染但判定"对方不在城镇/不可邀请"; self 进游戏收的是 subtype0+subtype1
        // 两份, 故给同屏他人补 subtype1。id 头(bytes 3-4)由 CharacterId 改写为 UserId 以对齐城镇名册。
        private byte[] BuildFullUserInfoPacket(EnhancedClientSession s)
        {
            if (_selectDataSource == null || s?.Player == null || s.Player.CharacterId <= 0)
                return null;
            try
            {
                var snap = _selectDataSource.Load(s.Player.CharacterId, s.Account?.AccountId ?? 1);
                if (snap?.CharacterRecord == null || snap.InitializationSnapshot?.UserInfoAddition == null)
                    return null;
                if (!new Network.Builders.UserInfoBodyBuilder().TryBuild(snap, 1, out var fullBody) || fullBody == null || fullBody.Length < 5)
                    return null;
                BitConverter.GetBytes(s.Player.UserId).CopyTo(fullBody, 3);
                return GamePacketEnvelopeBuilder.Build(0x00, 0x0002, fullBody);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] BuildFullUserInfoPacket cid={s.Player.CharacterId} 失败: {ex.Message}");
                return null;
            }
        }

        public void PersistPosition(EnhancedClientSession session, bool forceImmediate, string source)
        {
            try
            {
                if (session?.Player == null || session.Player.CharacterId <= 0)
                    return;

                var now = DateTime.UtcNow;
                if (!forceImmediate)
                {
                    if (now - session.Player.LastPositionPersistAt < PositionPersistThrottle)
                        return;
                }

                var gate = GameWorld.Town.GetCeraRoomInfo(session.Player.CurTownId);
                if (gate.Town <= 0)
                    return;

                _characterRepository.UpdatePosition(
                    session.Player.CharacterId,
                    session.Player.CurTownId,
                    session.Player.CurAreaId,
                    session.Player.CurPosX,
                    session.Player.CurPosY,
                    session.Player.CurDirection,
                    session.Player.CurAreaState);
                session.Player.LastPositionPersistAt = now;
                FileLogger.Log($"[{ProtocolName}] Persisted position ({source}) character_id={session.Player.CharacterId} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Persist position ({source}) failed: {ex.Message}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_POSITION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            var gotoPosX = BitConverter.ToInt16(body, 0);
            var gotoPosY = BitConverter.ToInt16(body, 2);
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            PersistPosition(session, forceImmediate: false, source: "set_user_position");

            // 联机同屏: 把移动广播给同区域其它玩家(USER_POSITION 0x0016)。
            if (_sessions != null && session.Player.CharacterId > 0)
            {
                var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                await _sessions.BroadcastToAreaAsync(
                    session.Player.CurTownId, session.Player.CurAreaId, session.Player.CharacterId,
                    GamePacketEnvelopeBuilder.Build(0x00, 0x0016, TownAreaNotificationBuilder.BuildUserPosition(snap)));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_AREA(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6) return;
            var gotoTownId = body[0];
            var gotoAreaId = body[1];
            var gotoPosX = BitConverter.ToInt16(body, 2);
            var gotoPosY = BitConverter.ToInt16(body, 4);

            session.Player.CurTownId = gotoTownId;
            session.Player.CurAreaId = gotoAreaId;
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = 0x05;
            session.Player.CurAreaState = 0x03;

            var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));

            // 联机同屏: 名册含同区域其它玩家, 并让已在场玩家看到新来的自己。
            await BroadcastAreaRosterAsync(session, selfSnapshot);

            PersistPosition(session, forceImmediate: true, source: "set_user_area");
        }

        // 同屏"插入他人"包的构造。脱壳客户端逆向确认(2026-07-06夜):右键组队邀请要求
        // 目标客户端对象 vtable[+40] 返回的 type==4(sub_118C100=sub_118C080==4),否则报字符串311
        // "对方不在城镇内"、连 REQUEST_PEER(0x000A) 都不发。df insert_user: 城镇分支(area[+0x68]==1)
        // 发 0x0018、野外/副本分支发 0x0017 —— 0x17/0x18 编码"对象在野外 vs 城镇"。当前用 0x0017(野外)
        // 插同屏他人 → 疑客户端建成野外对象(type≠4)→ 不可邀请。
        // env DFO_COPRESENCE_TOWN_INSERT 三档(晨间 A/B, 一份 build 全支持):
        //   0/未设(默认)= 只 0x0017(野外, 保持既有已工作的渲染, 不回归)
        //   1 = 只 0x0018(城镇分支 count=1; 试 type→4 可邀请, 但能否触发渲染他人对象未验)
        //   2 = both(先 0x0017 渲染 + 再 0x0018 城镇登记; 最稳: 保渲染又补城镇类型)
        private static readonly int _coPresenceMode =
            int.TryParse(System.Environment.GetEnvironmentVariable("DFO_COPRESENCE_TOWN_INSERT"), out var m) ? m : 0;

        private static byte[][] BuildCoPresenceInserts(TownUserSnapshot snap)
        {
            var f0017 = GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snap));
            var f0018 = GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snap.TownId, snap.AreaId, new[] { snap }));
            switch (_coPresenceMode)
            {
                case 1: return new[] { f0018 };          // 城镇 0x0018 only
                case 2: return new[] { f0017, f0018 };   // both
                default: return new[] { f0017 };         // 默认 0x0017
            }
        }

        /// <summary>
        /// 城镇同屏核心: 收集同区域全部会话, 给每个人下发含全体的 AREA_USERS(0x0018)。
        /// _sessions 为空(单人/未注入)时退化为只发自己 —— 与既有单机行为等价。
        /// </summary>
        private async Task BroadcastAreaRosterAsync(EnhancedClientSession session, TownUserSnapshot selfSnapshot)
        {
            var townId = session.Player.CurTownId;
            var areaId = session.Player.CurAreaId;

            IReadOnlyList<EnhancedClientSession> others = _sessions?.GetSessionsInArea(townId, areaId, session.Player.CharacterId)
                ?? System.Array.Empty<EnhancedClientSession>();

            FileLogger.Log($"[{ProtocolName}] AREA co-presence: uid={session.Player.UserId} town={townId} area={areaId} others={others.Count}");

            // 全体名册(自己 + 其它人)。
            var roster = new List<TownUserSnapshot>(others.Count + 1) { selfSnapshot };
            foreach (var o in others)
                roster.Add(TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player));

            // 真机实测(逆向+抓包结论): 只发 0x17/0x18 客户端既不生成他人角色对象、也不主动拉外观。
            // self 能渲染是因为进游戏时收了【完整外观】(USERINFO 0x0002 含形象)。故照"自身入场先有外观后有位置"
            // 主动 PUSH: 给新人为【每个已在场玩家】先推一份 USERINFO(0x0002 外观)、再发 0x0017(定位/生成), 最后补 0x0018 名册。
            // 给新人: 每个已在场玩家 subtype0(精简外观, 生成对象)+ subtype1(完整属性/装备/技能, 让客户端认其可组队邀请)+ 0x0017 定位。
            foreach (var o in others)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002,
                    Game.Appearance.AppearanceService.BuildNoti2Body(o.Player)));
                var oFull = BuildFullUserInfoPacket(o);
                if (oFull != null) await session.SendPacketAsync(oFull);
                var oSnap = TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player);
                foreach (var pkt in BuildCoPresenceInserts(oSnap))
                    await session.SendPacketAsync(pkt);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(townId, areaId, roster)));

            // 给每个已在场玩家推【新人】的 subtype0 + subtype1 + 0x0017(insert), 让他们生成并认可新人。
            var selfAppearance = GamePacketEnvelopeBuilder.Build(0x00, 0x0002, Game.Appearance.AppearanceService.BuildNoti2Body(session.Player));
            var selfFull = BuildFullUserInfoPacket(session);
            var selfAreas = BuildCoPresenceInserts(selfSnapshot);
            foreach (var o in others)
            {
                await o.SendPacketAsync(selfAppearance);
                if (selfFull != null) await o.SendPacketAsync(selfFull);
                foreach (var pkt in selfAreas)
                    await o.SendPacketAsync(pkt);
            }
        }

        /// <summary>联机同屏: 断线/离开区域时通知同区域其它玩家移除该分身(USER_LEAVE 0x0006)。</summary>
        public async Task NotifyLeaveAsync(EnhancedClientSession session)
        {
            if (_sessions == null || session?.Player == null || session.Player.CharacterId <= 0)
                return;
            await _sessions.BroadcastToAreaAsync(
                session.Player.CurTownId, session.Player.CurAreaId, session.Player.CharacterId,
                GamePacketEnvelopeBuilder.Build(0x00, 0x0006, TownAreaNotificationBuilder.BuildUserLeave(session.Player.UserId)));
        }

        public async Task Handle_ENUM_CMDPACKET_FINISH_LOADING(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0025, CommonPacketBodyBuilder.BuildSuccessAck()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, FinishLoadingBuilder.BuildNotification()));
            await _growthCapsule.SendExpProgressAsync(session, "finish-loading");
        }

        public async Task Handle_ENUM_CMDPACKET_TELEPORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 8)
                return;

            var type = BitConverter.ToInt16(body, 0);
            var itemCode = BitConverter.ToInt32(body, 2);
            if (itemCode != 0x0027AC4E)
                return;

            var townId = body[7];
            var ceraRoomInfo = Town.GetCeraRoomInfo(townId);
            session.Player.CurTownId = ceraRoomInfo.Town;
            session.Player.CurAreaId = ceraRoomInfo.Area;
            session.Player.CurPosX = ceraRoomInfo.X;
            session.Player.CurPosY = ceraRoomInfo.Y;
            session.Player.CurDirection = 0;
            session.Player.CurAreaState = 3;

            var (cid, _) = InventoryHandler.ResolveOwner(session);
            int remainingCount = 0;
            short targetSlot = -1;
            if (InventoryContext.TryGetLease(cid, out var lease) && lease.IsOwnedBy(session.SessionId))
            {
                lock (lease.SyncRoot)
                {
                    if (lease.Inventory.TryConsumeMainItem(itemCode, 1, out var consumeResult) && consumeResult.Success)
                    {
                        targetSlot = consumeResult.SlotIndex;
                        remainingCount = consumeResult.RemainingCount;
                        FileLogger.Log($"[{ProtocolName}] TELEPORT: consumed 1x teleport item slot={targetSlot} remaining={remainingCount}");
                    }
                }

                if (targetSlot < 0)
                    remainingCount = lease.Inventory.CountMainItem(itemCode);
            }
            else
            {
                FileLogger.Log($"[{ProtocolName}] TELEPORT: online inventory missing cid={cid}");
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018, TownAreaNotificationBuilder.BuildAreaUsers(TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player))));
            if (targetSlot >= 0 && _refresh != null)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, targetSlot);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00ED, TeleportPacketBuilder.BuildTeleportResponse(type, itemCode)));

            PersistPosition(session, forceImmediate: true, source: "teleport");
        }

        public async Task Handle_ENUM_CMDPACKET_GIVEUP_GAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await ReturnSelfToTownAsync(session, header);
            await SendTownAccountStateAsync(session, "giveup-game");
            // ★跟随退出(item17)只在【通关回城 BACK_2_VILLAGE 0x84】触发: 副本结束队长回城 → 队员跟随。
            //   ⚠️【放弃 GIVEUP_GAME 0x2A = 未完成中途退出】绝不 fan-out:
            //     放弃者独自回城、【留队】; 其余队员【继续留在副本、留队】(真机确认的正确语义)。
            //   0x2A/0x84 同路由到本 handler, 靠 header.type 区分。
            if (header.type == 0x0084)
                await TryFanOutLeaderReturnToTownAsync(session, header);
            else
                FileLogger.Log($"[{ProtocolName}] GIVEUP_GAME(type=0x{header.type:X2}): 未完成放弃退出, cid={session.Player?.CharacterId} 独自回城留队, 不拉队员(其余留本)");
        }

        // 把【单个会话】自己拉回城镇(EndRun + 城镇区域同步)。队长/队员复用同一序列。
        private async Task ReturnSelfToTownAsync(EnhancedClientSession session, GamePacketHeader header)
        {
            await Dungeon.DungeonRunLifecycle.EndRunToTownAsync(session);
            session.Player.UserState = 0x00;

            var list = new List<byte>();
            list.Add(session.Player.CurTownId);
            list.Add(session.Player.CurAreaId);
            list.AddRange(BitConverter.GetBytes(session.Player.CurPosX));
            list.AddRange(BitConverter.GetBytes(session.Player.CurPosY));
            list.Add(session.Player.CurDirection);
            list.Add(session.Player.CurTownId);
            list.Add(session.Player.CurAreaState);
            list.Add(session.Player.CurAreaId);
            await Handle_ENUM_CMDPACKET_SET_USER_AREA(session, header, list.ToArray());
        }

        // ★组队副本收尾 fan-out(⚠️协议/渲染, 待真机)。仅当【队长】+开 DFO_PARTY_DUNGEON_COOP + 队伍>1:
        //   把每个仍在副本内(CurrentRun!=null)的在线队员也拉回其城镇 → 客户端呈现"跟着队长退出"。
        //   非队长放弃(item16 个人退出)不 fan-out, 只回自己, 其余人继续留本。
        private async Task TryFanOutLeaderReturnToTownAsync(EnhancedClientSession leader, GamePacketHeader header)
        {
            if (Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            if (_partyManager == null || _sessions == null || leader?.Player == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = _partyManager.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} → fan-out 跟随退出");
            foreach (var m in party.MembersBySlot())
            {
                if (m.UserId == leaderUid) continue;
                _sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected) continue;
                if (bs.Player.CurrentRun == null) continue;   // 已在城镇, 不重复拉
                try
                {
                    await ReturnSelfToTownAsync(bs, header);
                    await SendTownAccountStateAsync(bs, "party-return-village");
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member cid={bs.Player.CharacterId} 跟随退出→城镇");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member uid={m.UserId} 跟随异常: {ex.Message}");
                }
            }
        }

        private async Task SendTownAccountStateAsync(EnhancedClientSession session, string reason)
        {
            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0 || characterId <= 0)
                return;

            var summary = _honorLevel.LoadSummary(accountId);
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0",
                summary);

            await _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
        }
    }
}
