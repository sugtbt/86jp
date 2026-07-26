using DfoServer.Game.Auction;

namespace DfoServer.Network.Handlers
{
    internal static class AuctionRegisterItemFailureReasonMapper
    {
        public static string Map(AuctionApplicationError error)
        {
            switch (error)
            {
                case AuctionApplicationError.InvalidLease:
                case AuctionApplicationError.OwnershipMismatch:
                case AuctionApplicationError.NotOwner:
                    return "当前角色没有执行该拍卖操作的权限";

                case AuctionApplicationError.ItemNotFound:
                case AuctionApplicationError.ItemMismatch:
                    return "物品不存在或已发生变化";

                case AuctionApplicationError.InvalidQuantity:
                case AuctionApplicationError.NotEnoughQuantity:
                case AuctionApplicationError.NonStackableQuantity:
                    return "物品数量不足";

                case AuctionApplicationError.TradeRestricted:
                case AuctionApplicationError.ItemExpired:
                    return "该物品无法上架拍卖行";

                case AuctionApplicationError.SortLocked:
                case AuctionApplicationError.EquipmentLocked:
                    return "请先解除物品锁定";

                case AuctionApplicationError.InvalidTerms:
                    return "请输入正确的一口价";

                case AuctionApplicationError.AuctionGoldLimitExceeded:
                    return "您输入的金额超过拍卖额上限";

                case AuctionApplicationError.ActiveListingLimitReached:
                    return "已超过上架数量上限， 需要拍卖行优惠券才能继续上架";

                case AuctionApplicationError.InsufficientDepositGold:
                    return "金币不足，无法支付10000金币保证金";

                default:
                    return "拍卖行操作失败，请稍后重试";
            }
        }
    }
}
