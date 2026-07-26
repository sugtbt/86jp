using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // 一局副本的生命周期唯一入口。
    // 真相载体是 PlayerContext.CurrentRun: 进本 new 一个 DungeonRun, 结束置 null,
    // 单局字段随对象消失 -- 全仓不再有逐字段重置清单。
    internal static class DungeonRunLifecycle
    {
        // 进本: 掐掉旧局残留定时器 -> 换新局。
        internal static void BeginRun(EnhancedClientSession session, int dungeonId, byte difficulty)
        {
            var towerItemIds = CaptureTowerItemIds(session);
            CancelAutoFlip(session);
            CancelDeathRespawn(session);
            DungeonMechanismCoordinator.CancelRunTimers(session);
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);

            session.Player.CurrentRun = new DungeonRun((short)dungeonId, difficulty);
            DungeonMechanismCoordinator.OnRunCreated(session, dungeonId, "begin_run");
            RecalibrateTowerQuestOverlayWithoutNotification(session, towerItemIds);
            PetCreatureRuntimeService.BeginDungeon(session, dungeonId, "begin_run");
        }

        // 进塔: 塔是一局副本的变体, 同样换新局(顺带丢弃上一局的全部残留状态)。
        internal static void BeginTowerRun(
            EnhancedClientSession session,
            int dungeonId,
            Game.DeathTower.DeathTowerSession tower,
            byte difficulty = 0)
        {
            var towerItemIds = CaptureTowerItemIds(session);
            CancelAutoFlip(session);
            CancelDeathRespawn(session);
            DungeonMechanismCoordinator.CancelRunTimers(session);

            session.Player.CurrentRun = new DungeonRun((short)dungeonId, difficulty);
            session.Player.CurrentRun.Tower = tower;
            RecalibrateTowerQuestOverlayWithoutNotification(session, towerItemIds);
            PetCreatureRuntimeService.BeginDungeon(session, dungeonId, "begin_tower_run");
        }

        // 返城(EPLP/回城/放弃): 先掐定时器(残留的翻牌定时器不能对下一局或城镇状态发包), 再丢弃本局。
        internal static async Task EndRunToTownAsync(EnhancedClientSession session)
        {
            var tower = session?.Player?.CurrentRun?.Tower;
            var towerItemIds = tower != null
                ? new List<int>(tower.SeenItemIds)
                : null;
            CancelAutoFlip(session);
            CancelDeathRespawn(session);
            DungeonMechanismCoordinator.CancelRunTimers(session);
            PersistSessionExp(session, "town");
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);
            await DungeonMechanismCoordinator.ClearRunEffectsAsync(session, "town");
            await PetCreatureRuntimeService.EndDungeonToTownAsync(session, "town");

            session.Player.DungeonSceneUniqueId = 0;
            session.Player.CurrentRun = null;
            if (towerItemIds != null
                && towerItemIds.Count > 0
                && session.GameSession?.QuestManager != null)
            {
                await session.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(towerItemIds);
            }
        }

        // 断线/换角色: 同样丢弃本局。
        // 换角色时必须丢弃当前局 -- PlayerContext 实例跨角色复用, 不丢会把上个角色的副本状态带给下个角色。
        internal static void EndRunOnTeardown(EnhancedClientSession session, string source)
        {
            var towerItemIds = CaptureTowerItemIds(session);
            LinkedDungeonEntryAuthorizationStore.Clear(session?.Player);
            CancelAutoFlip(session);
            CancelDeathRespawn(session);
            DungeonMechanismCoordinator.CancelRunTimers(session);
            PersistSessionExp(session, source);
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);
            PetCreatureRuntimeService.EndCharacterSession(session, source);

            session.Player.DungeonSceneUniqueId = 0;
            session.Player.CurrentRun = null;
            RecalibrateTowerQuestOverlayWithoutNotification(session, towerItemIds);
        }

        private static List<int> CaptureTowerItemIds(EnhancedClientSession session)
        {
            var tower = session?.Player?.CurrentRun?.Tower;
            return tower == null ? null : new List<int>(tower.SeenItemIds);
        }

        private static void RecalibrateTowerQuestOverlayWithoutNotification(
            EnhancedClientSession session,
            ICollection<int> towerItemIds)
        {
            if (towerItemIds == null
                || towerItemIds.Count == 0
                || session?.GameSession?.QuestManager == null)
            {
                return;
            }

            try
            {
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressWithoutNotification(towerItemIds);
            }
            catch (System.Exception ex)
            {
                FileLogger.Log($"[DungeonRunLifecycle] ERROR: tower quest rollback failed: cid={session.Player?.CharacterId ?? 0}: {ex.Message}");
            }
        }

        // 离开一局时把会话内存的等级/经验落库(实现收口在经验系统,
        // 这里只保留"仍在一局中才需要兜底"的副本生命周期判断)。
        private static void PersistSessionExp(EnhancedClientSession session, string source)
        {
            var player = session?.Player;
            if (player == null || player.CurrentRun == null)
                return;

            Game.Progression.CharacterExperienceService.PersistSessionExp(player, source);
        }

        // 取消当前局的翻牌自动流程定时器(结算界面 2s 布局 + 4s 自动翻免费卡)。
        // 先推进版本号, 让已经出队但还在异步发包的旧回调失效; 再取消仍挂在 ClockService 中的句柄。
        internal static void CancelAutoFlip(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            Interlocked.Increment(ref run.AutoFlipTimerVersion);
            var handle = Interlocked.Exchange(ref run.AutoFlipTimerHandle, null);
            handle?.Cancel();
        }

        internal static void CancelDeathRespawn(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            run.IsWaitingDeathRespawn = false;
            run.DeathRespawnAvailableAt = System.DateTime.MinValue;
            Interlocked.Increment(ref run.DeathRespawnTimerVersion);
            var handle = Interlocked.Exchange(ref run.DeathRespawnTimerHandle, null);
            handle?.Cancel();
        }

    }
}
