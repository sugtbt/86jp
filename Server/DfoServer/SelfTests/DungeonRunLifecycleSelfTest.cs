using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;

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
            CheckTowerRewardGrantPersistence(ref failures);
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

            var cardFlowPolicy = typeof(DungeonSettlementHandler).GetMethod(
                "ShouldScheduleCardRewardFlow",
                BindingFlags.Static | BindingFlags.NonPublic);
            Check("tower settlement exposes the standard card-flow policy",
                cardFlowPolicy != null, ref failures);
            if (cardFlowPolicy != null)
            {
                Check("tower of despair skips the delayed card layout and auto-flip",
                    !(bool)cardFlowPolicy.Invoke(null, new object[] { 11008 }), ref failures);
                Check("ordinary dungeons retain delayed card layout and auto-flip",
                    (bool)cardFlowPolicy.Invoke(null, new object[] { 1002 }), ref failures);
            }

            var rewardFactory = typeof(DungeonSettlementHandler).GetMethod(
                "BuildTowerOfDespairRewardCandidates",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(int),
                    typeof(Func<ClearRewardGenerator.CardReward>)
                },
                null);
            Check("tower settlement exposes the original ten-slot reward policy",
                rewardFactory != null, ref failures);
            if (rewardFactory != null)
            {
                var nextItemId = 2600000;
                var randomFactoryCallCount = 0;
                Func<ClearRewardGenerator.CardReward> randomReward = () =>
                    {
                        randomFactoryCallCount++;
                        return new ClearRewardGenerator.CardReward
                        {
                            ItemId = ++nextItemId,
                            StackCount = 1,
                        };
                    };
                var floor16 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 16, randomReward });
                var floor16FactoryCalls = randomFactoryCallCount;
                randomFactoryCallCount = 0;
                var floor10 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 10, randomReward });
                var floor10FactoryCalls = randomFactoryCallCount;
                randomFactoryCallCount = 0;
                var floor100 = (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(null, new object[] { 100, randomReward });
                var floor100FactoryCalls = randomFactoryCallCount;

                Check("ordinary despair floor rolls five item rewards",
                    floor16FactoryCalls == 5
                    && floor16.Count == 5,
                    ref failures);
                Check("player-mirror despair floor rolls nine items plus the synthesizer",
                    floor10FactoryCalls == 9
                    && floor10.Count == 10
                    && floor10[9].ItemId == 1252
                    && floor10[9].StackCount == 1,
                    ref failures);
                Check("floor 100 rolls five items plus the completion medal",
                    floor100FactoryCalls == 5
                    && floor100.Count == 6
                    && floor100[5].ItemId == 3314
                    && floor100[5].StackCount == 1,
                    ref failures);

                var fallbackFactoryCalls = 0;
                Func<ClearRewardGenerator.CardReward> goldFallback = () =>
                {
                    fallbackFactoryCalls++;
                    return new ClearRewardGenerator.CardReward
                    {
                        IsGold = true,
                        GoldAmount = 123,
                    };
                };
                var fallbackRewards =
                    (IReadOnlyList<ClearRewardGenerator.CardReward>)
                    rewardFactory.Invoke(
                        null,
                        new object[] { 16, goldFallback });
                Check("invalid item fallbacks remain empty instead of becoming gold",
                    fallbackFactoryCalls == 5
                    && fallbackRewards.Count == 0,
                    ref failures);
            }

            var builder = typeof(DungeonSettlementHandler).GetMethod(
                "TryBuildTowerOfDespairClearRewardWithTime",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(int), typeof(uint),
                    typeof(IReadOnlyList<ClearRewardGenerator.CardReward>),
                    typeof(byte[]).MakeByRefType()
                },
                null);
            Check("tower settlement derives the displayed floor from the cleared dungeon",
                builder != null, ref failures);
            if (builder == null)
                return;

            var rewards = new List<ClearRewardGenerator.CardReward>();
            for (var i = 0; i < 5; i++)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 2600001 + i,
                    StackCount = i + 1,
                });
            }
            var args = new object[] { 11013, 15750u, rewards, null };
            var built = (bool)builder.Invoke(null, args);
            var body = args[3] as byte[];
            Check("tower clear packet keeps the original fixed ten-slot wire layout",
                built
                && body != null
                && body.Length == 87
                && BitConverter.ToUInt32(body, 0) == 15750u
                && BitConverter.ToUInt16(body, 4) == 6
                && body[6] == 10
                && BitConverter.ToInt32(body, 7) == 2600001
                && BitConverter.ToInt32(body, 11) == 1
                && BitConverter.ToInt32(body, 39) == 2600005
                && BitConverter.ToInt32(body, 43) == 5
                && BitConverter.ToInt32(body, 47) == -1
                && BitConverter.ToInt32(body, 51) == 0
                && BitConverter.ToInt32(body, 79) == -1
                && BitConverter.ToInt32(body, 83) == 0,
                ref failures);
        }

        private static void CheckTowerRewardGrantPersistence(ref int failures)
        {
            const int accountId = 970021;
            const int characterId = 970121;
            const int synthesizerItemId = 1252;
            const int completionMedalItemId = 3314;
            var tempDb = Path.Combine(
                Path.GetTempPath(),
                $"tower-of-despair-reward-{Guid.NewGuid():N}.db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    tempDb,
                    ServerPaths.SchemaFilePath);
                SeedTowerRewardOwners(
                    connectionString,
                    new[] { (accountId, characterId) });
                InventoryService inventory;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId);
                }

                var lease = new InventoryLease(
                    Guid.NewGuid(),
                    characterId,
                    inventory,
                    version: 1);
                var candidates = new List<ClearRewardGenerator.CardReward>
                {
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = synthesizerItemId,
                        StackCount = 1,
                    },
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = completionMedalItemId,
                        StackCount = 1,
                    },
                };

                var service = new TowerOfDespairRewardGrantService();
                var successful = service.Grant(inventory, candidates);
                Check("tower reward grant uses the online inventory batch and reports actual changed slots",
                    successful.Count == 2
                    && successful[0].Reward.ItemId == synthesizerItemId
                    && successful[1].Reward.ItemId == completionMedalItemId
                    && successful[0].ListType == InventoryListType.Main
                    && successful[0].Slot >= InventoryService.MainSlotStart
                    && successful[1].ListType == InventoryListType.Main
                    && successful[1].Slot >= InventoryService.MainSlotStart
                    && inventory.CountMainItem(synthesizerItemId) == 1
                    && inventory.CountMainItem(completionMedalItemId) == 1,
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease);
                        transaction.Commit();
                    }
                }
                inventory.ClearDirtyState();

                InventoryService reloaded;
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    reloaded = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId);
                }
                Check("tower reward online-inventory mutations persist through the shared inventory persistence path",
                    reloaded.CountMainItem(synthesizerItemId) == 1
                    && reloaded.CountMainItem(completionMedalItemId) == 1,
                    ref failures);

                var rejectedInventory = new InventoryService(
                    characterId + 1,
                    accountId + 1);
                var rejected = service.Grant(
                    rejectedInventory,
                    new[]
                    {
                        candidates[0],
                        new ClearRewardGenerator.CardReward
                        {
                            ItemId = int.MaxValue,
                            StackCount = 1,
                        },
                    });
                Check("unsupported tower reward rejects the whole planned batch without partial inventory mutation",
                    rejected.Count == 0
                    && rejectedInventory.CountMainItem(synthesizerItemId) == 0,
                    ref failures);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteTempDatabase(tempDb);
            }
        }

        private static void SeedTowerRewardOwners(
            string connectionString,
            IReadOnlyList<(int AccountId, int CharacterId)> owners)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                foreach (var owner in owners)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @memberId, '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, @aid, @name);";
                        command.Parameters.AddWithValue("@aid", owner.AccountId);
                        command.Parameters.AddWithValue(
                            "@memberId",
                            $"tower-reward-{owner.AccountId}");
                        command.Parameters.AddWithValue("@cid", owner.CharacterId);
                        command.Parameters.AddWithValue(
                            "@name",
                            $"tower-reward-{owner.CharacterId}");
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
