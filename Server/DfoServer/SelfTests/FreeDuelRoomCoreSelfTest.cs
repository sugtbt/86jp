using System;
using DfoServer.Game.Pvp;
using DfoServer.Network;
using DfoServer.Network.Parsers.Pvp;

namespace DfoServer.SelfTests
{
    public static class FreeDuelRoomCoreSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            var parsedMake = MakePvpRoomRequest.TryParse(
                new byte[] { 0x06, 0x00, 0x00, 0x00, 0x00 },
                out var makeRequest,
                out _);
            var registry = new FreeDuelRoomRegistry();
            var ownerSession = Guid.NewGuid();
            FreeDuelRoom room = null;
            byte createError = byte.MaxValue;
            var created = parsedMake &&
                registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5001,
                    ownerSession,
                    5001,
                    makeRequest,
                    out room,
                    out createError);
            Check(
                "public room request creates the first legacy room id",
                created && createError == 0 && room.RoomId == 0,
                ref failures);

            var parsedEnter = EnterPvpRoomRequest.TryParse(
                new byte[] { 0x00, 0x00, 0x00 },
                out var enterRequest,
                out _);
            var memberSession = Guid.NewGuid();
            byte memberSeat = byte.MaxValue;
            byte joinError = byte.MaxValue;
            var joined = parsedEnter &&
                registry.TryJoin(
                    GameNetworkConfig.FreeDuelGamePort,
                    5002,
                    memberSession,
                    5002,
                    enterRequest,
                    out room,
                    out memberSeat,
                    out joinError);
            Check(
                "second player joins a deterministic open seat",
                joined && joinError == 0 && memberSeat == 1 &&
                room.NonObserverPlayerCount == 2,
                ref failures);

            var observerChanged = registry.TrySetSeatState(
                5002,
                memberSession,
                memberSeat,
                FreeDuelRoom.AlternateObserverSeatState,
                out room,
                out var observerError);
            Check(
                "alternate observer state is preserved and excluded from combat count",
                observerChanged && observerError == 0 &&
                room.IsObserverSeat(memberSeat) &&
                room.GetSeatState(memberSeat) ==
                    FreeDuelRoom.AlternateObserverSeatState &&
                room.NonObserverPlayerCount == 1,
                ref failures);

            var ownerChangedMode = registry.TrySetBattleMode(
                5001,
                ownerSession,
                6,
                out room,
                out var modeError);
            var memberCannotChangeMode = !registry.TrySetBattleMode(
                5002,
                memberSession,
                2,
                out _,
                out var memberModeError);
            Check(
                "only the exact owner session can change battle mode",
                ownerChangedMode && modeError == 0 && room.BattleMode == 6 &&
                memberCannotChangeMode && memberModeError == 8,
                ref failures);

            var removed = registry.TryTakeOwnedRoomForRemoval(
                5001,
                ownerSession,
                out var retired);
            var released = removed && registry.ReleaseRemovedRoomId(retired);
            FreeDuelRoom recycledRoom = null;
            var recycled = released &&
                registry.TryCreate(
                    GameNetworkConfig.FreeDuelGamePort,
                    5010,
                    Guid.NewGuid(),
                    5010,
                    makeRequest,
                    out recycledRoom,
                    out _);
            Check(
                "retired room ids are recycled only after explicit release",
                recycled && recycledRoom.RoomId == 0,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "FreeDuelRoomCoreSelfTest OK"
                    : $"FreeDuelRoomCoreSelfTest FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition)
                failures++;
        }
    }
}
