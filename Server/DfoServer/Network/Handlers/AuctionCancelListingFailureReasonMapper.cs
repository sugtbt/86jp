using DfoServer.Game.Auction;

namespace DfoServer.Network.Handlers
{
    internal static class AuctionCancelListingFailureReasonMapper
    {
        public static string Map(AuctionApplicationError error)
        {
            switch (error)
            {
                case AuctionApplicationError.InvalidLease:
                case AuctionApplicationError.NotOwner:
                    return "当前角色没有执行该拍卖操作的权限";

                case AuctionApplicationError.ListingNotFound:
                case AuctionApplicationError.ListingNotActive:
                case AuctionApplicationError.VersionConflict:
                    return "该拍卖品已不存在或状态发生变化";

                case AuctionApplicationError.CancellationWindowExpired:
                    return "该拍卖品已到期，无法取消上架";

                case AuctionApplicationError.MailRejected:
                    return "返还邮件创建失败，请稍后重试";

                default:
                    return "取消上架失败，请稍后重试";
            }
        }
    }
}
