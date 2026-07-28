using DfoServer.Game.Dungeon;
using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal static class DungeonCommandParser
    {
        internal static bool TryParse(
            ushort wireType,
            byte[] body,
            out DungeonCommand command,
            out string error)
        {
            command = null;
            error = string.Empty;

            switch (wireType)
            {
                case (ushort)CmdPacketType.SUMMON_MONSTER:
                    return TryParseSummonMonster(
                        wireType,
                        body,
                        out command,
                        out error);

                case (ushort)CmdPacketType.TIMER_MODIFY_INFO:
                    command = new TimerModifyInfoDungeonCommand(
                        wireType,
                        body);
                    return true;

                case (ushort)CmdPacketType.SEA_CHASE_MINI_GAME_RESULT:
                    if (body == null || body.Length < 4)
                    {
                        error =
                            "SEA_CHASE_MINI_GAME_RESULT requires 4 bytes, " +
                            $"got {body?.Length ?? 0}";
                        return false;
                    }

                    command = new SeaChaseResultDungeonCommand(
                        wireType,
                        BitConverter.ToInt32(body, 0));
                    return true;

                case (ushort)CmdPacketType.EVENT_NPC_DROP_ITEM_:
                    command = new NpcItemDropDungeonCommand(wireType, body);
                    return true;

                case (ushort)CmdPacketType.BREAK_TRAP_RESULT:
                    command = new BreakTrapResultDungeonCommand(wireType, body);
                    return true;

                case 0x013C:
                case 0x0270:
                    command = new SeaChaseObservedDungeonCommand(
                        wireType,
                        body);
                    return true;

                default:
                    error = $"unsupported dungeon command 0x{wireType:X4}";
                    return false;
            }
        }

        private static bool TryParseSummonMonster(
            ushort wireType,
            byte[] body,
            out DungeonCommand command,
            out string error)
        {
            command = null;
            error = string.Empty;
            if (body == null || body.Length < 19)
            {
                error =
                    $"SUMMON_MONSTER requires 19 bytes, got {body?.Length ?? 0}";
                return false;
            }

            var parsed = new SummonMonsterDungeonCommand(
                wireType,
                BitConverter.ToUInt16(body, 0),
                BitConverter.ToInt32(body, 2),
                BitConverter.ToInt32(body, 6),
                BitConverter.ToInt32(body, 10),
                BitConverter.ToUInt16(body, 14),
                BitConverter.ToUInt16(body, 16),
                body[18]);
            if (parsed.MonsterCode <= 0
                || parsed.StateId <= 0
                || parsed.MapId <= 0
                || parsed.MatchCount == 0)
            {
                error =
                    "SUMMON_MONSTER contains an invalid monster, state, " +
                    "map or match count";
                return false;
            }

            command = parsed;
            return true;
        }
    }
}
