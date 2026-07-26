using System;

namespace DfoServer.Network.Parsers.CeraShop
{
    public sealed class CeraShopPurchaseRequest
    {
        // 一次购买可包含多件不同商品(购物车), 每件一个 commodityNo
        public System.Collections.Generic.List<int> CommodityNos { get; } = new System.Collections.Generic.List<int>();

        // 每件商品对应的属性选择字节，仅装扮类商品生效。
        public System.Collections.Generic.List<byte> AttributeValues { get; } = new System.Collections.Generic.List<byte>();

        // 支付方式: 0=点券(Cera), 1=装扮兑换券(AvatarCoupon)。
        public byte PaymentMode { get; private set; }

        public bool CouponSelected { get; private set; }

        public int CouponItemId { get; private set; }

        public short CouponSlot { get; private set; } = -1;

        // 兼容旧调用: 取第一件
        public int ProductId => CommodityNos.Count > 0 ? CommodityNos[0] : 0;
        public int ItemTemplateId => ProductId;
        public int Count => 1; // 每个 commodityNo 购买 1 份, 份内数量由 cerashop 商品定义

        // 请求 body 布局(由客户端 sendBuyPacket 逆向):
        //   [0..1] 未知  [2] totalCount(byte)  [3] 未知  [4] paymentMode(0=点券,1=兑换券)
        //   之后每件商品项 15 字节, commodityNo(int) 在项内偏移 +3
        private const int HeaderSize = 4;
        private const int ItemStride = 15;
        private const int CommodityOffsetInItem = 3;

        public static bool TryParse(byte[] body, out CeraShopPurchaseRequest request)
        {
            request = null;
            if (body == null || body.Length < HeaderSize + ItemStride)
                return false;

            int totalCount = body[2];
            if (totalCount <= 0)
                totalCount = 1;

            var parsed = new CeraShopPurchaseRequest
            {
                CouponSelected = body[3] != 0,
                PaymentMode = body[4],
            };
            for (int i = 0; i < totalCount; i++)
            {
                int itemBase = HeaderSize + i * ItemStride;
                int commodityOffset = itemBase + CommodityOffsetInItem;
                if (commodityOffset + 4 > body.Length)
                    break;
                int commodityNo = BitConverter.ToInt32(body, commodityOffset);
                if (commodityNo > 0)
                {
                    parsed.CommodityNos.Add(commodityNo);
                    parsed.AttributeValues.Add(body[itemBase + 1]);
                }
            }

            if (parsed.CommodityNos.Count == 0)
                return false;

            if (parsed.CouponSelected)
            {
                // Package purchases may append a variable-length component list.
                // The selected coupon tuple is always the final itemId(U32)+slot(U16).
                var couponOffset = body.Length - 6;
                if (couponOffset < 0 || couponOffset + 6 > body.Length)
                    return false;

                parsed.CouponItemId = BitConverter.ToInt32(body, couponOffset);
                parsed.CouponSlot = BitConverter.ToInt16(body, couponOffset + 4);
                if (parsed.CouponItemId <= 0 || parsed.CouponSlot < 0)
                    return false;
            }

            request = parsed;
            return true;
        }
    }
}
