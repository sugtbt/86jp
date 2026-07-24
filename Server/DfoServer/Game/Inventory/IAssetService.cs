using DfoServer.Game.Currency;

namespace DfoServer.Game.Inventory
{
    public enum ItemPlacementHint
    {
        Natural,
        QuestInventory,
    }

    public interface IAssetService
    {
        DbScope OpenScope(int characterId, int accountId);

        bool TryAddItem(DbScope scope, int itemTemplateId, int count, out short assignedSlot);
        bool TryAddItem(
            DbScope scope,
            int itemTemplateId,
            int count,
            ItemPlacementHint placementHint,
            out short assignedSlot)
            => TryAddItem(
                scope,
                itemTemplateId,
                count,
                out assignedSlot);
        bool TryRemoveItem(DbScope scope, int itemTemplateId, int count, out short slot, out int remaining);
        int CountItem(DbScope scope, int itemTemplateId);

        WalletSnapshot LoadWallet(DbScope scope);

        // 发放返回受携带上限约束后的实际入账额。扣费余额不足返回false且余额不变。
        int GrantGold(DbScope scope, int amount);
        bool TrySpendGold(DbScope scope, int amount);
        void GrantLuckyStar(DbScope scope, int amount);
        bool TrySpendLuckyStar(DbScope scope, int amount);

        CharacterItemListSnapshot LoadSnapshot(DbScope scope);
    }
}
