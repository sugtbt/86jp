using System;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        // 副本状态真相: 一局一个 DungeonRun 对象, null = 不在副本中。
        // 进本由 DungeonRunLifecycle.BeginRun/BeginTowerRun 置换新实例,
        // 返城/断线/换角色置 null -- 单局字段随对象消失, 不存在漏重置。
        public Game.Dungeon.DungeonRun CurrentRun { get; internal set; }

        // One-shot linked-dungeon authorization survives the result-screen town
        // transition, but is never persisted and is cleared on character teardown.
        internal object LinkedDungeonEntryAuthorizationSyncRoot { get; } = new object();
        internal Game.Dungeon.LinkedDungeonEntryAuthorization
            PendingLinkedDungeonEntryAuthorization { get; set; }

        // ---- 跨局存活字段(刻意不随 run 重建) ----

        // 深渊华丽挑战 UI 开关: 在选图界面(进本之前)切换。
        public bool HellPartyGorgeousChallengeEnabled { get; set; }

        // Pet satiety runtime anchors. DungeonRun is recreated per run, but town recovery
        // spans the time between runs, so these stay on the player context.
        public DateTime PetCreatureSatietyDungeonStartUtc { get; set; } = DateTime.MinValue;
        public short PetCreatureSatietyDungeonId { get; set; }
        public DateTime PetCreatureSatietyTownStartUtc { get; set; } = DateTime.MinValue;
        public int PetCreatureLastDeathCreatureKey { get; set; }
        public int PetCreatureDeathTimerVersion { get; set; }

        public ushort DungeonSceneUniqueId { get; set; }
    }
}
