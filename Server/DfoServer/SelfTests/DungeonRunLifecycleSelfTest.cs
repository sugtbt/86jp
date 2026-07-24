using System;
using System.Net.Sockets;
using System.Reflection;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    // 锁定 DungeonRun 状态模型的关键语义:
    // 1. 新局字段默认值 = 旧版"返城重置后"的取值(常量表);
    // 2. Begin/End 生命周期与幂等性;
    // 3. 跨局字段(华丽挑战开关)不随 run 重建;
    // 4. 翻牌定时器句柄随局取消, 换局时旧句柄必被取消。
    public static class DungeonRunLifecycleSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_RUN_LIFECYCLE selftest ===");
            var failures = 0;

            using var client = new TcpClient();
            var session = new EnhancedClientSession(client, new GamePacketHeader());
            var player = session.Player;

            // 1. 初始无局; 塔便捷入口跟随
            Check("fresh session has no run",
                player.CurrentRun == null
                && player.DeathTowerState == null
                && !player.IsInDeathTower,
                ref failures);
            // 2. 新局字段默认值 = 旧版返城重置后的取值(常量表)
            var fresh = new DungeonRun();
            Check("fresh run fields carry legacy reset defaults",
                fresh.DungeonId == 0
                && fresh.Phase == DungeonRunPhase.None
                && fresh.MazeIndex == -1
                && fresh.LayeredMapIndex == -1
                && fresh.MazeStartX == -1
                && fresh.MazeStartY == -1
                && !fresh.HellMode
                && fresh.HellMapId == -1
                && fresh.HellMapX == 0xFF
                && fresh.HellMapY == 0xFF
                && fresh.RoomMonsters.Count == 0
                && fresh.RoomKilledSeqIds.Count == 0
                && fresh.RoomStates.Count == 0
                && fresh.Drops.Count == 0
                && fresh.CardRewards == null
                && fresh.FreeCardSlots.Length == 4 && fresh.FreeCardSlots[0] == 0xFF
                && fresh.PaidCardSlots[3] == 0xFF
                && !fresh.IsWaitingDeathRespawn
                && fresh.DeathRespawnAvailableAt == DateTime.MinValue
                && fresh.Tower == null,
                ref failures);

            CheckTowerSettlementPolicy(ref failures);
            // 3. BeginRun 建立新局
            DungeonRunLifecycle.BeginRun(session, 1002, 1);
            var run = player.CurrentRun;
            Check("BeginRun creates run with entry params",
                run != null
                && run.DungeonId == 1002
                && run.Difficulty == 1
                && run.Phase == DungeonRunPhase.InProgress,
                ref failures);

            var markerRun = new DungeonRun(1002, 0);
            Check("clear-map quest sync marker deduplicates by dungeon and map",
                markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && !markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && markerRun.TryMarkClearMapQuestSynced(0, 33061)
                && markerRun.TryMarkClearMapQuestSynced(1002, 33060),
                ref failures);

            // 4. 跨局字段不随 run 重建
            player.HellPartyGorgeousChallengeEnabled = true;
            DungeonRunLifecycle.EndRunOnTeardown(session, "selftest");
            Check("teardown clears run",
                player.CurrentRun == null, ref failures);
            Check("cross-run fields survive teardown",
                player.HellPartyGorgeousChallengeEnabled,
                ref failures);

            // 5. 无局时 End 幂等不抛
            var idempotentOk = true;
            try
            {
                DungeonRunLifecycle.EndRunOnTeardown(session, "selftest-again");
                DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            }
            catch { idempotentOk = false; }
            Check("End without run is idempotent", idempotentOk, ref failures);

            // 6. 翻牌定时器句柄: 取消置空 + 换局时旧句柄必被取消
            DungeonRunLifecycle.BeginRun(session, 1002, 0);
            var firstRun = player.CurrentRun;
            var handle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var versionBeforeCancel = firstRun.AutoFlipTimerVersion;
            firstRun.AutoFlipTimerHandle = handle;
            DungeonRunLifecycle.CancelAutoFlip(session);
            Check("CancelAutoFlip cancels and clears the handle",
                firstRun.AutoFlipTimerHandle == null
                && firstRun.AutoFlipTimerVersion == versionBeforeCancel + 1
                && !handle.Cancel(),
                ref failures);

            var deathHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:death-respawn:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var deathVersionBeforeCancel = firstRun.DeathRespawnTimerVersion;
            firstRun.IsWaitingDeathRespawn = true;
            firstRun.DeathRespawnAvailableAt = DateTime.UtcNow.AddHours(1);
            firstRun.DeathRespawnTimerHandle = deathHandle;
            DungeonRunLifecycle.CancelDeathRespawn(session);
            Check("CancelDeathRespawn cancels and clears the handle",
                firstRun.DeathRespawnTimerHandle == null
                && firstRun.DeathRespawnTimerVersion == deathVersionBeforeCancel + 1
                && !firstRun.IsWaitingDeathRespawn
                && firstRun.DeathRespawnAvailableAt == DateTime.MinValue
                && !deathHandle.Cancel(),
                ref failures);

            var staleHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var staleDeathHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:death-respawn:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            firstRun.AutoFlipTimerHandle = staleHandle;
            firstRun.IsWaitingDeathRespawn = true;
            firstRun.DeathRespawnAvailableAt = DateTime.UtcNow.AddHours(1);
            firstRun.DeathRespawnTimerHandle = staleDeathHandle;
            DungeonRunLifecycle.BeginRun(session, 1003, 0);
            Check("BeginRun cancels the previous run timer and swaps the run",
                !staleHandle.Cancel()
                && !staleDeathHandle.Cancel()
                && !ReferenceEquals(player.CurrentRun, firstRun)
                && player.CurrentRun.DungeonId == 1003,
                ref failures);

            // 7. 塔局: 挂 Tower 载荷, 返城随局消失
            var tower = new Game.DeathTower.DeathTowerSession(new Game.DeathTower.DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 3,
                StageMapIds = new[] { 1, 2, 3 },
                BasisLevel = 50,
            });
            DungeonRunLifecycle.BeginTowerRun(session, 11000, tower);
            tower.BeginStage(123, new[]
            {
                new Game.DeathTower.StageTowerItem
                {
                    SourceMonsterUniqueId = 7,
                    ItemUniqueId = 9,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });
            tower.GenerateDropsForMonster(7);
            tower.TryPickupGroundItem(9, out _);
            Check("BeginTowerRun mounts tower payload",
                player.IsInDeathTower
                && ReferenceEquals(player.DeathTowerState, tower)
                && player.CurrentRun.DungeonId == 11000
                && tower.InventoryItems.Count == 1,
                ref failures);

            DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            Check("EndRunToTown clears run and tower",
                player.CurrentRun == null && !player.IsInDeathTower, ref failures);

            var replacementTower = CreateTowerWithPickedItem();
            DungeonRunLifecycle.BeginTowerRun(session, 11000, replacementTower);
            DungeonRunLifecycle.BeginRun(session, 1002, 0);
            Check("starting another dungeon discards tower inventory with the old run",
                player.CurrentRun != null
                    && player.CurrentRun.Tower == null
                    && player.CurrentRun.DungeonId == 1002,
                ref failures);

            var teardownTower = CreateTowerWithPickedItem();
            DungeonRunLifecycle.BeginTowerRun(session, 11000, teardownTower);
            DungeonRunLifecycle.EndRunOnTeardown(session, "tower-selftest");
            Check("disconnect or character teardown discards tower inventory",
                player.CurrentRun == null && !player.IsInDeathTower,
                ref failures);

            var staleTower = CreateTowerWithPickedItem();
            var freshTower = new Game.DeathTower.DeathTowerSession(staleTower.Config);
            DungeonRunLifecycle.BeginTowerRun(session, 11000, staleTower);
            DungeonRunLifecycle.BeginTowerRun(session, 11000, freshTower);
            Check("starting a new tower run replaces the old temporary inventory",
                ReferenceEquals(player.DeathTowerState, freshTower)
                    && freshTower.InventoryItems.Count == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static Game.DeathTower.DeathTowerSession CreateTowerWithPickedItem()
        {
            var tower = new Game.DeathTower.DeathTowerSession(new Game.DeathTower.DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 1,
                StageMapIds = new[] { 1 },
                BasisLevel = 50,
            });
            tower.BeginStage(456, new[]
            {
                new Game.DeathTower.StageTowerItem
                {
                    SourceMonsterUniqueId = 10,
                    ItemUniqueId = 11,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });
            tower.GenerateDropsForMonster(10);
            if (!tower.TryPickupGroundItem(11, out _))
                throw new InvalidOperationException("tower lifecycle fixture pickup failed");
            return tower;
        }

        private static void CheckTowerSettlementPolicy(ref int failures)
        {
            var paidPolicy = typeof(DungeonSettlementHandler).GetMethod(
                "ShouldGeneratePaidCardRewards",
                BindingFlags.Static | BindingFlags.NonPublic);
            Check("tower settlement has a dedicated paid-card policy",
                paidPolicy != null, ref failures);
            if (paidPolicy != null)
            {
                Check("tower of despair does not generate an invisible paid-card reward",
                    !(bool)paidPolicy.Invoke(null, new object[] { 11008 }), ref failures);
                Check("ordinary dungeons retain paid-card rewards",
                    (bool)paidPolicy.Invoke(null, new object[] { 1002 }), ref failures);
            }

            var builder = typeof(DungeonSettlementHandler).GetMethod(
                "TryBuildTowerOfDespairClearRewardWithTime",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(int), typeof(uint), typeof(int), typeof(int),
                    typeof(byte[]).MakeByRefType()
                },
                null);
            Check("tower settlement derives the displayed floor from the cleared dungeon",
                builder != null, ref failures);
            if (builder == null)
                return;

            const int rewardItemId = 2600001;
            var args = new object[] { 11013, 15750u, rewardItemId, 1, null };
            var built = (bool)builder.Invoke(null, args);
            var body = args[4] as byte[];
            Check("tower clear packet matches the client 015C wire layout",
                built
                && body != null
                && body.Length == 15
                && BitConverter.ToUInt32(body, 0) == 15750u
                && BitConverter.ToUInt16(body, 4) == 6
                && body[6] == 1
                && BitConverter.ToUInt32(body, 7) == rewardItemId
                && BitConverter.ToUInt32(body, 11) == 1u,
                ref failures);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
