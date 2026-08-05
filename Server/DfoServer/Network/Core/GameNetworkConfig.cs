using System;

namespace DfoServer.Network
{
    public static class GameNetworkConfig
    {
        public const string ChannelName = "ch.11";
        public const int ChannelServerIndex = 1;
        public const int ChannelIndex = 11;
        public const int NormalGamePort = 10011;
        public const int InitialUdpPort1 = 12311;
        public const int InitialUdpPort2 = 12312;
        public const int LoginChannelPort = 10128;
        public const int LoginUnknownPort = 17200;
        public const int CommandPacketCount = 1086;
        public const int NotificationPacketCount = 1036;

        public static string ServerIp { get; private set; } = "127.0.0.1";
        public static bool PacketCaptureEnabled { get; private set; }
        public static string PacketCaptureDir { get; private set; }
        public static bool ProxyMode { get; private set; }
        public static bool UdpRelayEnabled { get; private set; }
        public static string UdpRelayPublicIp { get; private set; }
        public static bool UdpRelayPublicIpConfigured { get; private set; }
        public static int UdpRelayPortBase { get; private set; } = 30000;
        public static int UdpRelayPortCount { get; private set; } = 256;

        public static void Configure(string[] args)
        {
            string serverIp = null;
            string udpRelayPublicIp = null;

            UdpRelayPublicIp = null;
            UdpRelayPublicIpConfigured = false;

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (string.Equals(
                            args[i],
                            "--server-ip",
                            StringComparison.OrdinalIgnoreCase) &&
                        i + 1 < args.Length)
                    {
                        serverIp = args[++i];
                    }
                    else if (string.Equals(
                                 args[i],
                                 "--udp-relay-public-ip",
                                 StringComparison.OrdinalIgnoreCase) &&
                             i + 1 < args.Length)
                    {
                        udpRelayPublicIp = args[++i];
                    }
                    else if (string.Equals(
                                 args[i],
                                 "--packet-capture",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        PacketCaptureEnabled = true;
                        if (i + 1 < args.Length &&
                            !args[i + 1].StartsWith("-"))
                        {
                            PacketCaptureDir = args[++i];
                        }
                    }
                    else if (string.Equals(
                                 args[i],
                                 "--proxy",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        ProxyMode = true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(serverIp))
                serverIp = Environment.GetEnvironmentVariable("SERVER_IP");
            if (string.IsNullOrWhiteSpace(serverIp))
            {
                serverIp = Environment.GetEnvironmentVariable(
                    "DFO_PUBLIC_SERVER_IP");
            }
            if (!string.IsNullOrWhiteSpace(serverIp))
                ServerIp = serverIp.Trim();

            UdpRelayEnabled = ReadBoolEnvironmentVariable(
                "DFO_UDP_RELAY",
                false);
            if (string.IsNullOrWhiteSpace(udpRelayPublicIp))
            {
                udpRelayPublicIp = Environment.GetEnvironmentVariable(
                    "DFO_UDP_RELAY_PUBLIC_IP");
            }
            if (!string.IsNullOrWhiteSpace(udpRelayPublicIp))
            {
                UdpRelayPublicIp = udpRelayPublicIp.Trim();
                UdpRelayPublicIpConfigured = true;
            }

            UdpRelayPortBase = ReadIntEnvironmentVariable(
                "DFO_UDP_RELAY_PORT_BASE",
                30000);
            UdpRelayPortCount = ReadIntEnvironmentVariable(
                "DFO_UDP_RELAY_PORT_COUNT",
                256);
        }

        internal static void ValidateRelayConfiguration()
        {
            if (UdpRelayEnabled &&
                !IsValidRelayPortRange(
                    UdpRelayPortBase,
                    UdpRelayPortCount))
            {
                throw new InvalidOperationException(
                    "Invalid party UDP relay port range: " +
                    $"{UdpRelayPortBase}/{UdpRelayPortCount}.");
            }
        }

        private static bool IsValidRelayPortRange(
            int portBase,
            int portCount)
            => portBase > 0 &&
               portCount >= 2 &&
               (long)portBase + portCount - 1 <= ushort.MaxValue;

        private static bool ReadBoolEnvironmentVariable(
            string name,
            bool fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return value.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => fallback,
            };
        }

        private static int ReadIntEnvironmentVariable(
            string name,
            int fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed)
                ? parsed
                : fallback;
        }
    }
}
