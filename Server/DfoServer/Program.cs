using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer
{
    internal class Program
    {
        // 自测注册表: 新增自测在这里加一行, 单跑参数与 --selftest-all 都会覆盖到。
        private static readonly (string Arg, Func<int> Run)[] SelfTestRegistry =
        {
            ("--selftest-avatar-compound", SelfTests.AvatarCompoundSelfTest.Run),
            ("--selftest-cerashop", SelfTests.CeraShopSelfTest.Run),
            ("--selftest-pet-consumable", SelfTests.PetConsumableSelfTest.Run),
            ("--selftest-titlebook-item-codec", SelfTests.LegacyTitleBookItemCodecSelfTest.Run),
            ("--selftest-npc-material-exchange-price", SelfTests.NpcMaterialExchangePriceSelfTest.Run),
            ("--selftest-collectbox-runtime", SelfTests.CollectBoxRuntimeSelfTest.Run),
            ("--selftest-lottery-item", SelfTests.LotteryItemSelfTest.Run),
            ("--selftest-dungeon-map-fallback", SelfTests.DungeonMapFallbackSelfTest.Run),
            ("--selftest-tower-of-despair-progress", SelfTests.TowerOfDespairProgressSelfTest.Run),
            ("--selftest-dungeon-room-progress", SelfTests.DungeonRoomProgressSelfTest.Run),
            ("--selftest-dungeon-run", SelfTests.DungeonRunLifecycleSelfTest.Run),
            ("--selftest-special-dungeon", SelfTests.SpecialDungeonSelfTest.Run),
            ("--selftest-special-dungeon-part2", SelfTests.SpecialDungeonPart2SelfTest.Run),
            ("--selftest-special-dungeon-part3", SelfTests.SpecialDungeonPart3SelfTest.Run),
            ("--selftest-card-reward-flow", SelfTests.CardRewardFlowSelfTest.Run),
            ("--selftest-monster-card-drop", SelfTests.MonsterCardDropSelfTest.Run),
            ("--selftest-character-option", SelfTests.CharacterOptionSelfTest.Run),
            ("--selftest-crystal-contract", SelfTests.CrystalContractSelfTest.Run),
            ("--selftest-slot-expansion-quest", SelfTests.SlotExpansionQuestSelfTest.Run),
            ("--selftest-character-slot-policy", SelfTests.CharacterSlotPolicySelfTest.Run),
            ("--selftest-knight-shield-deck", SelfTests.KnightShieldDeckRepositorySelfTest.Run),
            ("--selftest-clear-map-quest", SelfTests.ClearMapQuestSelfTest.Run),
            ("--selftest-death-tower-map-loader", SelfTests.DeathTowerMapLoaderSelfTest.Run),
            ("--selftest-death-tower-drop", SelfTests.DeathTowerDropSelfTest.Run),
            ("--selftest-death-tower-protocol", SelfTests.DeathTowerProtocolSelfTest.Run),
            ("--selftest-death-tower-quest-routing", SelfTests.DeathTowerQuestRoutingSelfTest.Run),
            ("--selftest-quest-clear", SelfTests.QuestClearSelfTest.Run),
            ("--selftest-quest-trigger-counts", SelfTests.QuestTriggerCountSelfTest.Run),
            ("--selftest-quest-ack-format", SelfTests.QuestAckFormatSelfTest.Run),
            ("--selftest-clear-quest-list-packet", SelfTests.ClearQuestListPacketSelfTest.Run),
            ("--selftest-special-reward-quest-source", SelfTests.SpecialRewardQuestSourceSelfTest.Run),
            ("--selftest-question-quest-branch", SelfTests.QuestionQuestBranchSelfTest.Run),
            ("--selftest-striker-skill", SelfTests.StrikerSkillSelfTest.Run),
            ("--selftest-pet-equipment", SelfTests.PetEquipmentSelfTest.Run),
            ("--selftest-pet-hatch", SelfTests.PetHatchSelfTest.Run),
            ("--selftest-gold-limit", SelfTests.GoldLimitSelfTest.Run),
            ("--selftest-daily-reset", SelfTests.DailyResetSelfTest.Run),
            ("--selftest-revive-coin", SelfTests.ReviveCoinSelfTest.Run),
            ("--selftest-clock", SelfTests.ClockSelfTest.Run),
            ("--selftest-rental-info", SelfTests.RentalInfoSelfTest.Run),
            ("--selftest-honor-level", SelfTests.HonorLevelSelfTest.Run),
            ("--selftest-character-experience-progression", SelfTests.CharacterExperienceProgressionSelfTest.Run),
            ("--selftest-party", SelfTests.PartySelfTest.Run),
            ("--selftest-dungeon-combat-party", SelfTests.DungeonCombatPartySelfTest.Run),
            ("--selftest-udp-relay", SelfTests.UdpRelaySelfTest.Run),
            ("--selftest-growth-capsule", SelfTests.GrowthCapsuleSelfTest.Run),
        };

        // 顺序跑全部自测, 输出汇总表; 任一失败(或抛异常)退出码为 1。
        private static int RunAllSelfTests()
        {
            var failed = new List<string>();
            foreach (var entry in SelfTestRegistry)
            {
                var name = entry.Arg.Substring("--selftest-".Length);
                Console.WriteLine($"===== [{name}] =====");
                int code;
                try
                {
                    code = entry.Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{name}] EXCEPTION: {ex.Message}");
                    code = 1;
                }
                if (code != 0)
                    failed.Add(name);
            }

            Console.WriteLine("===== SELFTEST SUMMARY =====");
            Console.WriteLine($"total={SelfTestRegistry.Length} pass={SelfTestRegistry.Length - failed.Count} fail={failed.Count}");
            foreach (var name in failed)
                Console.WriteLine($"FAIL: {name}");
            return failed.Count == 0 ? 0 : 1;
        }

        private static int RebuildInventoryNewItems()
        {
            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
                GameWorld.PvfArchiveAccessor.ReadText("character/character.lst");

                var connectionString = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                    Infrastructure.ServerPaths.DatabasePath,
                    Infrastructure.ServerPaths.SchemaFilePath);
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
                {
                    connection.Open();
                    Game.Inventory.InventoryNewItemMigrationService.Migrate(connection);
                }

                Console.WriteLine($"[InventoryMigration] rebuilt new inventory tables from legacy tables: {Infrastructure.ServerPaths.DatabasePath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[InventoryMigration] rebuild failed: {ex}");
                return 1;
            }
        }

        static void Main(string[] args)
        {
            args ??= Array.Empty<string>();

            if (Array.IndexOf(args, "--rebuild-inventory-new-items") >= 0)
            {
                Environment.Exit(RebuildInventoryNewItems());
                return;
            }

            if (Array.IndexOf(args, "--selftest-all") >= 0)
            {
                Environment.Exit(RunAllSelfTests());
                return;
            }

            foreach (var entry in SelfTestRegistry)
            {
                if (Array.IndexOf(args, entry.Arg) >= 0)
                {
                    Environment.Exit(entry.Run());
                    return;
                }
            }

            if (Array.IndexOf(args, "--probe-skill-state") >= 0 || Array.IndexOf(args, "--probe-skill-csv") >= 0)
            {
                Environment.Exit(SelfTests.SkillStateProbe.Run(args));
                return;
            }

            GameNetworkConfig.Configure(args);

            PacketFileLogger.Initialize();
            if (GameNetworkConfig.PacketCaptureEnabled)
                Console.WriteLine("[PacketCapture] ENABLED – all SEND/RECV packets logged to packet_log.txt");

            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (System.IO.FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Script.pvf not found.");
                Console.WriteLine("Please place Script.pvf in Data/Pvf/Script.pvf, or set the PVF_ARCHIVE_PATH environment variable.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            Console.Write("Loading Script.pvf... ");
            try
            {
                GameWorld.PvfArchiveAccessor.ReadText("character/character.lst");
                Game.Mercenary.StrikerSkillDataProvider.Warmup();
                Game.Mercenary.StrikerDefaultAvatarDataProvider.Warmup();
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED");
                Console.WriteLine($"Error: Failed to load Script.pvf: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            // 启动时一次性按当前等级重算所有角色战斗属性, 修复历史"升级未重算属性"的存量数据。
            // 必须在 PVF 加载后: 属性表来自 Script.pvf。幂等, 重复执行结果一致, 正常时静默, 仅出错时提示。
            try
            {
                new Game.CharacterData.SqliteSubtype1Repository(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath)
                    .RecomputeAllCombatStats();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] combat stats recompute skipped: {ex.Message}");
            }

            var server = new MultiStructureTcpServer();
            var sessionDirectory = new Game.Session.SessionDirectory();

            int channelPort = GameNetworkConfig.ProxyMode ? 7002 : 7001;
            int gamePort = GameNetworkConfig.ProxyMode ? 10012 : 10011;

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { channelPort, (new ChannelProtocolHandler(), new ChannelPacketHeader()) },
                { gamePort, (new GameProtocolHandler(sessionDirectory, packet => server.BroadcastToPortAsync(gamePort, packet)), new GamePacketHeader()) }
            };

            server.Start(portConfigs);

            Game.Inventory.InventoryPersistenceService.RegisterClock(Infrastructure.ClockService.Instance);
            Infrastructure.ClockService.Instance.Start();

            if (GameNetworkConfig.ProxyMode)
                Console.WriteLine($"[ProxyMode] Server listening on {channelPort}(channel) / {gamePort}(game) – PvfProxy forwards 7001/10011 to these ports.");

            Console.WriteLine("Multi-structure TCP server started!");
            Console.WriteLine($"Advertised server IP: {GameNetworkConfig.ServerIp} (ports 7001 channel, 10011 game)");
            var interactiveConsole = Environment.UserInteractive && !Console.IsInputRedirected;
            Console.WriteLine(interactiveConsole
                ? "Press 's' for statistics, 'q' to quit."
                : "Running without interactive console. Stop the service to quit.");

            if (!interactiveConsole)
            {
                var stopped = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stopped.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stopped.Set();
                stopped.Wait();
            }
            else
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.KeyChar == 's' || key.KeyChar == 'S')
                    {
                        var stats = server.GetStatistics();
                        Console.WriteLine("\n=== Server Statistics ===");
                        Console.WriteLine($"Total Clients: {stats.TotalClients}");
                        foreach (var stat in stats.PortStats)
                        {
                            var config = portConfigs[stat.Key];
                            Console.WriteLine($"Port {stat.Key} ({config.structure.GetType().Name}): {stat.Value} clients");
                        }
                        Console.WriteLine("=========================\n");
                    }
                    else if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        break;
                    }
                }
            }

            server.Stop();
            Game.Inventory.InventoryPersistenceService.SaveAllDirty();
            // 服务停止后不再产生常规业务日志，此时完成队列并等待后台写入结束，避免退出时丢失尾部日志。
            FileLogger.Shutdown(TimeSpan.FromSeconds(5));
            Console.WriteLine("Server stopped.");
        }
    }
}
