using DfoServer.Game.Inventory;
using DfoServer.Game.Dungeon;
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
    // 规则数据来自 QuestDropProvider(PVF)，发放走在线背包服务，发放后同步寻物任务进度。
    // 原先寄居在副本共享服务里, 拆出归任务域。
    public sealed class QuestDropService
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly QuestDropNotificationBatcher _notificationBatcher;
        private readonly string _connectionString;
        private readonly Func<QuestDropCandidate, int, int> _rollDrop;
        private readonly DungeonItemAcquisitionService _itemAcquisition;

        public QuestDropService(
            InventoryRefreshSender inventoryRefresh,
            string connectionString = null,
            Func<QuestDropCandidate, int, int> rollDrop = null)
            : this(
                inventoryRefresh,
                connectionString,
                rollDrop,
                itemAcquisition: null)
        {
        }

        internal QuestDropService(
            InventoryRefreshSender inventoryRefresh,
            string connectionString,
            Func<QuestDropCandidate, int, int> rollDrop,
            DungeonItemAcquisitionService itemAcquisition)
        {
            if (inventoryRefresh == null) throw new ArgumentNullException(nameof(inventoryRefresh));
            _notificationBatcher = new QuestDropNotificationBatcher(inventoryRefresh);
            _connectionString = connectionString;
            _rollDrop = rollDrop ?? QuestDropProvider.RollDrop;
            _itemAcquisition = itemAcquisition
                ?? new DungeonItemAcquisitionService(new DropService());
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

        private Task CheckDrop(
            EnhancedClientSession session,
            int sourceCode,
            string sourceName,
            Func<ICollection<int>, List<QuestDropCandidate>> getCandidates)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !run.RewardPolicy.AllowsQuestDrops)
                return Task.CompletedTask;

            var activeQuestIds = LoadActiveQuestIds(session, $"{sourceName}={sourceCode}");
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return Task.CompletedTask;

            var candidates = getCandidates(activeQuestIds);
            if (candidates == null) return Task.CompletedTask;

            if (!InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped because online inventory is missing cid={session.Player.CharacterId}");
                return Task.CompletedTask;
            }

            var grantedItemIds = new HashSet<int>();
            var grantedSlots = new HashSet<short>();

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var requests = new List<DungeonItemGrantRequest>();
                var projectedHeldCounts = new Dictionary<int, int>();
                foreach (var candidate in candidates)
                {
                    if (!projectedHeldCounts.TryGetValue(
                            candidate.ItemId,
                            out var currentHeld))
                    {
                        currentHeld = inventory.CountMainItem(candidate.ItemId);
                    }

                    int dropCount = ClampDropCount(
                        candidate,
                        currentHeld,
                        _rollDrop(candidate, currentHeld));
                    if (dropCount <= 0)
                        continue;

                    requests.Add(new DungeonItemGrantRequest
                    {
                        QuestId = candidate.QuestId,
                        ItemTemplateId = candidate.ItemId,
                        Count = dropCount,
                        Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                    });
                    projectedHeldCounts[candidate.ItemId] =
                        currentHeld > int.MaxValue - dropCount
                            ? int.MaxValue
                            : currentHeld + dropCount;
                }

                if (requests.Count == 0)
                    return Task.CompletedTask;

                if (!_itemAcquisition.TryGrantItems(
                        inventory,
                        requests,
                        out var grants))
                {
                    FileLogger.Log(
                        $"[{ProtocolLogName}] QUEST_DROP: batch grant failed " +
                        $"{sourceName}={sourceCode} count={requests.Count} error={grants?.Error}");
                    return Task.CompletedTask;
                }

                foreach (var entry in grants.Entries)
                {
                    var grant = entry?.Grant;
                    if (entry?.Request == null
                        || grant == null
                        || grant.SlotIndex < 0)
                    {
                        continue;
                    }

                    grantedItemIds.Add(entry.Request.ItemTemplateId);
                    grantedSlots.Add(grant.SlotIndex);
                }
            }

            if (grantedItemIds.Count <= 0)
                return Task.CompletedTask;

            if (session.GameSession?.QuestManager == null)
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: granted {grantedItemIds.Count} item kinds but quest progress sync skipped because QuestManager is missing");
            }
            else
            {
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressWithoutNotification(
                        grantedItemIds);
            }

            // Coalesce only client projections after online inventory and quest state settle.
            _notificationBatcher.Queue(session, grantedSlots);
            return Task.CompletedTask;
        }

        internal static int ClampDropCount(
            QuestDropCandidate candidate,
            int currentHeld,
            int requestedCount)
        {
            if (requestedCount <= 0)
                return 0;

            var effectiveLimit = QuestDropProvider.GetEffectiveHeldLimit(candidate);
            if (effectiveLimit < 0)
                return requestedCount;
            if (currentHeld >= effectiveLimit)
                return 0;
            return Math.Min(requestedCount, effectiveLimit - currentHeld);
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
