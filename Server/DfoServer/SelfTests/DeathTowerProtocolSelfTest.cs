using System;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DeathTowerProtocolSelfTest
    {
        private const int TowerHastePotionItemId = 6518;
        private const int TowerColorlessCubeItemId = 6515;
        private const byte QuickSlotListType = 0x1D;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_PROTOCOL selftest ===");
            var failures = 0;

            using (var materialFixture = ProtocolFixture.Create(TowerColorlessCubeItemId))
            {
                var handler = new DeathTowerCoordinator();
                var handledPickup = handler.TryHandleGetItem(materialFixture.Session, 51)
                    .GetAwaiter().GetResult();
                var pickup = materialFixture.ReadPacket();
                var hasPickupUpdate = materialFixture.TryReadPacket(out var pickupUpdate);
                Check("0x002B tower material pickup sends slot 121 ACK and authoritative 0x000E",
                    handledPickup
                        && pickup.Command == 0x00
                        && pickup.Type == 0x0027
                        && pickup.Body.Length >= 17
                        && BitConverter.ToUInt16(pickup.Body, 14) == 121
                        && hasPickupUpdate
                        && pickupUpdate.Command == 0x00
                        && pickupUpdate.Type == 0x000E
                        && HasCommonUpdate(pickupUpdate.Body, 0, 121, TowerColorlessCubeItemId, 2),
                    ref failures);
            }

            using (var fixture = ProtocolFixture.Create())
            {
                var handler = new DeathTowerCoordinator();

                var handledPickup = handler.TryHandleGetItem(fixture.Session, 51)
                    .GetAwaiter().GetResult();
                var pickup = fixture.ReadPacket();
                var hasPickupUpdate = fixture.TryReadPacket(out var pickupUpdate);
                Check("0x002B tower pickup routes to 0x0027 with tower slot 3 and 0x000E",
                    handledPickup
                        && pickup.Command == 0x00
                        && pickup.Type == 0x0027
                        && pickup.Body.Length >= 17
                        && BitConverter.ToUInt16(pickup.Body, 0) == 51
                        && BitConverter.ToUInt16(pickup.Body, 14) == 3
                        && hasPickupUpdate
                        && pickupUpdate.Type == 0x000E
                        && HasCommonUpdate(pickupUpdate.Body, QuickSlotListType, 3, TowerHastePotionItemId, 2),
                    ref failures);

                var handledUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBody(3, TowerHastePotionItemId, 0x127))
                    .GetAwaiter().GetResult();
                CapturedPacket useAck = null;
                CapturedPacket useUpdate = null;
                var hasUseAck = handledUse && fixture.TryReadPacket(out useAck);
                var hasUseUpdate = hasUseAck && fixture.TryReadPacket(out useUpdate);
                Check("captured list 0x1D tower use sends echoed success ACK before authoritative 0x000E",
                    handledUse
                        && hasUseAck
                        && useAck.Command == 0x01
                        && useAck.Type == 0x002C
                        && useAck.Body.Length >= 4
                        && useAck.Body[0] == 1
                        && useAck.Body[3] == QuickSlotListType
                        && hasUseUpdate
                        && useUpdate.Command == 0x00
                        && useUpdate.Type == 0x000E
                        && HasCommonUpdate(useUpdate.Body, QuickSlotListType, 3, TowerHastePotionItemId, 1),
                    ref failures);

                var handledMove = handler.TryHandleMoveItem(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(3, 4, 1))
                    .GetAwaiter().GetResult();
                var moveAck = fixture.ReadPacket();
                var moveUpdate = fixture.ReadPacket();
                Check("0x0013 tower move sends success ACK and two-slot 0x000E",
                    handledMove
                        && moveAck.Command == 0x01
                        && moveAck.Type == 0x0013
                        && moveAck.Body.Length >= 1
                        && moveAck.Body[0] == 1
                        && moveUpdate.Command == 0x00
                        && moveUpdate.Type == 0x000E
                        && HasCommonUpdate(moveUpdate.Body, QuickSlotListType, 3, -1, 0)
                        && HasCommonUpdate(moveUpdate.Body, QuickSlotListType, 4, TowerHastePotionItemId, 1),
                    ref failures);

                var handledInvalidUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBody(4, 6515, 1))
                    .GetAwaiter().GetResult();
                var invalidAck = fixture.ReadPacket();
                Check("invalid tower use returns error and never falls through",
                    handledInvalidUse
                        && invalidAck.Command == 0x01
                        && invalidAck.Type == 0x002C
                        && invalidAck.Body.Length >= 1
                        && invalidAck.Body[0] == 0
                        && GetTowerItemCount(fixture.Tower, TowerHastePotionItemId) == 1,
                    ref failures);

                var handledSevenByteUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBodyWithoutItemId(4, 1))
                    .GetAwaiter().GetResult();
                var sevenByteAck = fixture.ReadPacket();
                var sevenByteUpdate = fixture.ReadPacket();
                Check("7-byte 0x002C derives the authoritative tower item from its slot",
                    handledSevenByteUse
                        && sevenByteAck.Command == 0x01
                        && sevenByteAck.Type == 0x002C
                        && sevenByteAck.Body[0] == 1
                        && sevenByteUpdate.Type == 0x000E
                        && HasCommonUpdate(sevenByteUpdate.Body, QuickSlotListType, 4, -1, 0)
                        && GetTowerItemCount(fixture.Tower, TowerHastePotionItemId) == 0,
                    ref failures);
            }

            using (var routingFixture = ProtocolFixture.Create())
            {
                var handler = new DeathTowerCoordinator();
                var petBody = BuildUseBody(3, TowerHastePotionItemId, 1);
                petBody[2] = 1;
                var handledPetList = handler.TryHandleUseStackable(
                    routingFixture.Session,
                    new GamePacketHeader(),
                    petBody).GetAwaiter().GetResult();
                Check("tower 0x002C leaves non-main inventory lists to later handlers",
                    !handledPetList
                        && GetTowerItemCount(routingFixture.Tower, TowerHastePotionItemId) == 0
                        && routingFixture.Tower.GroundItems.Count == 1,
                    ref failures);

                var petMoveBody = BuildMoveBody(3, 4, 1);
                petMoveBody[0] = 1;
                petMoveBody[11] = 1;
                var handledPetMove = handler.TryHandleMoveItem(
                    routingFixture.Session,
                    new GamePacketHeader(),
                    petMoveBody).GetAwaiter().GetResult();
                Check("tower 0x0013 leaves non-main inventory lists to later handlers",
                    !handledPetMove
                        && routingFixture.Tower.GroundItems.Count == 1,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildUseBody(short slot, int itemId, int instanceValue)
        {
            var body = new byte[15];
            BitConverter.GetBytes(slot).CopyTo(body, 0);
            body[2] = QuickSlotListType;
            BitConverter.GetBytes(instanceValue).CopyTo(body, 3);
            BitConverter.GetBytes(itemId).CopyTo(body, 7);
            return body;
        }

        private static int GetTowerItemCount(DeathTowerSession tower, int itemId)
        {
            var snapshot = tower.GetItemCountsSnapshot();
            return snapshot.TryGetValue(itemId, out var count) ? count : 0;
        }

        private static byte[] BuildMoveBody(short source, short destination, int count)
        {
            var body = new byte[14];
            body[0] = QuickSlotListType;
            BitConverter.GetBytes(source).CopyTo(body, 1);
            BitConverter.GetBytes(count).CopyTo(body, 3);
            BitConverter.GetBytes(count).CopyTo(body, 7);
            body[11] = QuickSlotListType;
            BitConverter.GetBytes(destination).CopyTo(body, 12);
            return body;
        }

        private static byte[] BuildUseBodyWithoutItemId(short slot, int instanceValue)
        {
            var body = new byte[7];
            BitConverter.GetBytes(slot).CopyTo(body, 0);
            body[2] = QuickSlotListType;
            BitConverter.GetBytes(instanceValue).CopyTo(body, 3);
            return body;
        }

        private static bool HasCommonUpdate(
            byte[] body,
            byte wantedItemSpace,
            short wantedSlot,
            int wantedItemId,
            int wantedCount)
        {
            if (body == null || body.Length < 3 || body[0] != wantedItemSpace)
                return false;
            var count = BitConverter.ToUInt16(body, 1);
            for (var index = 0; index < count; index++)
            {
                var offset = 3 + index * 84;
                if (offset + 10 > body.Length)
                    return false;
                if (BitConverter.ToInt16(body, offset) == wantedSlot
                    && BitConverter.ToInt32(body, offset + 2) == wantedItemId)
                {
                    return wantedItemId < 0
                        || BitConverter.ToInt32(body, offset + 6) == wantedCount;
                }
            }
            return false;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class ProtocolFixture : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _accepted;

            private ProtocolFixture(
                TcpListener listener,
                TcpClient client,
                TcpClient accepted,
                EnhancedClientSession session,
                DeathTowerSession tower)
            {
                _listener = listener;
                _client = client;
                _accepted = accepted;
                Session = session;
                Tower = tower;
            }

            public EnhancedClientSession Session { get; }
            public DeathTowerSession Tower { get; }

            public static ProtocolFixture Create(int itemId = TowerHastePotionItemId)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var accepted = listener.AcceptTcpClient();
                connectTask.GetAwaiter().GetResult();
                client.ReceiveTimeout = 2000;

                var session = new EnhancedClientSession(accepted, new GamePacketHeader());
                session.Player.CharacterId = 990002;
                session.Player.UserId = 88;

                var tower = new DeathTowerSession(
                    DeathTowerSelfTestFactory.CreateConfig(
                        11000,
                        new[] { 1 },
                        50));
                tower.BeginStage(0x12345678, new[]
                {
                    new StageTowerItem
                    {
                        SourceListIndex = 1,
                        SourceMonsterUniqueId = 41,
                        ItemUniqueId = 51,
                        ItemId = itemId,
                        DropRate = 10000,
                        StackCount = 2,
                    },
                });
                tower.GenerateDropsForMonster(41);
                session.Player.CurrentRun = new DungeonRun(11000, 0) { Tower = tower };
                return new ProtocolFixture(listener, client, accepted, session, tower);
            }

            public CapturedPacket ReadPacket()
            {
                var header = ReadExact(15);
                var length = BitConverter.ToInt32(header, 3);
                return new CapturedPacket
                {
                    Command = header[0],
                    Type = BitConverter.ToUInt16(header, 1),
                    Body = length > 15 ? ReadExact(length - 15) : Array.Empty<byte>(),
                };
            }

            public bool TryReadPacket(out CapturedPacket packet)
            {
                packet = null;
                if (!_client.Client.Poll(100000, SelectMode.SelectRead) || _client.Available == 0)
                    return false;
                packet = ReadPacket();
                return true;
            }

            public void Dispose()
            {
                _accepted.Dispose();
                _client.Dispose();
                _listener.Stop();
            }

            private byte[] ReadExact(int count)
            {
                var result = new byte[count];
                var offset = 0;
                var stream = _client.GetStream();
                while (offset < count)
                {
                    var read = stream.Read(result, offset, count - offset);
                    if (read <= 0)
                        throw new InvalidOperationException("connection closed before packet completed");
                    offset += read;
                }
                return result;
            }
        }

        private sealed class CapturedPacket
        {
            public byte Command { get; set; }
            public ushort Type { get; set; }
            public byte[] Body { get; set; }
        }
    }
}
