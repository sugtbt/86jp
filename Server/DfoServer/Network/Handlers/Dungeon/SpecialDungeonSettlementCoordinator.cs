using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Settlement-time behavior owned by ordinary special-dungeon mechanisms.
    // Generic settlement still owns phase transitions, rewards and card flow.
    internal static class SpecialDungeonSettlementCoordinator
    {
        // S4A14 SET_PLAY_RESULT: the SeizeMoney hit counter is int32 at body + 6.
        private const int SeizeMoneyHitCountOffset = 6;

        internal static async Task OnResultPreparingAsync(
            EnhancedClientSession session,
            DungeonRun run,
            byte[] body)
        {
            var special = run?.SpecialDungeon;
            if (run == null
                || special == null
                || special.Kind != SpecialDungeonKind.SeizeMoney)
            {
                return;
            }

            var dungeonLevel = 0;
            try
            {
                dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"reason=dungeon_level error={ex.Message}");
                return;
            }

            var bossSequence = special.SeizeMoneyBossSeq;
            var bossCode = ResolveBossCode(run, bossSequence);
            if (bossCode <= 0
                || !IndependentDropSystem.TryResolveSingleFixedDropTemplate(
                    bossCode,
                    run.Difficulty,
                    dungeonLevel,
                    run.EntryPartyMemberCount,
                    out var rewardItemId,
                    out var maxDropCount))
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"boss={bossCode} bossSeq={bossSequence} " +
                    $"reason=fixed_drop_not_unique");
                return;
            }

            var config = special.Definition.SeizeMoney;
            var unitValue = Math.Max(1, config.GaugeSubOnDamage);
            var maxUnits = Math.Max(1, config.GaugeMax / unitValue);
            var hitCount = Math.Max(
                0,
                ReadInt32(body, SeizeMoneyHitCountOffset));
            var remainingUnits =
                Math.Max(0, maxUnits - Math.Min(maxUnits, hitCount));
            if (bossSequence == 0
                || !special.TryReserveSeizeMoneyClearReward(
                    remainingUnits,
                    maxDropCount,
                    out var count,
                    out var gauge))
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] SEIZE_MONEY drops skipped: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"bossSeq={bossSequence} hitCount={hitCount} " +
                    $"remainingUnits={remainingUnits} gauge={special.SeizeMoneyGauge}");
                return;
            }

            var drops = new List<DropInfo>();
            lock (run.SyncRoot)
            {
                for (var i = 0; i < count; i++)
                {
                    run.SceneSlotCounter++;
                    var drop = new DropInfo
                    {
                        SceneSlot = run.SceneSlotCounter,
                        TemplateId = (uint)rewardItemId,
                        StackCount = 1,
                    };
                    drops.Add(drop);
                    run.Drops[drop.SceneSlot] = drop;
                }
            }

            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.DIE_MONSTER,
                DungeonNotificationBuilder.BuildMonsterDie(
                    bossSequence,
                    drops,
                    session.Player.UserId)));
            FileLogger.Log(
                $"[SpecialDungeonModule] SEIZE_MONEY drops sent: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"boss={bossCode} bossSeq={bossSequence} " +
                $"item={rewardItemId} count={count}/{maxDropCount} " +
                $"hitCount={hitCount} remainingUnits={remainingUnits} " +
                $"gauge={gauge}/{config.GaugeMax}");
        }

        private static int ResolveBossCode(DungeonRun run, ushort bossSequence)
        {
            if (run.BossCode > 0)
                return run.BossCode;
            if (bossSequence == 0)
                return 0;

            lock (run.SyncRoot)
            {
                var localIndex = (int)bossSequence - run.RoomStartSequence;
                if (run.RoomMonsters == null
                    || localIndex < 0
                    || localIndex >= run.RoomMonsters.Count)
                {
                    return 0;
                }

                return run.RoomMonsters[localIndex].Code;
            }
        }

        private static int ReadInt32(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 4 > body.Length)
                return 0;

            return BitConverter.ToInt32(body, offset);
        }
    }
}
