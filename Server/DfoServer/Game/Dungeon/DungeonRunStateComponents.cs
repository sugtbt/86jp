using DfoServer.Game.Quests;
using DfoServer.Game.Dungeon.Tournament;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonRunSelectionState
    {
        internal int MazeIndex { get; set; } = -1;
        internal int LayeredMapIndex { get; set; } = -1;
        internal bool MazeQuestConnected { get; set; }
        internal int MazeStartMapId { get; set; }
        internal int MazeStartX { get; set; } = -1;
        internal int MazeStartY { get; set; } = -1;
        internal int TotalRoomCount { get; set; } = 1;
        internal int EntryPartyMemberCount { get; set; } = 1;
        internal int ChronicleDropJobGroup { get; set; } = -1;
        internal int LinkedDungeonNextId { get; set; }
        internal int LinkedDungeonNextRate { get; set; }
        internal int LinkedDungeonNextCondition { get; set; }

        internal bool HellMode { get; set; }
        internal byte HellPartyMode { get; set; }
        internal bool VeryDifficultHell { get; set; }
        internal bool HellGorgeousChallenge { get; set; }
        internal int HellMapId { get; set; } = -1;
        internal byte HellMapX { get; set; } = 0xFF;
        internal byte HellMapY { get; set; } = 0xFF;
        internal GameWorld.Dungeon.HellPartyRoomInfo HellRoomInfo { get; set; }
    }

    internal sealed class DungeonRunCombatState
    {
        internal ushort MonsterCount { get; set; }
        internal ushort RoomStartSequence { get; set; }
        internal IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> RoomMonsters { get; set; }
            = Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
        internal HashSet<ushort> RoomKilledSeqIds { get; set; }
            = new HashSet<ushort>();
        internal RoomKey RoomKey { get; set; }
        internal Dictionary<RoomKey, RoomState> RoomStates { get; set; }
            = new Dictionary<RoomKey, RoomState>();
        internal uint Seed { get; set; }
        internal DnfLcg RoomLcg { get; set; }
        internal List<RidableObjectSpawnEntry> RidableObjects { get; set; }
            = new List<RidableObjectSpawnEntry>();

        internal ClearConditionState ClearCondition { get; set; }
        internal int BossCode { get; set; }
        internal int[] BossMapPos { get; set; }
        internal int SelectedBossMapId { get; set; } = -1;

        internal uint TotalExp { get; set; }
        internal uint BossTotalExp { get; set; }
        internal uint ChampionTotalExp { get; set; }
        internal uint SuperChampionTotalExp { get; set; }
        internal uint NamedMonsterTotalExp { get; set; }
        internal uint MonsterGrowthContractBonusExp { get; set; }
        internal int TotalGold { get; set; }

        internal ushort SceneSlotCounter { get; set; }
        internal Dictionary<ushort, DropInfo> Drops { get; set; }
            = new Dictionary<ushort, DropInfo>();
        internal bool IsWaitingDeathRespawn { get; set; }
        internal DateTime DeathRespawnAvailableAt { get; set; } = DateTime.MinValue;
    }

    internal sealed class DungeonRunSettlementData
    {
        internal SemaphoreSlim CardProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal SemaphoreSlim DeathTowerProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal SemaphoreSlim BloodAltarProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal DungeonSettlementRuntime Runtime { get; set; }
        internal DeathTower.DeathTowerSettlementRuntime DeathTower { get; set; }
        internal SecretShop.SecretShopOffer SecretShopOffer { get; set; }
        internal List<ClearRewardGenerator.CardReward> CardRewards { get; set; }
        internal int PaidCardCost { get; set; }
        internal int CardFlipCount { get; set; }
        internal byte[] FreeCardSlots { get; set; } = { 0xFF, 0xFF, 0xFF, 0xFF };
        internal byte[] PaidCardSlots { get; set; } = { 0xFF, 0xFF, 0xFF, 0xFF };
        internal bool FreeCardRewardDelivered { get; set; }
        internal bool PaidCardRewardDelivered { get; set; }
        internal int CardAutoFlipDelayMs { get; set; }
        internal int? PendingPresentationRankPoint { get; set; }
        internal TournamentParticipantRewardState Tournament { get; set; }
    }

    internal sealed class DungeonRunQuestBridgeState
    {
        private readonly HashSet<(int DungeonId, int MapId)> _syncedClearMapTargets =
            new HashSet<(int DungeonId, int MapId)>();
        private readonly Dictionary<(ushort QuestId, int ChannelIndex), int>
            _pendingServerTriggerEchoes =
                new Dictionary<(ushort QuestId, int ChannelIndex), int>();
        private readonly HashSet<QuestActivationId> _npcItemDropActivations =
            new HashSet<QuestActivationId>();

        internal QuestRunSnapshot Snapshot { get; set; } = QuestRunSnapshot.Empty;

        internal bool TryMarkClearMapSynced(int dungeonId, int mapId)
            => _syncedClearMapTargets.Add((dungeonId, mapId));

        internal void UnmarkClearMapSynced(int dungeonId, int mapId)
            => _syncedClearMapTargets.Remove((dungeonId, mapId));

        internal void MarkServerTrigger(ushort questId, int channelIndex)
        {
            var key = (questId, channelIndex);
            _pendingServerTriggerEchoes.TryGetValue(key, out var pending);
            _pendingServerTriggerEchoes[key] = pending + 1;
        }

        internal bool TryConsumeServerTrigger(
            ushort questId,
            int channelMask)
        {
            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                if ((channelMask & (1 << channelIndex)) == 0)
                    continue;

                if (!_pendingServerTriggerEchoes.TryGetValue(
                        (questId, channelIndex),
                        out var pending)
                    || pending <= 0)
                {
                    return false;
                }
            }

            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                if ((channelMask & (1 << channelIndex)) == 0)
                    continue;

                var key = (questId, channelIndex);
                var pending = _pendingServerTriggerEchoes[key];
                if (pending == 1)
                    _pendingServerTriggerEchoes.Remove(key);
                else
                    _pendingServerTriggerEchoes[key] = pending - 1;
            }

            return true;
        }

        internal bool HasPendingServerTriggers()
            => _pendingServerTriggerEchoes.Count > 0;

        internal bool TryMarkNpcItemDropGenerated(
            QuestActivationId activationId)
            => activationId.IsValid
                && _npcItemDropActivations.Add(activationId);

        internal void UnmarkNpcItemDropGenerated(
            QuestActivationId activationId)
        {
            if (activationId.IsValid)
                _npcItemDropActivations.Remove(activationId);
        }
    }

    internal sealed class DungeonMechanismRuntimeSet
    {
        internal SpecialDungeonRuntime SpecialDungeon { get; set; }
        internal bool IgnoreDefaultDungeonClear { get; set; }
        internal IReadOnlyList<IReadOnlyList<(byte X, byte Y)>>
            SpecialMinimapIconGroups { get; set; }
        internal List<BossEntranceConditionTargetState>
            BossEntranceConditionTargets { get; set; }
                = new List<BossEntranceConditionTargetState>();
        internal List<int> BossEntranceConditionalSummonCodes { get; set; }
            = new List<int>();
        internal bool BossEntranceConditionComplete { get; set; }
        internal bool ConditionalBossSpawned { get; set; }
        internal int ConditionalBossCode { get; set; }
        internal ScriptedFatalEndpointRuntime ScriptedFatalEndpoint { get; set; }

        internal bool TimeSpiralTeleportPending { get; set; }
        internal int TimeSpiralTrapMapId { get; set; }
        internal bool TimeSpiralTargetActive { get; set; }
        internal int TimeSpiralTargetX { get; set; } = -1;
        internal int TimeSpiralTargetY { get; set; } = -1;
        internal int TimeSpiralTargetFlag { get; set; } = -1;
        internal int TimeSpiralTargetWeight { get; set; }
        internal bool TimeSpiralHiddenBossActive { get; set; }
        internal ushort TimeSpiralHiddenBossSeqId { get; set; }
        internal int TimeSpiralHiddenBossCode { get; set; }
        internal int TimeSpiralHiddenBossMapId { get; set; }
        internal int TimeSpiralHiddenBossX { get; set; } = -1;
        internal int TimeSpiralHiddenBossY { get; set; } = -1;
        internal string TimeSpiralHiddenBossSource { get; set; }

        internal DeathTower.DeathTowerSession Tower { get; set; }

        internal bool HasBossEntranceConditionalSummon =>
            BossEntranceConditionTargets != null
            && BossEntranceConditionTargets.Count > 0
            && BossEntranceConditionalSummonCodes != null
            && BossEntranceConditionalSummonCodes.Count > 0;
    }
}
