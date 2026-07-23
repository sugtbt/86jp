using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LoginHandler
    {
        private const string DefaultLoginMid = "10038";

        private readonly IAccountRepository _accountRepository;
        private readonly ICharacterRepository _characterRepository;
        private readonly AccountSettingsRepository _settingsRepository;
        private readonly HonorLevelSyncService _honorLevel;

        public string ProtocolName => "GameProtocol";

        public LoginHandler(IAccountRepository accountRepository, ICharacterRepository characterRepository)
        {
            _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _honorLevel = new HonorLevelSyncService(_characterRepository);
            _settingsRepository = new AccountSettingsRepository(
                Infrastructure.ServerPaths.DatabasePath,
                Infrastructure.ServerPaths.SchemaFilePath);
        }

        public async Task Handle_ClientFirstConnected(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0001, LoginPacketBuilder.BuildInitialLoginNotice()));
        }

        public async Task Handle_ENUM_CMDPACKET_LOGIN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                var mId = DefaultLoginMid;
                var passwordHash = string.Empty;
                if (LoginRequestParser.TryParse(body, out var parsed))
                {
                    mId = parsed.MId;
                    passwordHash = parsed.PasswordHash ?? string.Empty;
                    FileLogger.Log($"[{ProtocolName}] Login request parsed: m_id={mId} pwd_md5={passwordHash}");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Login body unparseable, falling back to m_id={DefaultLoginMid}");
                }

                var account = _accountRepository.GetByMid(mId);
                if (account == null)
                {
                    var newId = _accountRepository.Create(mId, passwordHash);
                    account = _accountRepository.GetById(newId);
                    FileLogger.Log($"[{ProtocolName}] Login auto-created account id={newId} m_id={mId}");
                }
                session.Account = account;
                var remoteIp = session.TcpClient?.Client?.RemoteEndPoint?.ToString() ?? string.Empty;
                _accountRepository.UpdateLastLogin(account.AccountId, remoteIp, DateTime.UtcNow);
                FileLogger.Log($"[{ProtocolName}] Login bound session {session.SessionId} -> account_id={account.AccountId} m_id={account.MId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Login account lookup failed: {ex.Message}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0001, LoginPacketBuilder.BuildLoginSuccess()));
            await SendAccountSettingsOnLoginAsync(session);
            foreach (var packet in AuctionServiceNotificationBuilder.BuildOpenPackets())
                await session.SendPacketAsync(packet);
            await _honorLevel.SendInfoAsync(session, ProtocolName, null);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x01A1, CommonPacketBodyBuilder.BuildZeroBytes(1)));
        }

        private async Task SendAccountSettingsOnLoginAsync(EnhancedClientSession session)
        {
            var accountId = session.Account?.AccountId ?? 1;
            var settings = _settingsRepository.Load(accountId);

            // 普通攻击连发等输入选项是账号级状态。必须在登录成功后、GET_USERINFO/选角前下发，
            // 只依赖选角色初始化包会导致 UI 勾选已恢复但运行态未应用。
            foreach (var packet in AccountSettingsPacketBuilder.BuildLoginAccountSettings(settings))
                await session.SendPacketAsync(packet);

            FileLogger.Log($"[{ProtocolName}] LOGIN account settings sent account={accountId}");
        }
    }
}
