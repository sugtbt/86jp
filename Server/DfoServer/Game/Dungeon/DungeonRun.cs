using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    // 一局副本(从选本进入到返城/断线/换角色)的全部会话内状态。
    // PlayerContext.CurrentRun 持有当前局, null 表示不在副本中:
    // 进本 new 一个实例, 结束置 null, 字段随对象一起消失 -- 不存在"漏重置"。
    // 字段默认值即"返城重置后"的取值, 与旧版逐字段清零清单一致。
    //
    // 跨局存活的状态不放这里(它们留在 PlayerContext 上):
    // - 深渊华丽挑战 UI 开关(选图界面在进本之前切换);
    // - 宠物城镇恢复锚点(副本之间持续计时);
    // - 宠物死亡定时器版本号(单调递增, 用于让过期的延迟回调失效, 归零会让旧回调复活)。
    public sealed class DungeonRun
    {
        // 组队副本联机: 击杀 relay(BroadcastMonsterDieToPartyAsync→PropagateKillForClearAsync)在【击杀者线程】
        // 写/读【队友 run】的 RoomKilledSeqIds(HashSet)与 RoomStates(Dict), 而队友自己线程也在改同一结构 →
        // 跨线程并发改集合会崩/CPU 空转/丢结算。所有对 RoomKilledSeqIds / RoomStates 的读改写都必须在此锁下,
        // 且【锁内绝不 await】(只护同步的集合访问, await 一律在锁外)。单人副本无 relay, 锁基本无竞争、开销可忽略。
        public readonly object SyncRoot = new object();

        public DungeonRun(short dungeonId, byte difficulty)
        {
            DungeonId = dungeonId;
            Difficulty = difficulty;
            Phase = DungeonRunPhase.InProgress;
            StartedUtc = DateTime.UtcNow;
        }

        // 自测用: 构造一个字段全为默认值的空局。
        public DungeonRun()
        {
        }

        public short DungeonId;
        public byte Difficulty;
        public DungeonRunPhase Phase;
        public DateTime StartedUtc;
        private readonly HashSet<(int DungeonId, int MapId)> _syncedClearMapQuestTargets =
            new HashSet<(int DungeonId, int MapId)>();

        // Per-run state for the ordinary special dungeons in PR part one.
        internal SpecialDungeonRuntime SpecialDungeon;
        public bool IgnoreDefaultDungeonClear;
        public IReadOnlyList<IReadOnlyList<(byte X, byte Y)>> SpecialMinimapIconGroups;
        internal List<MeltdownHelpusHostageAssignment> MeltdownHelpusHostages =
            new List<MeltdownHelpusHostageAssignment>();
        internal bool MeltdownHelpusBossConditionComplete;
        internal bool MeltdownHelpusBossSpawned;

        // 迷宫选择与任务连接
        public int MazeIndex = -1;
        public int LayeredMapIndex = -1;
        public bool MazeQuestConnected;
        public int MazeStartMapId;
        public int MazeStartX = -1;
        public int MazeStartY = -1;
        public int LinkedDungeonNextId;
        public int LinkedDungeonNextRate;
        public int LinkedDungeonNextCondition;

        // 赫拉斯研究所 / TimeSpiral 单局传送与结算状态。
        internal bool TimeSpiralTeleportPending;
        internal int TimeSpiralTrapMapId;
        internal bool TimeSpiralTargetActive;
        internal int TimeSpiralTargetX = -1;
        internal int TimeSpiralTargetY = -1;
        internal int TimeSpiralTargetFlag = -1;
        internal int TimeSpiralTargetWeight;
        internal bool TimeSpiralHiddenBossActive;
        internal ushort TimeSpiralHiddenBossSeqId;
        internal int TimeSpiralHiddenBossCode;
        internal int TimeSpiralHiddenBossMapId;
        internal int TimeSpiralHiddenBossX = -1;
        internal int TimeSpiralHiddenBossY = -1;
        internal string TimeSpiralHiddenBossSource;

        // 深渊(地狱派对)
        public bool HellMode;
        public byte HellPartyMode;
        public bool VeryDifficultHell;
        public bool HellGorgeousChallenge;
        public int HellMapId = -1;
        public byte HellMapX = 0xFF;
        public byte HellMapY = 0xFF;
        public GameWorld.Dungeon.HellPartyRoomInfo HellRoomInfo;

        // 当前房间与怪物追踪
        public ushort MonsterCount;
        public ushort RoomStartSequence;
        public IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> RoomMonsters
            = Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
        public HashSet<ushort> RoomKilledSeqIds = new HashSet<ushort>();
        public RoomKey RoomKey;
        public Dictionary<RoomKey, RoomState> RoomStates = new Dictionary<RoomKey, RoomState>();
        public uint Seed;
        public DnfLcg RoomLcg;
        public List<RidableObjectSpawnEntry> RidableObjects = new List<RidableObjectSpawnEntry>();

        // 通关条件与 Boss
        public ClearConditionState ClearCondition;
        public int BossCode;
        public int[] BossMapPos;
        public int SelectedBossMapId = -1;

        // 本局累计(经验/金币/统计)
        public uint TotalExp;
        public uint BossTotalExp;
        public uint ChampionTotalExp;
        public uint SuperChampionTotalExp;
        public uint NamedMonsterTotalExp;
        public uint MonsterGrowthContractBonusExp;
        public int TotalGold;

        // 本局通关后生成的秘密商店快照；随 DungeonRun 一起创建和销毁。
        internal SecretShop.SecretShopOffer SecretShopOffer;

        // 掉落物追踪
        public ushort SceneSlotCounter;
        public Dictionary<ushort, DropInfo> Drops = new Dictionary<ushort, DropInfo>();

        // 通关翻牌
        public List<ClearRewardGenerator.CardReward> CardRewards;
        public int CardFlipCount;
        public byte[] FreeCardSlots = { 0xFF, 0xFF, 0xFF, 0xFF };
        public byte[] PaidCardSlots = { 0xFF, 0xFF, 0xFF, 0xFF };
        // 翻牌奖励按免费/付费两段分别做幂等标记。
        // 自动翻免费卡、玩家手动翻牌、EPLP/再次挑战可能前后贴得很近; 发奖前必须先占用对应标记,
        // 防止后续路径把同一段奖励再次入库。
        public bool FreeCardRewardDelivered;
        public bool PaidCardRewardDelivered;

        // 死亡之塔: 塔是一局副本的变体, 塔专属状态(层数/推进状态/序号)封装在 DeathTowerSession,
        // 挂在局对象上; null=本局不是塔。
        public DeathTower.DeathTowerSession Tower;

        // 翻牌自动流程定时器句柄(结算界面 2s 布局 + 4s 自动翻免费卡)。
        // 旧服 CParty timer 使用 gen_timer_key/check_timer_key 让旧回调失效;
        // 当前项目用 ClockTimerHandle + 单局版本号表达同一语义。
        // 回调必须捕获所属 DungeonRun 实例, 并在动作前校验它仍是当前局。
        public ClockService.ClockTimerHandle AutoFlipTimerHandle;
        public int AutoFlipTimerVersion;

        public bool IsWaitingDeathRespawn;
        public DateTime DeathRespawnAvailableAt = DateTime.MinValue;
        public ClockService.ClockTimerHandle DeathRespawnTimerHandle;
        public int DeathRespawnTimerVersion;

        public ClockService.ClockTimerHandle SpecialDungeonTimerHandle;
        public int SpecialDungeonTimerVersion;

        internal bool TryMarkClearMapQuestSynced(int dungeonId, int mapId)
        {
            return _syncedClearMapQuestTargets.Add((dungeonId, mapId));
        }
    }

    internal sealed class MeltdownHelpusHostageAssignment
    {
        internal int MonsterCode { get; set; }
        internal byte X { get; set; }
        internal byte Y { get; set; }
        internal bool Rescued { get; set; }
    }
}
