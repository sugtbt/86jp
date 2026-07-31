using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using GameDungeon = DfoServer.Game.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    public sealed class DeathTowerCoordinator
    {
        private readonly DeathTowerSettlementService _settlementService;
        private readonly Func<EnhancedClientSession, DeathTowerSettlementResult, Task> _sendExpGrantNotification;
        private readonly Func<EnhancedClientSession, Task> _sendInDungeonLevelUpFollowups;
        private readonly InventoryRefreshSender _inventoryRefresh;

        public DeathTowerCoordinator()
            : this(null, null, null, null, null, null)
        {
        }

        internal DeathTowerCoordinator(
            string connectionString = null,
            DeathTowerExperienceGrantInTransaction grantExperienceInTransaction = null,
            Func<EnhancedClientSession, DeathTowerSettlementResult, Task> sendExpGrantNotification = null,
            AccountExperienceProgressService accountExperience = null,
            Func<EnhancedClientSession, Task> sendInDungeonLevelUpFollowups = null,
            InventoryRefreshSender inventoryRefresh = null)
        {
            _sendExpGrantNotification = sendExpGrantNotification;
            _sendInDungeonLevelUpFollowups = sendInDungeonLevelUpFollowups;
            _inventoryRefresh = inventoryRefresh;
            if (!string.IsNullOrWhiteSpace(connectionString))
                _settlementService = new DeathTowerSettlementService(
                    connectionString,
                    accountExperience,
                    grantExperienceInTransaction);
        }

        public bool TryCreateSession(int dungeonId, out DeathTowerSession tower)
        {
            tower = null;
            var config = DeathTowerData.GetConfig(dungeonId);
            if (config == null)
                return false;

            tower = new DeathTowerSession(config);
            return true;
        }

        public async Task SendEntryPacketsAsync(EnhancedClientSession session, DeathTowerSession tower, byte difficulty = 0)
        {
            if (InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                && lease.IsOwnedBy(session.SessionId))
            {
                try
                {
                    lock (lease.SyncRoot)
                    {
                        tower.SetPersistentMainSlotOccupancy(
                            lease.Inventory.GetItems(InventoryListType.Main)
                                .Select(item => item.Key)
                                .ToList());
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DeathTower] online inventory occupancy load failed; continuing without persistent-slot reservations: {ex.Message}");
                }
            }

            var dungeonId = tower.Config.DungeonId;
            var hasRun = session.Player.CurrentRun != null;
            FileLogger.Log($"[DeathTower] ENTER: cid={session.Player.CharacterId} dungeon={dungeonId} difficulty={difficulty} hasRun={hasRun} stages={tower.Config.TotalStages} basisLv={tower.Config.BasisLevel}");

            // NOTI 142 DEATH_TOWER_INFO (8B)
            var infoBody = DeathTowerPacketBuilder.BuildTowerInfo(dungeonId, (ushort)tower.Config.TotalStages);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008E, infoBody));
            FileLogger.Log($"[DeathTower] SENT 0x008E TOWER_INFO: bodyLen={infoBody.Length}");

            // NOTI 143 首层
            await SendStageMap(session, tower);

            // NOTI 0x1E FINISH_LOADING
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x001E FINISH_LOADING (entry)");
        }

        public async Task<bool> TryHandleMoveMap(EnhancedClientSession session)
        {
            var tower = session.Player.DeathTowerState;
            if (tower == null)
                return false;

            var prevState = tower.State;
            if (prevState >= 1)
                await SyncCurrentStageClearMapAsync(session, tower, "tower_move_map");

            if (!tower.TryAdvanceStage())
            {
                FileLogger.Log($"[DeathTower] MOVE_MAP rejected: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} state={tower.State} (need state>=1, not last stage)");
                return true;
            }

            if (prevState == 1)
                FileLogger.Log($"[DeathTower] MOVE_MAP advance from state=1 (0x009F(2) not received, 86JP may skip it)");

            FileLogger.Log($"[DeathTower] ADVANCE: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} map={tower.GetCurrentMapId()}");

            await SendStageMap(session, tower);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x001E FINISH_LOADING (advance)");

            return true;
        }

        public async Task HandleStageCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var tower = session.Player.DeathTowerState;
            if (tower == null)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: cid={session.Player?.CharacterId} not in tower");
                return;
            }
            if (body == null || body.Length < 1)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: body null or empty");
                return;
            }

            var commandType = body[0];
            switch (commandType)
            {
                case 1:
                    tower.SetFighting();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(1) fight start: cid={session.Player.CharacterId} stage={tower.CurrentStage}");
                    break;
                case 2:
                    tower.SetCleared();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(2) stage clear: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} isLast={tower.IsLastStage}");
                    await SyncCurrentStageClearMapAsync(session, tower, "tower_stage_cmd");
                    if (tower.IsLastStage)
                    {
                        await SendSettlement(session, tower);
                        return;
                    }
                    break;
                default:
                    FileLogger.Log($"[DeathTower] STAGE_CMD unknown commandType={commandType}: cid={session.Player.CharacterId} bodyHex={BitConverter.ToString(body)}");
                    break;
            }
        }

        private static Task SyncCurrentStageClearMapAsync(EnhancedClientSession session, DeathTowerSession tower, string source)
        {
            var mapId = tower.GetCurrentMapId();
            return DungeonClearMapQuestSync.SyncAsync(session, 0, mapId, source);
        }

        // 返城时清除塔状�?由生命周期统一清理路径调用; run 置换后本方法只负责日志与提前摘除)
        public static void ClearTowerState(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run?.Tower != null)
            {
                FileLogger.Log($"[DeathTower] CLEAR: cid={session.Player.CharacterId} wasStage={run.Tower.CurrentStage}");
                run.Tower = null;
            }
        }

        private async Task SendSettlement(EnhancedClientSession session, DeathTowerSession tower)
        {
            var cid = session.Player.CharacterId;
            if (!tower.TryBeginSettlement())
            {
                FileLogger.Log($"[DeathTower] SETTLEMENT duplicate ignored: cid={cid} dungeon={tower.Config.DungeonId}");
                return;
            }
            FileLogger.Log($"[DeathTower] SETTLEMENT begin: cid={cid} dungeon={tower.Config.DungeonId} stages={tower.Config.TotalStages}");

            DeathTowerSettlementResult settlement = null;
            if (_settlementService != null)
            {
                try
                {
                    if (!TryGetOwnedInventory(session, out var lease))
                    {
                        throw new InvalidOperationException(
                            $"Death tower settlement requires owned online inventory for character {cid}.");
                    }

                    var context = new DeathTowerSettlementContext(
                        cid,
                        session.Account?.AccountId ?? 1,
                        session.Player.Level,
                        session.Player.Exp);
                    settlement = _settlementService.Grant(context, tower, lease);
                    session.Player.Exp = settlement.ExperienceGrant.NewExp;
                    session.Player.Level = settlement.ExperienceGrant.NewLevel;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DeathTower] SETTLEMENT reward grant failed: cid={cid}: {ex}");
                    tower.AbortSettlement();
                    return;
                }
            }

            // NOTI 144 排行(空安全版)
            try
            {
                var rankingBody = DeathTowerPacketBuilder.BuildEmptyRanking(tower.Config.DungeonId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0090, rankingBody));
                FileLogger.Log($"[DeathTower] SENT 0x0090 RANKING: bodyLen={rankingBody.Length}");

                // NOTI 145: Death Tower reward groups.
                var rewardGroups = new IReadOnlyList<DeathTowerRewardItem>[]
                {
                    settlement?.Items ?? Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                };
                // The first u32 is a separate client field whose semantic name is not proven
                // in this build. Keep it zero; EXP and gold use their authoritative updates.
                var rewardBody = DeathTowerPacketBuilder.BuildReward(0, rewardGroups);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0091, rewardBody));
                FileLogger.Log($"[DeathTower] SENT 0x0091 REWARD: bodyLen={rewardBody.Length} items={settlement?.Items.Count ?? 0}");

                // NOTI 146 EPLP(通关=1)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0092, DeathTowerPacketBuilder.BuildEplp(true)));
                FileLogger.Log($"[DeathTower] SENT 0x0092 EPLP: cleared=true");

                if (settlement != null)
                {
                    if (settlement.ExpGained > 0 || settlement.CharacterStateChanged)
                    {
                        if (_sendExpGrantNotification != null)
                            await _sendExpGrantNotification(session, settlement);
                        else
                        {
                            FileLogger.Log($"[DeathTower] SETTLEMENT 0x0025 skipped: cid={cid} experience notification service unavailable");
                        }
                    }
                    if (settlement.LeveledUp && _sendInDungeonLevelUpFollowups != null)
                        await _sendInDungeonLevelUpFollowups(session);
                    if (settlement.GoldGained > 0)
                    {
                        if (_inventoryRefresh != null)
                            await _inventoryRefresh.SendGoldUpdate(session, settlement.UpdatedGold);
                    }
                    if (_inventoryRefresh != null && settlement.ChangedMainSlots.Count > 0)
                    {
                        await _inventoryRefresh.SendUpdateItemList(
                            session,
                            InventoryListType.Main,
                            settlement.ChangedMainSlots);
                    }
                    FileLogger.Log($"[DeathTower] SETTLEMENT rewards: cid={cid} floors={settlement.ClearedFloorCount} exp={settlement.ExpGained} normalExp={settlement.NormalExpGained} honorExp={settlement.HonorExpGained} gold={settlement.GoldGained} items={settlement.Items.Count} level={settlement.PreviousLevel}->{settlement.UpdatedLevel}");
                }

                FileLogger.Log($"[DeathTower] SETTLEMENT complete: cid={cid}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DeathTower] SETTLEMENT notification failed after commit: cid={cid}: {ex}");
            }
        }

        private async Task SendStageMap(EnhancedClientSession session, DeathTowerSession tower)
        {
            var mapId = tower.GetCurrentMapId();
            var monsters = DeathTowerMapLoader.LoadStageMonsters(tower);
            if (monsters.Count > byte.MaxValue)
            {
                FileLogger.Log($"[DeathTower] Stage monster list truncated to {byte.MaxValue}: stage={tower.CurrentStage} map={mapId} count={monsters.Count}");
                monsters.RemoveRange(byte.MaxValue, monsters.Count - byte.MaxValue);
            }
            if (monsters.Count == 0)
                FileLogger.Log($"[DeathTower] WARNING: stage={tower.CurrentStage} map={mapId} loaded 0 monsters (map may have only [apc random point] or PVF read failed)");

            var items = DeathTowerMapLoader.LoadStageItems(tower, monsters);
            var stageSeed = (uint)Infrastructure.ServerRandom.Next();
            tower.BeginStage(stageSeed, items);
            SyncCombatStage(session, tower, monsters);
            var body = DeathTowerPacketBuilder.BuildStageMap(tower, monsters, items, stageSeed);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008F, body));
            FileLogger.Log($"[DeathTower] SENT 0x008F STAGE_MAP: stage={tower.CurrentStage} map={mapId} monsters={monsters.Count} items={items.Count} seed={stageSeed} bodyLen={body.Length}");
        }

        private static bool TryGetOwnedInventory(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private static void SyncCombatStage(
            EnhancedClientSession session,
            DeathTowerSession tower,
            IReadOnlyList<StageMonster> monsters)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !ReferenceEquals(run.Tower, tower))
                return;

            var combatMonsters = new List<DfoServer.GameWorld.Dungeon.MonsterSumInfo>(monsters.Count);
            foreach (var monster in monsters)
            {
                combatMonsters.Add(new DfoServer.GameWorld.Dungeon.MonsterSumInfo
                {
                    Code = monster.MonsterIndex,
                    Level = monster.MonsterLevel,
                    Type = monster.MonsterType,
                    IsBlocking = monster.IsBoxMonster == 0,
                    TemplateOrder = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, monster.ListIndex)),
                    PacketIndex = monster.MonsterUniqueId,
                });
            }

            lock (run.SyncRoot)
            {
                if (!ReferenceEquals(run.Tower, tower))
                    return;

                run.RoomKilledSeqIds.Clear();
                run.Drops.Clear();
                run.RoomMonsters = combatMonsters;
                run.RoomStartSequence = monsters.Count > 0 ? monsters[0].MonsterUniqueId : (ushort)0;
                run.Seed = tower.StageSeed;
                run.RoomLcg = tower.StageLcg;
            }
        }

        public bool TryGenerateDropsForMonster(
            EnhancedClientSession session,
            ushort monsterUniqueId,
            out IReadOnlyList<GameDungeon.DropInfo> drops)
        {
            var tower = session?.Player?.DeathTowerState;
            if (tower == null)
            {
                drops = Array.Empty<GameDungeon.DropInfo>();
                return false;
            }

            drops = tower.GenerateDropsForMonster(monsterUniqueId);

            FileLogger.Log($"[DeathTower] DIE_MONSTER: cid={session.Player.CharacterId} stage={tower.CurrentStage} monsterUid={monsterUniqueId} drops={drops.Count} ground={tower.GroundItems.Count}");
            return true;
        }

        public async Task<bool> TryHandleGetItem(EnhancedClientSession session, ushort sceneSlot)
        {
            var tower = session?.Player?.DeathTowerState;
            if (tower == null)
                return false;

            if (!tower.TryPickupGroundItem(sceneSlot, out var pickup))
            {
                FileLogger.Log($"[DeathTower] GET_ITEM rejected: cid={session.Player.CharacterId} sceneSlot={sceneSlot} ground={tower.GroundItems.Count} inventory={tower.InventoryItems.Count}");
                return true;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0027,
                DropItemBuilder.BuildPickupItem(
                    sceneSlot,
                    session.Player.UserId,
                    (ushort)pickup.DestinationSlot,
                    7)));
            await SendInventoryUpdates(session, tower, pickup.ChangedSlots);
            RecalibrateTowerQuestProgress(session, tower, pickup.ItemId);
            FileLogger.Log($"[DeathTower] GET_ITEM: cid={session.Player.CharacterId} sceneSlot={sceneSlot} item={pickup.ItemId} towerSlot={pickup.DestinationSlot}");
            return true;
        }

        public async Task<bool> TryHandleUseStackable(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var tower = session?.Player?.DeathTowerState;
            if (tower == null)
                return false;
            if (body == null || body.Length < 7)
                return true;

            var slot = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            var instanceValue = BitConverter.ToInt32(body, 3);
            if (listType != InventoryListType.Main
                && (listType != InventoryListType.QuickSlot
                    || !ItemSlotBoundService.IsMainQuickSlot(slot)))
                return false;

            var expectedItemId = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;
            if (expectedItemId <= 0 && tower.TryGetInventoryItem(slot, out var authoritativeItem))
                expectedItemId = authoritativeItem.ItemId;
            if (expectedItemId <= 0
                || !tower.TryUseItem(slot, expectedItemId, out var mutation))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x002C,
                    UseStackableAckBuilder.BuildError((byte)listType, instanceValue, expectedItemId)));
                FileLogger.Log($"[DeathTower] USE_STACKABLE rejected: cid={session.Player.CharacterId} list={listType} slot={slot} item={expectedItemId}");
                return true;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x002C,
                UseStackableAckBuilder.BuildSuccess(
                    slot,
                    (byte)listType,
                    instanceValue,
                    expectedItemId)));
            await SendInventoryUpdates(session, tower, mutation.ChangedSlots);
            RecalibrateTowerQuestProgress(session, tower, mutation.ItemId);
            FileLogger.Log($"[DeathTower] USE_STACKABLE: cid={session.Player.CharacterId} slot={slot} item={expectedItemId} remaining={mutation.RemainingCount}");
            return true;
        }

        public async Task<bool> TryHandleMoveItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var tower = session?.Player?.DeathTowerState;
            if (tower == null)
                return false;
            if (body == null || body.Length < 14)
                return true;

            var sourceListType = (InventoryListType)body[0];
            var sourceSlot = BitConverter.ToInt16(body, 1);
            var moveCount = BitConverter.ToInt32(body, 7);
            var destinationListType = (InventoryListType)body[11];
            var destinationSlot = BitConverter.ToInt16(body, 12);
            var touchesTower = IsTowerEndpoint(sourceListType, sourceSlot, tower)
                || IsTowerEndpoint(destinationListType, destinationSlot, tower);
            if (!touchesTower)
                return false;

            if (!IsSupportedTowerEndpoint(sourceListType, sourceSlot)
                || !IsSupportedTowerEndpoint(destinationListType, destinationSlot)
                || !tower.TryMoveItem(sourceSlot, destinationSlot, moveCount, out var move))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0013,
                    MoveItemSpaceAckBuilder.BuildError(
                        0x04,
                        (byte)sourceListType,
                        (byte)destinationListType)));
                FileLogger.Log($"[DeathTower] MOVE_ITEMSPACE rejected: cid={session.Player.CharacterId} src={sourceListType}:{sourceSlot} dst={destinationListType}:{destinationSlot} count={moveCount}");
                return true;
            }

            var ackResult = new InventoryMoveResult
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlot,
                MoveValue32 = move.MoveValue32,
                DestinationListType = destinationListType,
                DestinationSlotIndex = destinationSlot,
                Mutated = move.ChangedSlots.Count > 0,
            };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0013,
                MoveItemSpaceAckBuilder.Build(ackResult)));
            if (move.ChangedSlots.Count > 0)
                await SendInventoryUpdates(session, tower, move.ChangedSlots);
            FileLogger.Log($"[DeathTower] MOVE_ITEMSPACE: cid={session.Player.CharacterId} src={sourceSlot} dst={destinationSlot} count={moveCount}");
            return true;
        }

        private static async Task SendInventoryUpdates(
            EnhancedClientSession session,
            DeathTowerSession tower,
            IReadOnlyList<short> slots)
        {
            var mainSlots = new List<short>();
            var quickSlots = new List<short>();
            foreach (var slot in slots)
            {
                var itemSpace = ItemSlotBoundService.IsMainQuickSlot(slot)
                    ? InventoryListType.QuickSlot
                    : InventoryListType.Main;

                if (itemSpace == InventoryListType.QuickSlot)
                    AddSlot(quickSlots, slot);
                else
                    AddSlot(mainSlots, slot);
            }

            await SendTowerItemUpdates(session, tower, InventoryListType.QuickSlot, quickSlots);
            await SendTowerItemUpdates(session, tower, InventoryListType.Main, mainSlots);
        }

        private static async Task SendTowerItemUpdates(
            EnhancedClientSession session,
            DeathTowerSession tower,
            InventoryListType listType,
            IReadOnlyList<short> slots)
        {
            if (slots == null || slots.Count == 0)
                return;

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteUInt16((ushort)slots.Count);
            foreach (var slot in slots)
            {
                if (tower.TryGetInventoryItem(slot, out var item))
                    ItemListProtocolWriter.WriteCommonEntry84(writer, slot, CreateTowerItemCore(item));
                else
                    ItemListProtocolWriter.WriteEmptyEntry(writer, listType, slot);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, writer.ToArray()));
        }

        private static ItemCore CreateTowerItemCore(TowerInventoryItem item)
        {
            var itemKind = ItemCore.KindConsumable;
            if (item != null && ItemMetadataResolver.TryResolveItemKind(item.ItemId, out var resolvedKind))
                itemKind = resolvedKind;

            var core = ItemCore.Create(itemKind, item?.ItemId ?? 0);
            core.Count = item?.Count ?? 0;
            return core;
        }

        private static void AddSlot(List<short> slots, short slot)
        {
            if (!slots.Contains(slot))
                slots.Add(slot);
        }

        private static bool IsSupportedTowerEndpoint(InventoryListType listType, short slot)
            => listType == InventoryListType.Main
                || (listType == InventoryListType.QuickSlot
                    && ItemSlotBoundService.IsMainQuickSlot(slot));

        private static bool IsTowerEndpoint(
            InventoryListType listType,
            short slot,
            DeathTowerSession tower)
            => IsSupportedTowerEndpoint(listType, slot)
                && (ItemSlotBoundService.IsMainQuickSlot(slot)
                    || tower.InventoryItems.ContainsKey(slot));

        private static void RecalibrateTowerQuestProgress(
            EnhancedClientSession session,
            DeathTowerSession tower,
            int itemId)
        {
            var questManager = session?.GameSession?.QuestManager;
            if (questManager == null || itemId <= 0)
                return;
            questManager.RecalibrateItemSeekingQuestProgressWithoutNotification(
                new[] { itemId },
                tower.GetItemCountsSnapshot());
        }
    }
}
