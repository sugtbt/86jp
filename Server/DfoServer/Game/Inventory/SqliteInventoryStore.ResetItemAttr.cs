using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        /// <summary>
        /// 根据装备使用等级和品级，查表计算当前封装次数所需的黄金蜜蜡消耗数量。
        /// 公式：基础消耗 = f(等级区间, 稀有度) ; 总消耗 = 基础消耗 + (封装次数 - 1)。
        /// levelRanges: [1~30] [31~50] [51~70] [71~86]
        /// rarity: 2=紫(rare) 3=粉(unique) 6=金(epic)
        /// resealCount: 本次封装后的新次数(1~7)
        /// </summary>
        public static int ComputeWaxResealCost(int rarity, int minimumLevel, int newResealCount)
        {
            if (newResealCount < 1) newResealCount = 1;
            if (newResealCount > 7) newResealCount = 7;

            int baseCost = rarity switch
            {
                2 => minimumLevel switch // 紫色 (Rare)
                {
                    <= 30 => 3,
                    <= 50 => 6,
                    <= 70 => 9,
                    _ => 12,
                },
                3 => minimumLevel switch // 粉色 → 表中"黄色品级"
                {
                    <= 30 => 4,
                    <= 50 => 8,
                    <= 70 => 12,
                    _ => 16,
                },
                6 => minimumLevel switch // 金色 → 表中"金色品级"
                {
                    <= 30 => 6,
                    <= 50 => 12,
                    <= 70 => 18,
                    _ => 24,
                },
                _ => 1, // 未知品级兜底
            };

            // 再封装次数每增加 1 次，所需蜜蜡数也增加 1 个
            return baseCost + (newResealCount - 1);
        }

        // 0x0051 RESET_ITEM_ATTR (黄金蜜蜡/重新封装装备) 的存储层逻辑。
        // 读取目标装备 extra_json.extData0 的高 3 位(bits 5-7)作为封装次数，+1 后写回。
        // 上限 7 次，达到上限拒绝；同时消耗 N 个蜡(按等级+品级+次数公式) + seal_flag 置 1。
        public bool TryUseWaxForReseal(
            int characterId,
            int accountId,
            short targetSlot,
            int expectedTargetItemId,
            short waxSlot,
            out WaxResealResult result)
        {
            result = null;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    // 1. 加载目标装备(主背包)。
                    var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, targetSlot);
                    if (target == null)
                        return false;

                    // 客户端携带的 targetItemId 作为一致性校验(非 0 时必须匹配)。
                    if (expectedTargetItemId != 0 && target.ItemTemplateId != expectedTargetItemId)
                        return false;

                    // 只对装备类生效。
                    if (!string.Equals(target.ItemKind, "equipment", StringComparison.OrdinalIgnoreCase))
                        return false;

                    // 2. 解析装备元数据(品级+使用等级)，计算蜜蜡消耗公式。
                    var metadata = ItemMetadataResolver.Resolve(expectedTargetItemId);
                    var rarity = metadata?.Rarity ?? 0;
                    var minimumLevel = metadata?.MinimumLevel ?? 0;

                    // 3. 读取当前封装次数（extra_json.extData0 高 3 位），检查上限。
                    var view = InventoryItemView.ForCommon(target);
                    var currentCount = view.ReSealCount;
                    if (currentCount >= 7)
                        return false;

                    var newCount = (byte)(currentCount + 1);

                    // 4. 按公式计算本次需消耗的蜜蜡数量。
                    var waxCost = ComputeWaxResealCost(rarity, minimumLevel, newCount);

                    // 5. 检查蜡堆叠是否足够。
                    var waxItem = _db.LoadItemRecord(connection, transaction, characterId, MapToDbListType(InventoryListType.Main), waxSlot);
                    if (waxItem == null || GetStackedRecordCount(waxItem) < waxCost)
                        return false;

                    // 6. 更新封装次数 + seal_flag。
                    view.ReSealCount = newCount;
                    target.SealFlag = 1;
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    UpdateSealFlag(connection, transaction, target.ItemUid, 1);

                    // 7. 消耗 N 个黄金蜜蜡(主背包 waxSlot)。
                    var dbListType = MapToDbListType(InventoryListType.Main);
                    if (!TryDeleteItemCore(connection, transaction, characterId, InventoryListType.Main, dbListType, waxSlot, (short)waxCost, out _))
                        return false;

                    transaction.Commit();

                    result = new WaxResealResult
                    {
                        TargetListType = InventoryListType.Main,
                        TargetSlotIndex = targetSlot,
                        TargetItemTemplateId = target.ItemTemplateId,
                        WaxSlotIndex = waxSlot,
                        WaxCost = waxCost,
                        NewSealFlag = 1,
                        NewReSealCount = newCount,
                    };
                    return true;
                }
            }
        }

        private static void UpdateSealFlag(SqliteConnection connection, SqliteTransaction transaction, long itemUid, byte sealFlag)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET seal_flag = @sealFlag,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@sealFlag", sealFlag);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }
    }

    public sealed class WaxResealResult
    {
        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short WaxSlotIndex { get; set; }
        public int WaxCost { get; set; }
        public byte NewSealFlag { get; set; }
        public byte NewReSealCount { get; set; }
    }
}
