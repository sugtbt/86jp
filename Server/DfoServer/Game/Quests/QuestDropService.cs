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
                var plannedDrops =
                    new List<(QuestDropCandidate Candidate, int HeldCount, int DropCount)>();
                var projectedHeldCounts = new Dictionary<int, int>();

                foreach (var candidate in candidates)
                {
                    if (!projectedHeldCounts.TryGetValue(candidate.ItemId, out var currentHeld))
                        currentHeld = inventory.CountMainItem(candidate.ItemId);

                    int dropCount = _rollDrop(candidate, currentHeld);
                    if (dropCount <= 0)
                        continue;

                    plannedDrops.Add((candidate, currentHeld, dropCount));
                    projectedHeldCounts[candidate.ItemId] =
                        currentHeld > int.MaxValue - dropCount
                            ? int.MaxValue
                            : currentHeld + dropCount;
                }

                if (plannedDrops.Count > 0)
                {
                    var requests = new List<InventoryRewardGrantRequest>(plannedDrops.Count);
                    foreach (var planned in plannedDrops)
                    {
                        requests.Add(InventoryRewardGrantRequest.Create(
                            planned.Candidate.ItemId,
                            planned.DropCount,
                            ItemCreateReason.QuestReward));
                    }

                    var batchSucceeded = InventoryRewardGrantService.TryGrantBatch(
                        inventory,
                        requests,
                        out var batchResult);
                    if (!batchSucceeded)
                    {
                        FileLogger.Log(
                            $"[{ProtocolLogName}] QUEST_DROP: batch insert failed " +
                            $"{sourceName}={sourceCode} requests={requests.Count} error={batchResult.Error}");
                    }

                    var resultCount = Math.Min(plannedDrops.Count, batchResult.Results.Count);
                    for (var index = 0; index < resultCount; index++)
                    {
                        var planned = plannedDrops[index];
                        var grant = batchResult.Results[index];
                        if (!grant.Success || grant.SlotIndex < 0)
                        {
                            FileLogger.Log(
                                $"[{ProtocolLogName}] QUEST_DROP: failed to insert " +
                                $"{sourceName}={sourceCode} item={planned.Candidate.ItemId} " +
                                $"x{planned.DropCount} held={planned.HeldCount} error={grant.Error}");
                            continue;
                        }

                        grantedItemIds.Add(planned.Candidate.ItemId);
                        grantedSlots.Add(grant.SlotIndex);
                    }
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
                // Avoid per-drop ACCEPTED_QUEST (0x023F); it causes a visible client hitch.
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressWithoutNotification(grantedItemIds);
            }

            // During a dungeon, refresh only the changed slots after quest progress has settled.
            await _inventoryRefresh.SendUpdateItemList(session, InventoryListType.Main, grantedSlots);
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
