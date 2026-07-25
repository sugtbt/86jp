using System.Threading.Tasks;
using DfoServer.Game.Quests;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public sealed class GameSession : ISessionPacketSender
    {
        private readonly EnhancedClientSession _networkSession;
        private readonly string _connStr;

        public QuestManager QuestManager { get; private set; }

        public PlayerContext Player { get { return _networkSession.Player; } }
        public int CharacterId { get { return _networkSession.Player != null ? _networkSession.Player.CharacterId : 0; } }
        public int AccountId { get { return _networkSession.Account != null ? _networkSession.Account.AccountId : 1; } }

        public GameSession(EnhancedClientSession networkSession, string connStr)
        {
            _networkSession = networkSession;
            _connStr = connStr;
            QuestManager = new QuestManager(this, connStr);
        }

        public Task SendPacketAsync(byte[] rawPacket)
        {
            return _networkSession.SendPacketAsync(rawPacket);
        }

        public Task SendNotiAsync(ushort notiType, byte[] body)
        {
            return _networkSession.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, notiType, body));
        }

        public Task SendCmdAckAsync(ushort cmdType, byte[] body)
        {
            return _networkSession.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x01, cmdType, body));
        }

    }
}
