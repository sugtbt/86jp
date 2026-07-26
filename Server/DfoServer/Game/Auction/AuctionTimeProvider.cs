using System;

namespace DfoServer.Game.Auction
{
    internal interface IAuctionTimeProvider
    {
        long UtcNowUnixSeconds();
    }

    internal sealed class SystemAuctionTimeProvider
        : IAuctionTimeProvider
    {
        public static readonly SystemAuctionTimeProvider Instance =
            new SystemAuctionTimeProvider();

        private SystemAuctionTimeProvider()
        {
        }

        public long UtcNowUnixSeconds()
            => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
