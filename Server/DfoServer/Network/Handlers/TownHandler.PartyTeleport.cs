using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Party;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers;

/// <summary>
/// 这个部分类只和组队传送相关
/// </summary>
public sealed partial class TownHandler
{
    private sealed class TeleportState
    {
        // 全队传送状态
        public byte PartyState { get; set; }

        // 目标村庄
        public byte Village { get; set; }

        // 目标区域
        public byte AreaIndex { get; set; }

        // 位置
        public ushort PosX { get; set; }
        public ushort PosY { get; set; }
        public byte Direction { get; set; }

        // 各队员的状态
        public MemberTeleportState[] MemberState { get; set; } =
        [
            new MemberTeleportState { },
            new MemberTeleportState { },
            new MemberTeleportState { },
            new MemberTeleportState { }
        ];
    }

    private class MemberTeleportState
    {
        public short MemberId { get; set; } = -1;
        public byte State { get; set; } = 0;
    }

    private readonly ConditionalWeakTable<Party, TeleportState> _teleportState = new();


    /// <summary>
    /// 队长使用传送光环时触发
    /// </summary>
    /// <param name="session">session</param>
    /// <param name="header">packet header</param>
    /// <param name="body">...</param>
    public async Task Handle_ENUM_CMDPACKET_PARTY_TELEPORT(EnhancedClientSession session, GamePacketHeader header,
        byte[] body)
    {
        if (body is not { Length: 7 })
        {
            return;
        }

        byte villageId = body[0];
        byte areaIndex = body[1];
        ushort posX = BitConverter.ToUInt16(body, 2);
        ushort posY = BitConverter.ToUInt16(body, 4);
        byte direction = body[6];
        FileLogger.Log(
            $"组队传送：villageId = {villageId}, areaIndex = {areaIndex}, posX = {posX}, posY = {posY}, direction = {direction}");

        // TODO: 台服逆向这里检查了一大堆东西，但是我懒得写，如果你想写可以参考 `Dispatcher_PartyTeleport::process` 和 `Dispatcher_PartyTeleport::check_error`


        // assume all check is ok
        var cid = (ushort)session.Player.CharacterId;
        var party = _partyManager.GetPartyByUser(cid);

        if (party is null)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type,
                CommonPacketBodyBuilder.BuildCmdError(19)));
            return;
        }

        var state = _teleportState.GetOrCreateValue(party);
        state.PartyState = 0;
        state.Village = villageId;
        state.AreaIndex = areaIndex;
        state.PosX = posX;
        state.PosY = posY;
        state.Direction = direction;
        // state.MemberState = [2, 2, 2, 2];
        var me = party.GetMember(session.Player.UserId);
        state.MemberState[me.SlotIndex].MemberId = (short)me.UserId;
        state.MemberState[me.SlotIndex].State = 1;

        UpdatePartyState(state, party);
        // 如果队伍里只有一个人的话这时已经满足了传送条件


        // do sending
        // Send a success to current character
        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type,
            CommonPacketBodyBuilder.BuildSuccessAck()));

        // send effect
        await SendEffectMultiPlace(party, 3);

        // send teleport state
        await SendTeleportState(party, state);

        // check and process teleport
        await ProcessTeleport(session, party, state);
    }


    /// <summary>
    /// 组队传送时队伍成员多于1个时会向其他成员展示确认对话框，成员与对话框交互时会触发这个函数
    /// TODO: 因为组队功能暂时还不能用 所以这个函数也暂时未完成
    /// </summary>
    /// <param name="session"></param>
    /// <param name="header"></param>
    /// <param name="body"></param>
    public async Task Handle_ENUM_CMDPACKET_PARTY_TELEPORT_CONFIRM(EnhancedClientSession session,
        GamePacketHeader header,
        byte[] body)
    {
        FileLogger.Log("组队传送确认：{0}", BitConverter.ToString(body ?? []));
        var cid = (ushort)session.Player.CharacterId;
        var party = _partyManager.GetPartyByUser(cid);

        if (party is null)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type,
                CommonPacketBodyBuilder.BuildCmdError(19)));
            return;
        }

        if (!_teleportState.TryGetValue(party, out var state))
        {
            // this implies that current party not in teleport state

            return;
        }

        var self = party.GetMember(cid);
        var slotIdx = self.SlotIndex;

        FileLogger.Log("组队传送确认暂未完成");
    }

    /// <summary>
    /// 向队长和队员同一区域的玩家，发送展示传送特效的通知
    /// </summary>
    /// <param name="party">当前所属队伍</param>
    /// <param name="effect">特效号码 1 为角色身上向上升起的光点， 3 为脚下的传送阵</param>
    private async Task SendEffectMultiPlace(Party party, byte effect)
    {
        var groupedAreaCharacter = new Dictionary<(byte, byte), List<short>>();

        foreach (var member in party.Members)
        {
            _sessions.TryGet(member.CharacterId, out var session);

            var ids = groupedAreaCharacter.GetValueOrDefault((session.Player.CurAreaId, session.Player.CurTownId), []);
            ids.Add((short)member.UserId);
            groupedAreaCharacter.Add((session.Player.CurAreaId, session.Player.CurTownId), ids);
        }

        foreach (var keyValuePair in groupedAreaCharacter)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(effect);
            writer.WriteByte((byte)keyValuePair.Value.Count);
            foreach (var id in keyValuePair.Value)
            {
                writer.WriteInt16(id);
            }

            await _sessions.BroadcastToAreaAsync(keyValuePair.Key.Item2, keyValuePair.Key.Item1, -1,
                GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketType.SHOW_EFFECT, writer.ToArray()));
        }
    }

    /// <summary>
    /// 向当前同一区域的玩家，发送展示传送特效的通知
    /// </summary>
    /// <param name="session"></param>
    /// <param name="party">队伍</param>
    /// <param name="effect">特效代码</param>
    private async Task SendEffectCurPlace(EnhancedClientSession session, Party party, byte effect)
    {
        var ids = party.Members.Select(member => (short)member.UserId).ToList();
        var writer = new GamePacketWriter();
        writer.WriteByte(effect);
        writer.WriteByte((byte)ids.Count);
        foreach (var id in ids)
        {
            writer.WriteInt16(id);
        }

        await _sessions.BroadcastToAreaAsync(session.Player.CurTownId, session.Player.CurAreaId, -1,
            GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketType.SHOW_EFFECT, writer.ToArray()));
    }

    /// <summary>
    /// 给全队发送组队传送状态
    /// </summary>
    /// <param name="party">队伍信心</param>
    /// <param name="state">传送状态</param>
    private async Task SendTeleportState(Party party, TeleportState state)
    {
        var writer = new GamePacketWriter();
        writer.WriteByte(state.Village);
        writer.WriteByte(state.PartyState); // teleport state

        foreach (var member in state.MemberState)
        {
            writer.WriteInt16(member.MemberId);
            writer.WriteByte(member.State);
        }

        foreach (var member in party.Members)
        {
            _sessions.TryGet(member.CharacterId, out var session);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketType.PARTY_TELEPORT_STATUS,
                writer.ToArray()));
        }
    }

    /// <summary>
    /// 检查组队传送状态，如果可执行传送则执行传送
    /// </summary>
    /// <param name="session"></param>
    /// <param name="party"></param>
    /// <param name="state"></param>
    private async Task ProcessTeleport(EnhancedClientSession session, Party party, TeleportState state)
    {
        if (state.PartyState == 1) // all ready do teleport
        {
            await SendEffectMultiPlace(party, 1);
            foreach (var member in party.Members)
            {
                _sessions.TryGet(member.CharacterId, out var mSession);
                await MoveArea(mSession, state.Village, state.AreaIndex, state.PosX, state.PosY, state.Direction);
            }

            await SendEffectCurPlace(session, party, 2);
        }
    }

    /// <summary>
    /// 执行移动
    /// 代码借用自<see cref="Handle_ENUM_CMDPACKET_SET_USER_AREA">Handle_ENUM_CMDPACKET_SET_USER_AREA</see>
    /// </summary>
    /// <param name="session">被移动的人</param>
    /// <param name="townId">目标城镇</param>
    /// <param name="areaId">目标区域</param>
    /// <param name="posX">x坐标</param>
    /// <param name="posY">y坐标</param>
    /// <param name="direction">方向</param>
    private async Task MoveArea(EnhancedClientSession session, byte townId, byte areaId, ushort posX, ushort posY,
        byte direction)
    {
        // TODO: lots of checks

        session.Player.CurTownId = townId;
        session.Player.CurAreaId = areaId;
        session.Player.CurPosX = (short)posX;
        session.Player.CurPosY = (short)posY;
        session.Player.CurDirection = direction;
        session.Player.CurAreaState = 0x03;

        var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
            TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));

        await BroadcastAreaRosterAsync(session, selfSnapshot);

        PersistPosition(session, forceImmediate: true, source: "party_teleport");
    }

    /// <summary>
    /// 遍历队员传送状态，如果都同意则设置全队传送状态为1
    /// </summary>
    /// <param name="state">传送状态</param>
    /// <param name="party">队伍信息</param>
    private static void UpdatePartyState(TeleportState state, Party party)
    {
        var allReady = true;
        foreach (var member in party.Members)
        {
            var memberState = state.MemberState[member.SlotIndex];
            if (memberState.State != 4 && memberState.State != 1)
            {
                // 1 = 同意传送
                // 4 = 离队
                allReady = false;
            }
        }

        if (allReady)
            state.PartyState = 1;
    }
}
