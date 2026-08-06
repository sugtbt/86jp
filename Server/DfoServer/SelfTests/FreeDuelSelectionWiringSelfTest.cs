using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// Free-duel regression for the production SELECT_CHARACTER dispatch.
    /// The handler must finish the CH.68 lobby handshake before accepting
    /// MAKE_PVP_ROOM; testing the room registry alone cannot catch a missing
    /// HandleLobbyReadyAsync call in GameProtocolHandler.
    /// </summary>
    public static class FreeDuelSelectionWiringSelfTest
    {
        private const int CharacterId = 62001;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "free_duel_selection_wiring_" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath =
                Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
            var previousFreeDuelEnvironment =
                Environment.GetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable);
            var previousFreeDuelEnabled =
                GameNetworkConfig.FreeDuelListenerEnabled;
            SessionDirectory sessions = null;
            ConnectedSession connection = null;
            GameProtocolHandler protocol = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    "1");
                GameNetworkConfig.Configure(Array.Empty<string>());

                var accounts = new SqliteAccountRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var characters = new SqliteCharacterRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var accountId = accounts.Create(
                    "free-duel-selection-wiring",
                    string.Empty);
                characters.Create(
                    new CharacterRecord
                    {
                        CharacterId = CharacterId,
                        AccountId = accountId,
                        Name = Encoding.UTF8.GetBytes("PvpWiring"),
                        Job = 0,
                        GrowType = 0,
                        Level = 1,
                        TownId = 1,
                        AreaId = 0,
                        Direction = 5,
                        AreaState = 3,
                        Appearance = Array.Empty<
                            CharacterAppearanceEntry>()
                    });

                sessions = new SessionDirectory();
                protocol = new GameProtocolHandler(sessions);
                connection = ConnectedSession.Create(
                    GameNetworkConfig.FreeDuelGamePort);
                connection.Session.Account =
                    accounts.GetById(accountId);

                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(0x0004),
                        new byte[] { 0x00, 0x00 })
                    .GetAwaiter()
                    .GetResult();
                var selectionPackets = connection.DrainPackets();
                var lobbySnapshotSent = selectionPackets.Any(
                    packet =>
                        packet.Command == 0x00 &&
                        packet.Type ==
                            PvpRoomHandler.RoomInfoNotificationType);
                Check(
                    "CH.68 SELECT_CHARACTER production dispatch sends " +
                    "the initial PVP_ROOM_INFO snapshot",
                    lobbySnapshotSent,
                    ref failures);

                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(
                            PvpRoomHandler.MakeRoomCommandType),
                        new byte[]
                        {
                            0x06, 0x00, 0x00, 0x00, 0x00
                        })
                    .GetAwaiter()
                    .GetResult();
                var makeRoomPackets = connection.DrainPackets();
                var roomCreated =
                    connection.Session.Player.UserState ==
                        PvpRoomHandler.PvpUserState &&
                    makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x00 &&
                            packet.Type ==
                                PvpRoomHandler
                                    .RoomInfoNotificationType) &&
                    makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x00 &&
                            packet.Type ==
                                PvpRoomHandler
                                    .UserAreaNotificationType) &&
                    !makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x01 &&
                            packet.Type ==
                                PvpRoomHandler.MakeRoomCommandType);
                Check(
                    "MAKE_PVP_ROOM succeeds after the production " +
                    "selection-to-lobby handshake",
                    roomCreated,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] free-duel selection wiring threw: " + ex);
                failures++;
            }
            finally
            {
                if (sessions != null && connection != null)
                {
                    try
                    {
                        sessions.UnregisterAsync(
                                CharacterId,
                                connection.Session)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch
                    {
                    }
                    InventoryContext.Unregister(
                        connection.Session.SessionId,
                        CharacterId);
                }

                protocol?.Dispose();
                connection?.Dispose();
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);

                // Restore the in-process gate exactly, without letting a
                // pre-existing environment value change the saved state.
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    "0");
                GameNetworkConfig.Configure(
                    previousFreeDuelEnabled
                        ? new[]
                        {
                            "--free-duel-channel-listener"
                        }
                        : Array.Empty<string>());
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    previousFreeDuelEnvironment);
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "FreeDuelSelectionWiringSelfTest OK"
                    : "FreeDuelSelectionWiringSelfTest FAIL (" +
                      failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static GamePacketHeader CommandHeader(ushort type)
        {
            return new GamePacketHeader
            {
                cmd = 0x01,
                type = type,
                length = 15
            };
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " +
                label);
            if (!condition)
                failures++;
        }

        private sealed class CapturedPacket
        {
            internal byte Command { get; set; }
            internal ushort Type { get; set; }
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _peer;
            private readonly TcpClient _server;

            private ConnectedSession(
                TcpListener listener,
                TcpClient peer,
                TcpClient server,
                EnhancedClientSession session)
            {
                _listener = listener;
                _peer = peer;
                _server = server;
                Session = session;
            }

            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(int listenerPort)
            {
                var listener = new TcpListener(
                    IPAddress.Loopback,
                    0);
                listener.Start();
                var port =
                    ((IPEndPoint)listener.LocalEndpoint).Port;
                var peer = new TcpClient
                {
                    ReceiveBufferSize = 1024 * 1024
                };
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback,
                    port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                server.SendBufferSize = 1024 * 1024;
                return new ConnectedSession(
                    listener,
                    peer,
                    server,
                    new EnhancedClientSession(
                        server,
                        new GamePacketHeader(),
                        listenerPort));
            }

            internal IReadOnlyList<CapturedPacket> DrainPackets()
            {
                var bytes = new List<byte>();
                var quiet = Stopwatch.StartNew();
                while (quiet.Elapsed < TimeSpan.FromMilliseconds(50))
                {
                    var available = _peer.Available;
                    if (available <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var chunk = new byte[available];
                    var offset = 0;
                    while (offset < chunk.Length)
                    {
                        var read = _peer.GetStream().Read(
                            chunk,
                            offset,
                            chunk.Length - offset);
                        if (read <= 0)
                            throw new EndOfStreamException(
                                "free-duel self-test socket closed");
                        offset += read;
                    }
                    bytes.AddRange(chunk);
                    quiet.Restart();
                }

                var packets = new List<CapturedPacket>();
                var packetOffset = 0;
                while (packetOffset + 15 <= bytes.Count)
                {
                    var data = bytes.ToArray();
                    var length = BitConverter.ToInt32(
                        data,
                        packetOffset + 3);
                    if (length < 15 ||
                        packetOffset + length > data.Length)
                    {
                        throw new InvalidDataException(
                            "truncated game packet in free-duel " +
                            "selection self-test");
                    }
                    packets.Add(
                        new CapturedPacket
                        {
                            Command = data[packetOffset],
                            Type = BitConverter.ToUInt16(
                                data,
                                packetOffset + 1)
                        });
                    packetOffset += length;
                }

                if (packetOffset != bytes.Count)
                {
                    throw new InvalidDataException(
                        "trailing game packet bytes in free-duel " +
                        "selection self-test");
                }
                return packets;
            }

            public void Dispose()
            {
                _server.Dispose();
                _peer.Dispose();
                _listener.Stop();
            }
        }
    }
}
