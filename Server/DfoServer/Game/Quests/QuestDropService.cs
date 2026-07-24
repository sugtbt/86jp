using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Quests
{
    // 击杀怪物/摧毁被动物体后的任务掉落判定与发放。
    // 规则数据来自 QuestDropProvider(PVF), 发放走资产服务事务, 发放后同步寻物任务进度。
    // 原先寄居在副本共享服务里, 拆出归任务域。
    public sealed class QuestDropService
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly string _connectionString;
        private readonly Func<QuestDropCandidate, int, int> _rollDrop;

        public QuestDropService(
            InventoryRefreshSender inventoryRefresh,
            string connectionString = null,
            Func<QuestDropCandidate, int, int> rollDrop = null)
        {
            _inventoryRefresh = inventoryRefresh ?? throw new ArgumentNullException(nameof(inventoryRefresh));
            _connectionString = connectionString;
            _rollDrop = rollDrop ?? QuestDropProvider.RollDrop;
        }

        public async Task CheckMonsterDrop(EnhancedClientSession session, int monsterCode)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.DungeonId <= 0 || monsterCode <= 0) return;

            await CheckDrop(session, monsterCode, "monster", activeQuestIds =>
                QuestDropProvider.CheckMonsterDrop(
                    activeQuestIds, run.DungeonId, run.Difficulty, monsterCode));
        }

        public async Task CheckPassiveObjectDrop(EnhancedClientSession session, int objectCode)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.DungeonId <= 0 || objectCode <= 0) return;

            await CheckDrop(session, objectCode, "passive", activeQuestIds =>
                QuestDropProvider.CheckEnemyDrop(
                    activeQuestIds,
                    run.DungeonId,
                    run.Difficulty,
                    objectCode,
                    QuestDropProvider.EnemyTypePassiveObject));
        }

        public Task CheckAiCharacterDrop(EnhancedClientSession session, int aiCharacterCode)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || run.DungeonId <= 0 || aiCharacterCode <= 0)
                return Task.CompletedTask;

            return CheckDrop(session, aiCharacterCode, "ai-character", activeQuestIds =>
                QuestDropProvider.CheckEnemyDrop(
                    activeQuestIds,
                    run.DungeonId,
                    run.Difficulty,
                    aiCharacterCode,
                    QuestDropProvider.EnemyTypeAiCharacter));
        }

        public Task CheckDungeonClearReward(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || run.DungeonId <= 0)
                return Task.CompletedTask;

            return CheckDrop(
                session,
                run.DungeonId,
                "dungeon-clear",
                activeQuestIds => QuestDropProvider.CheckClearReward(
                    activeQuestIds,
                    run.DungeonId,
                    run.Difficulty));
        }

        private async Task CheckDrop(
            EnhancedClientSession session,
            int sourceCode,
            string sourceName,
            Func<ICollection<int>, List<QuestDropCandidate>> getCandidates)
        {
            var activeQuestIds = LoadActiveQuestIds(session, $"{sourceName}={sourceCode}");
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return;

            var candidates = getCandidates(activeQuestIds);
            if (candidates == null) return;

            if (!InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped because online inventory is missing cid={session.Player.CharacterId}");
                return;
            }

            var grantedItemIds = new HashSet<int>();
            var grantedSlots = new HashSet<short>();

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                foreach (var candidate in candidates)
                {
                    int currentHeld = inventory.CountMainItem(candidate.ItemId);

                    int dropCount = _rollDrop(candidate, currentHeld);
                    if (dropCount <= 0)
                    {
                        if (candidate.MaxStack != -1 && currentHeld >= candidate.MaxStack)
                            FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped maxStack {sourceName}={sourceCode} item={candidate.ItemId} held={currentHeld} max={candidate.MaxStack}");
                        else if (candidate.DropRate >= 100)
                            FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped despite guaranteed rate {sourceName}={sourceCode} item={candidate.ItemId} held={currentHeld} count={candidate.Count}");
                        continue;
                    }

                    if (!InventoryRewardGrantService.TryCreateAndInsert(
                            inventory,
                            candidate.ItemId,
                            ItemCreateReason.QuestReward,
                            dropCount,
                            out var grant)
                        || !grant.Success
                        || grant.SlotIndex < 0)
                    {
                        FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: failed to insert {sourceName}={sourceCode} item={candidate.ItemId} x{dropCount} held={currentHeld}");
                        continue;
                    }

                    grantedItemIds.Add(candidate.ItemId);
                    grantedSlots.Add(grant.SlotIndex);
                    FileLogger.Log(
                        $"[{ProtocolLogName}] QUEST_DROP: " +
                        $"quest={candidate.QuestId} {sourceName}={sourceCode} " +
                        $"item={candidate.ItemId} x{dropCount} slot={grant.SlotIndex} " +
                        $"preferQuestInventory={candidate.PreferQuestInventory} " +
                        $"held={currentHeld}->{currentHeld + dropCount}");
                }
            }

            if (grantedItemIds.Count <= 0)
                return;

            if (session.GameSession?.QuestManager == null)
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: granted {grantedItemIds.Count} item kinds but quest progress sync skipped because QuestManager is missing");
            }
            else
            {
                await session.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(grantedItemIds);
            }

            // During a dungeon, refresh only the changed slots after quest progress has settled.
            await _inventoryRefresh.SendUpdateItemList(session, InventoryListType.Main, grantedSlots);
            FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: UPDATE_ITEM_LIST sent slots={string.Join(",", grantedSlots)}");
        }

        private HashSet<int> LoadActiveQuestIds(
            EnhancedClientSession session,
            string source)
        {
            try
            {
                var connStr = !string.IsNullOrWhiteSpace(_connectionString)
                    ? _connectionString
                    : SqliteDatabaseBootstrap.Initialize(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var quests = QuestService.LoadActiveQuests(connStr, session.Player.CharacterId);
                return quests.Count > 0
                    ? new HashSet<int>(quests.ConvertAll(q => (int)q.QuestId))
                    : null;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP ERROR: active quest load failed, drop check skipped: {source}: {ex.Message}");
                return null;
            }
        }

    }
}
