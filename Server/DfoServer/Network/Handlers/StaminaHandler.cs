using System;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 疲劳虚弱恢复。扣金币走在线背包，角色尾部状态仍写 subtype0 字段。
    /// </summary>
    public sealed class StaminaHandler
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly InventoryRefreshSender _refresh;

        public StaminaHandler(InventoryRefreshSender refresh = null)
        {
            _refresh = refresh;
        }

        public async Task Handle_ENUM_CMDPACKET_RECOVER_STAMINA(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: uid={session?.Player?.UserId ?? 0} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendRecoverStaminaErrorAsync(session, 4);
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: online inventory missing cid={characterId}");
                return;
            }

            try
            {
                var repo = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var tail = repo.Load(characterId) ?? session.Player.Subtype0Tail;
                if (tail == null || tail.Stamina == 0)
                {
                    await SendRecoverStaminaErrorAsync(session, 18);
                    FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: no weakness state cid={characterId}");
                    return;
                }

                var cost = CalculateRecoverStaminaGoldCost(session.Player.Level, tail.Stamina);
                var updatedGold = 0;
                byte errorCode = 0;
                string rejectLog = null;

                lock (lease.SyncRoot)
                {
                    var currentGold = lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                    if (currentGold < cost)
                    {
                        errorCode = 22;
                        rejectLog = $"[{ProtocolLogName}] RECOVER_STAMINA: insufficient gold cid={characterId} need={cost} have={currentGold} stamina={tail.Stamina}";
                    }
                    else
                    {
                        updatedGold = currentGold;
                        if (cost > 0)
                        {
                            if (!lease.Inventory.TryConsumeMainItem(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    cost,
                                    out var consumed)
                                || !consumed.Success)
                            {
                                errorCode = 22;
                                rejectLog = $"[{ProtocolLogName}] RECOVER_STAMINA: TrySpendGold refused cid={characterId} need={cost}";
                            }
                            else
                            {
                                updatedGold = consumed.RemainingCount;
                            }
                        }
                    }
                }

                if (errorCode != 0)
                {
                    await SendRecoverStaminaErrorAsync(session, errorCode);
                    FileLogger.Log(rejectLog);
                    return;
                }

                tail.Stamina = 0;
                tail.FatiguePenalty = 0;
                SaveSubtype0Tail(characterId, tail);
                session.Player.Subtype0Tail = tail;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0021, new[] { (byte)100 }));
                if (_refresh != null)
                    await _refresh.SendGoldUpdate(session, updatedGold);

                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: success cid={characterId} cost={cost} gold={updatedGold}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA ERROR: cid={characterId} {ex}");
                await SendRecoverStaminaErrorAsync(session, 4);
            }
        }

        internal static int CalculateRecoverStaminaGoldCost(byte level, byte stamina)
        {
            if (stamina == 0)
                return 0;

            var basePrice = RecoverStaminaPriceProvider.GetBasePrice(level);
            var normalizedStamina = Math.Min((byte)10, stamina);
            var officialCurrentStamina = Math.Max(0, 100 - normalizedStamina * 9);
            var cost = basePrice * (100 - officialCurrentStamina) / 90;
            return Math.Max(0, cost);
        }

        private static void SaveSubtype0Tail(int characterId, UserInfoMinimumTailSnapshot tail)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                SqliteSubtype0FieldsRepository.Save(conn, characterId, tail);
            }
        }

        private static Task SendRecoverStaminaErrorAsync(EnhancedClientSession session, byte errorCode)
        {
            if (session == null || session.TcpClient == null || !session.TcpClient.Connected)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0009, new[] { (byte)0, errorCode, (byte)0 }));
        }
    }
}
