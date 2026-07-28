using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class DungeonRewardPolicySelfTest
    {
        private static readonly string[] QuestTrainingDungeonPaths =
        {
            "dungeon/quest/training.dgn",
            "dungeon/quest/training2.dgn",
            "dungeon/quest/training_asura.dgn",
            "dungeon/quest/training_striker.dgn",
            "dungeon/quest/training_weaponmaster.dgn",
        };

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_REWARD_POLICY selftest ===");
            var failures = 0;

            var trainingEntries = GameWorld.Dungeon.LoadDungeonLstFile().Entries
                .Where(entry => DungeonRewardPolicyData.IsInteractiveTrainingAssetPath(
                    entry.FilePath))
                .ToList();
            Check(
                "dungeon.lst exposes one interactive training-room asset",
                trainingEntries.Count == 1,
                ref failures);
            if (trainingEntries.Count != 1)
                return failures == 0 ? 1 : failures;

            var trainingEntry = trainingEntries[0];
            var loaded = GameWorld.Dungeon.LoadDungeonFileWithPath(trainingEntry.Id);
            Check(
                "interactive training-room path and DGN structure agree",
                DungeonRewardPolicyData.IsInteractiveTrainingConfiguration(
                    loaded.FilePath,
                    loaded.File),
                ref failures);

            var trainingPolicy = DungeonRewardPolicyData.Resolve(trainingEntry.Id);
            Check(
                "interactive training-room policy disables rewards and progression",
                trainingPolicy.Kind == DungeonRewardPolicyKind.InteractiveTraining
                && !trainingPolicy.AllowsMonsterExperience
                && !trainingPolicy.AllowsMonsterDrops
                && !trainingPolicy.AllowsQuestDrops
                && !trainingPolicy.AllowsQuestProgress
                && !trainingPolicy.AllowsPetExperience
                && !trainingPolicy.AllowsClearCommit
                && !trainingPolicy.AllowsSettlement,
                ref failures);

            var questTrainingFilesRemainStandard = true;
            foreach (var path in QuestTrainingDungeonPaths)
            {
                var dungeonFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(path));
                if (DungeonRewardPolicyData.IsInteractiveTrainingAssetPath(path)
                    || DungeonRewardPolicyData.IsInteractiveTrainingConfiguration(
                        path,
                        dungeonFile))
                {
                    questTrainingFilesRemainStandard = false;
                    break;
                }
            }
            Check(
                "quest training dungeons are not interactive no-reward rooms",
                questTrainingFilesRemainStandard,
                ref failures);

            var standard = DungeonRewardPolicy.Standard;
            Check(
                "standard policy keeps all existing reward and progression paths",
                standard.Kind == DungeonRewardPolicyKind.Standard
                && standard.AllowsMonsterExperience
                && standard.AllowsMonsterDrops
                && standard.AllowsQuestDrops
                && standard.AllowsQuestProgress
                && standard.AllowsPetExperience
                && standard.AllowsClearCommit
                && standard.AllowsSettlement,
                ref failures);

            var sharedInstance = new DungeonInstance(
                checked((short)trainingEntry.Id),
                0,
                trainingPolicy);
            var leaderRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var memberRun = new DungeonRun(
                sharedInstance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            Check(
                "party participants share the frozen physical-instance policy",
                ReferenceEquals(leaderRun.Instance, memberRun.Instance)
                && ReferenceEquals(leaderRun.RewardPolicy, memberRun.RewardPolicy)
                && leaderRun.RewardPolicy.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining,
                ref failures);

            using var client = new TcpClient();
            var session = new EnhancedClientSession(
                client,
                new GamePacketHeader());
            DungeonRunLifecycle.BeginRun(
                session,
                trainingEntry.Id,
                difficulty: 0);
            Check(
                "normal run lifecycle freezes the resolved training policy",
                session.Player.CurrentRun != null
                && session.Player.CurrentRun.DungeonId == trainingEntry.Id
                && session.Player.CurrentRun.RewardPolicy.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_REWARD_POLICY selftest passed."
                    : $"DUNGEON_REWARD_POLICY selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
