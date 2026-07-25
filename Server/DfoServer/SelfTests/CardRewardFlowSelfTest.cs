using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 翻牌奖励幂等自测:
    // 自动翻、手动翻、EPLP/再次挑战会竞争同一局 CardRewards, 每一段奖励只能发一次。
    public static class CardRewardFlowSelfTest
    {
        private const int AccountId = 970016;
        private const int CharacterId = 970116;

        public static int Run()
        {
            Console.WriteLine("=== CARD_REWARD_FLOW selftest ===");
            var failures = 0;

            var tempDb = Path.Combine(Path.GetTempPath(), "card-reward-flow.db");
            DeleteTempDatabase(tempDb);
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            Seed(connStr);
            var service = new CardRewardService();

            using (var peer = ConnectedSession.Create())
            {
                var session = peer.Session;
                session.Player.CharacterId = CharacterId;
                session.Account = new AccountRecord { AccountId = AccountId };
                var inventory = new InventoryService(CharacterId, AccountId);
                InventoryContext.Register(session.SessionId, CharacterId, inventory);

                try
                {
                    var run = BuildRun(freeGold: 10, paidGold: 20);
                    session.Player.CurrentRun = run;

                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGold("free card grants once", inventory, 10, ref failures);
                    CheckGoldUpdatePacket("free card sends gold refresh packet", peer, 10, ref failures);
                    Check("free flag set, paid still pending",
                        run.FreeCardRewardDelivered && !run.PaidCardRewardDelivered && run.CardRewards != null,
                        ref failures);

                    var shouldReturn = service.HandleEplpCommand(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    Check("EPLP requests return", shouldReturn, ref failures);
                    Check("EPLP does not auto-grant paid card or duplicate free card",
                        LoadGold(inventory) == 10 && !run.PaidCardRewardDelivered,
                        ref failures);

                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGold("duplicate free-card click does not grant again", inventory, 10, ref failures);

                    var run2 = BuildRun(freeGold: 100, paidGold: 20);
                    session.Player.CurrentRun = run2;
                    service.HandleSelectCard(session, new byte[] { 0, 0 }).GetAwaiter().GetResult();
                    CheckGoldUpdatePacket("second free card sends gold refresh packet", peer, 110, ref failures);
                    service.HandleSelectCard(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    CheckGoldUpdatePacket("paid card sends gold refresh packet", peer, 90, ref failures);
                    CheckGold("explicit free card grant and paid card cost apply once", inventory, 90, ref failures);
                    Check("card rewards clear after both sides delivered",
                        run2.FreeCardRewardDelivered && run2.PaidCardRewardDelivered && run2.CardRewards == null,
                        ref failures);

                    service.HandleSelectCard(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    CheckGold("duplicate paid-card click does not spend again", inventory, 90, ref failures);

                    var run3 = BuildRun(freeGold: 5, paidGold: 7);
                    session.Player.CurrentRun = run3;
                    shouldReturn = service.HandleEplpCommand(session, new byte[] { 1, 0 }).GetAwaiter().GetResult();
                    Check("EPLP before any card reward does not grant", shouldReturn && LoadGold(inventory) == 90, ref failures);
                }
                finally
                {
                    inventory.ClearDirtyState();
                    InventoryContext.Unregister(session.SessionId, CharacterId);
                }
            }

            using (var peer = ConnectedSession.Create())
            {
                var session = peer.Session;
                session.Player.CharacterId = CharacterId;
                session.Account = new AccountRecord { AccountId = AccountId };
                session.Player.CurrentRun = new DungeonRun(11008, 0)
                {
                    Phase = DungeonRunPhase.ResultShown,
                    CardRewards = null,
                };

                var shouldReturn = service
                    .HandleEplpCommand(session, new byte[] { 1, 1 })
                    .GetAwaiter()
                    .GetResult();
                var sentExitAck = peer.TryReadPacket(out var packet)
                    && packet.Command == 0x01
                    && packet.Type == 0x0048;
                Check(
                    "EPLP exits immediately when settlement intentionally has no card flow",
                    shouldReturn && sentExitAck,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static DungeonRun BuildRun(int freeGold, int paidGold)
        {
            return new DungeonRun
            {
                Phase = DungeonRunPhase.CardsRevealed,
                CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    new ClearRewardGenerator.CardReward { IsGold = true, GoldAmount = freeGold },
                    default,
                    default,
                    default,
                    new ClearRewardGenerator.CardReward { IsGold = true, GoldAmount = paidGold },
                    default,
                    default,
                    default,
                },
                FreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                PaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
            };
        }

        private static int LoadGold(InventoryService inventory)
        {
            return inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'card-reward-flow', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, @aid, 'card-reward-flow');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static void CheckGold(string name, InventoryService inventory, int expected, ref int failures)
        {
            var actual = LoadGold(inventory);
            Check($"{name} expected={expected} actual={actual}", actual == expected, ref failures);
        }

        private static void CheckGoldUpdatePacket(string name, ConnectedSession peer, int expectedGold, ref int failures)
        {
            var ok = false;
            while (peer.TryReadPacket(out var packet))
            {
                if (packet.Command != 0x00 || packet.Type != 0x000E)
                    continue;

                if (ContainsGoldUpdate(packet.Body, expectedGold))
                {
                    ok = true;
                    break;
                }
            }

            Check($"{name} expectedGold={expectedGold}", ok, ref failures);
        }

        private static bool ContainsGoldUpdate(byte[] body, int expectedGold)
        {
            if (body == null || body.Length < 3 || body[0] != 0)
                return false;

            var count = BitConverter.ToUInt16(body, 1);
            var offset = 3;
            for (var i = 0; i < count; i++, offset += 84)
            {
                if (body.Length < offset + 10)
                    return false;

                var slot = BitConverter.ToInt16(body, offset);
                var itemId = BitConverter.ToInt32(body, offset + 2);
                var gold = BitConverter.ToInt32(body, offset + 6);
                if (slot == 0 && itemId == 0 && gold == expectedGold)
                    return true;
            }

            return false;
        }

        private static void DeleteTempDatabase(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
            var wal = path + "-wal";
            if (File.Exists(wal))
                File.Delete(wal);
            var shm = path + "-shm";
            if (File.Exists(shm))
                File.Delete(shm);
        }

        private sealed class ConnectedSession : IDisposable
        {
            private const int GameEnvelopeHeaderSize = 15;
            private readonly TcpClient _serverSide;

            private ConnectedSession(EnhancedClientSession session, TcpClient serverSide)
            {
                Session = session;
                _serverSide = serverSide;
                _serverSide.ReceiveTimeout = 1000;
            }

            public EnhancedClientSession Session { get; }

            public static ConnectedSession Create()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;

                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                client.Connect(endpoint.Address, endpoint.Port);
                var server = accept.GetAwaiter().GetResult();
                listener.Stop();

                return new ConnectedSession(
                    new EnhancedClientSession(client, new GamePacketHeader()),
                    server);
            }

            public void Dispose()
            {
                Session.Close();
                    _serverSide.Close();
            }

            public bool TryReadPacket(out SentPacket packet)
            {
                packet = default;
                try
                {
                    var stream = _serverSide.GetStream();
                    var header = new byte[GameEnvelopeHeaderSize];
                    if (!ReadExact(stream, header, 0, header.Length))
                        return false;

                    var length = (int)BitConverter.ToUInt32(header, 3);
                    if (length < GameEnvelopeHeaderSize)
                        return false;

                    var bodyLength = length - GameEnvelopeHeaderSize;
                    var body = new byte[bodyLength];
                    if (bodyLength > 0 && !ReadExact(stream, body, 0, bodyLength))
                        return false;

                    packet = new SentPacket(header[0], BitConverter.ToUInt16(header, 1), body);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    var read = stream.Read(buffer, offset, count);
                    if (read <= 0)
                        return false;

                    offset += read;
                    count -= read;
                }

                return true;
            }

            public struct SentPacket
            {
                public SentPacket(byte command, ushort type, byte[] body)
                {
                    Command = command;
                    Type = type;
                    Body = body;
                }

                public byte Command { get; }
                public ushort Type { get; }
                public byte[] Body { get; }
            }
        }
    }
}
