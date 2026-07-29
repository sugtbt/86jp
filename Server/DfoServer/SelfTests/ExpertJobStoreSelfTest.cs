using System;
using System.IO;
using System.Linq;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ExpertJobStoreSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== EXPERT_JOB_STORE selftest ===");
            var failures = 0;
            var capturedBody = new byte[]
            {
                0x00,
                0x01, 0x00, 0x00, 0x00, 0x30,
                0x01, 0x00, 0x00, 0x00,
                0xA5, 0x03,
                0x0A, 0x01,
                0xFF, 0xFF,
            };

            Check("captured create request parses", CreateExpertJobStoreRequest.TryParse(capturedBody, out var command), ref failures);
            Check("captured create request fields", command != null
                && command.Kind == ExpertJobStoreKind.DisjointMachine
                && command.NameBytes.SequenceEqual(new byte[] { 0x30 })
                && command.Cost == 1
                && command.PositionX == 933
                && command.PositionY == 266
                && command.Direction == -1, ref failures);
            Check("truncated request rejects", !CreateExpertJobStoreRequest.TryParse(capturedBody.Take(15).ToArray(), out _), ref failures);
            Check("trailing request bytes reject", !CreateExpertJobStoreRequest.TryParse(capturedBody.Concat(new byte[] { 0 }).ToArray(), out _), ref failures);
            Check("captured enter request parses",
                EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1, 0x03 }, out var enterRequest)
                && enterRequest.OwnerUserId == 1009, ref failures);
            Check("short enter request rejects",
                !EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1 }, out _), ref failures);
            Check("trailing enter request rejects",
                !EnterExpertJobStoreRequest.TryParse(new byte[] { 0xF1, 0x03, 0x00 }, out _), ref failures);
            Check("captured empty close request accepts null body",
                CloseExpertJobStoreRequest.IsValid(null), ref failures);
            Check("empty close request accepts empty body",
                CloseExpertJobStoreRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("close request rejects trailing bytes",
                !CloseExpertJobStoreRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured empty repair request accepts null body",
                RepairExpertJobStoreRequest.IsValid(null), ref failures);
            Check("empty repair request accepts empty body",
                RepairExpertJobStoreRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("repair request rejects trailing bytes",
                !RepairExpertJobStoreRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured empty upgrade request accepts null body",
                UpgradeDisjointMachineRequest.IsValid(null), ref failures);
            Check("empty upgrade request accepts empty body",
                UpgradeDisjointMachineRequest.IsValid(Array.Empty<byte>()), ref failures);
            Check("upgrade request rejects trailing bytes",
                !UpgradeDisjointMachineRequest.IsValid(new byte[] { 0x00 }), ref failures);
            Check("captured machine disjoint request parses",
                DisjointMachineRequest.TryParse(
                    new byte[] { 0xF1, 0x03, 0x25, 0x00, 0x00 },
                    out var disjointRequest)
                && disjointRequest.OwnerUserId == 1009
                && disjointRequest.TargetSlotIndex == 37
                && disjointRequest.ItemSpace == InventoryListType.Main,
                ref failures);
            Check("machine disjoint request rejects trailing bytes",
                !DisjointMachineRequest.TryParse(
                    new byte[] { 0xF1, 0x03, 0x25, 0x00, 0x00, 0x00 },
                    out _),
                ref failures);

            var runtime = new ExpertJobStoreRuntimeService();
            var sessionId = Guid.NewGuid();
            var state = new DisjointMachineState
            {
                MachineGrade = 6,
                Endurance = 163,
            };
            Check("disjointer creates store", runtime.TryCreate(
                    sessionId,
                    990486,
                    321,
                    3,
                    1,
                    2,
                    false,
                    false,
                    command,
                    state,
                    out var store,
                    out var errorCode)
                && errorCode == 0
                && runtime.Count == 1, ref failures);
            Check("duplicate owner rejects", !runtime.TryCreate(
                    sessionId,
                    990486,
                    321,
                    3,
                    1,
                    2,
                    false,
                    false,
                    command,
                    state,
                    out _,
                    out errorCode)
                && errorCode == ExpertJobStoreRuntimeService.ErrorStoreBusy, ref failures);
            Check("owner lookup reports deployed store",
                runtime.HasStore(990486)
                && runtime.TryGetOwnedStore(sessionId, 990486, out var ownedStore)
                && ReferenceEquals(store, ownedStore), ref failures);

            var createBody = ExpertJobStorePacketBuilder.BuildCreateNotification(store);
            Check("create notification field order", createBody.Length == 17
                && BitConverter.ToUInt16(createBody, 0) == 321
                && BitConverter.ToInt32(createBody, 2) == 1
                && createBody[6] == 0x30
                && createBody[7] == 1
                && createBody[8] == 2
                && BitConverter.ToInt16(createBody, 9) == 933
                && BitConverter.ToInt16(createBody, 11) == 266
                && BitConverter.ToInt32(createBody, 13) == 1, ref failures);
            Check("success ack shape", CommonPacketBodyBuilder.BuildSuccessAck().SequenceEqual(new byte[] { 1 }), ref failures);
            Check("same-area owner uid resolves",
                runtime.TryGetStoreInArea(1, 2, 321, out var entered)
                && ReferenceEquals(store, entered), ref failures);
            Check("cross-area owner uid rejects",
                !runtime.TryGetStoreInArea(1, 3, 321, out _), ref failures);
            Check("unknown owner uid rejects",
                !runtime.TryGetStoreInArea(1, 2, 322, out _), ref failures);
            var visitorSessionId = Guid.NewGuid();
            Check("enter binds visitor session",
                runtime.TryEnter(visitorSessionId, 990487, 1, 2, 321, out var visitorStore)
                && ReferenceEquals(store, visitorStore)
                && runtime.TryGetEnteredStore(visitorSessionId, 990487, out visitorStore)
                && ReferenceEquals(store, visitorStore), ref failures);
            Check("unbound session cannot route machine disjoint",
                !runtime.TryGetEnteredStore(Guid.NewGuid(), 990487, out _), ref failures);
            var leavingVisitorSessionId = Guid.NewGuid();
            Check("visitor leave clears machine routing",
                runtime.TryEnter(leavingVisitorSessionId, 990488, 1, 2, 321, out _)
                && runtime.Leave(leavingVisitorSessionId)
                && !runtime.TryGetEnteredStore(leavingVisitorSessionId, 990488, out _),
                ref failures);
            Check("enter ack field order",
                ExpertJobStorePacketBuilder.BuildEnterSuccess(store).SequenceEqual(new byte[]
                {
                    0x01,
                    0x00,
                    0x06,
                    0x01, 0x00, 0x00, 0x00,
                    0xA3, 0x00, 0x00, 0x00,
                }), ref failures);
            Check("area projection includes store", runtime.GetStoresInArea(1, 2).Count == 1
                && runtime.GetStoresInArea(1, 3).Count == 0, ref failures);
            Check("wrong session cannot close", !runtime.TryClose(Guid.NewGuid(), 990486, out _)
                && runtime.Count == 1, ref failures);
            Check("session close removes store", runtime.TryCloseSession(sessionId, out var closed)
                && ReferenceEquals(store, closed)
                && ExpertJobStorePacketBuilder.BuildCloseNotification(closed.OwnerUserId)
                    .SequenceEqual(BitConverter.GetBytes((ushort)321))
                && runtime.Count == 0, ref failures);
            Check("closing owner clears visitor bindings",
                !runtime.TryGetEnteredStore(visitorSessionId, 990487, out _), ref failures);

            var otherProfession = new ExpertJobStoreCreateCommand
            {
                Kind = ExpertJobStoreKind.EnchantShop,
                NameBytes = new byte[] { 0x31 },
                Cost = 1,
            };
            Check("future store kind is isolated until implemented", !runtime.TryCreate(
                    Guid.NewGuid(), 990487, 322, 1, 1, 2, false, false,
                    otherProfession, state, out _, out errorCode)
                && errorCode == ExpertJobStoreRuntimeService.ErrorInvalidState, ref failures);

            Check("legacy migration rejects truncated state",
                !ExpertJobStateCodec.TryDecodeLegacyBlob(
                    new byte[] { 0x00, 0x03, 0x07 },
                    out _,
                    out _),
                ref failures);
            RunLegacyMigrationChecks(ref failures);
            RunPersistenceChecks(ref failures);
            Check("PVF disjointer initial endurance",
                DisjointMachineConfigProvider.InitialEndurance == 300, ref failures);
            var config = DisjointMachineConfigProvider.Config;
            Check("PVF disjointer machine limits",
                config.MaximumStoreCharge == 10000
                && config.BaseConst == 150
                && config.EnduranceReduceMin == 1
                && config.EnduranceReduceMax == 3
                && config.GainExpMin == 0
                && config.GainExpMax == 1
                && config.RepairRules.Count == 11
                && config.GetRepairRule(1)?.FullRepairCost == 10000
                && config.GetRepairRule(1)?.MaximumEndurance == 300
                && config.GetUpgradeCost(2) == 30000
                && config.GetMinimumCharacterLevel(2) == 20,
                ref failures);
            Check("PVF disjointer experience thresholds map to one-based level",
                config.GetExpertJobLevel(19) == 1
                && config.GetExpertJobLevel(20) == 2,
                ref failures);
            Check("PVF disjointer grade-zero rarity rule",
                config.GetResult(0, 0, 0) is DisjointMachineResultRule rule
                && rule.ItemId == 3037
                && Math.Abs(rule.Multiplier - 0.72d) < 0.0001d, ref failures);
            Check("PVF disjointer preserves all equipment-state result rows",
                config.GetResult(3, 3, 0) != null
                && config.GetResult(3, 3, 1) != null
                && config.GetResult(3, 3, 2) != null,
                ref failures);
            Check("player disjoint additional table uses equipment grade boundaries",
                DisjointMachineResultCalculator.SelectAdditionalResult(
                    new[]
                    {
                        new DisjointMachineSelectionRule
                        {
                            MinimumLevel = 0,
                            MaximumLevel = 66,
                            ItemId = 1,
                            Weight = 10000,
                        },
                        new DisjointMachineSelectionRule
                        {
                            MinimumLevel = 67,
                            MaximumLevel = 99,
                            ItemId = 2,
                            Weight = 10000,
                        },
                    },
                    67)?.ItemId == 2,
                ref failures);
            var unidentifiedRuleSource = new ItemCore { AmplifyType = 0x80 };
            var epicMetadata = new ItemMetadata { Rarity = 4 };
            Check("current PVF starts unidentified epic disjoint at machine grade four",
                DisjointMachineResultCalculator.ResolveRule(
                    unidentifiedRuleSource,
                    epicMetadata,
                    3) == null
                && ReferenceEquals(
                    DisjointMachineResultCalculator.ResolveRule(
                        unidentifiedRuleSource,
                        epicMetadata,
                        4),
                    config.GetResult(3, 4, 1)),
                ref failures);
            var chronicleRuleSource = new ItemCore();
            chronicleRuleSource.ChronicleOption0.OptionId = 1;
            Check("chronicle equipment selects PVF state two result",
                ReferenceEquals(
                    DisjointMachineResultCalculator.ResolveRule(
                        chronicleRuleSource,
                        new ItemMetadata { Rarity = 3 },
                        1),
                    config.GetResult(0, 3, 2)),
                ref failures);

            const int unidentifiedEpicEquipmentId = 27769;
            var unidentifiedEpicMetadata = ItemMetadataResolver.Resolve(unidentifiedEpicEquipmentId);
            Check("unidentified epic fixture resolves from current PVF",
                unidentifiedEpicMetadata != null
                && unidentifiedEpicMetadata.ItemKind == "equipment"
                && unidentifiedEpicMetadata.Rarity == 4,
                ref failures);
            var systemDisjointInventory = CreateUnidentifiedEquipmentInventory(
                990510,
                unidentifiedEpicEquipmentId,
                71001);
            Check("system disjoint keeps unidentified equipment rejected",
                !InventoryDisjointService.TryDisjointItem(
                    systemDisjointInventory,
                    CreateDisjointRequest(37),
                    out var systemRejectedResult)
                && systemRejectedResult.ErrorCode == DisjointItemResult.ErrorInvalidTarget
                && systemDisjointInventory.GetItem(InventoryListType.Main, 37)?.AmplifyType == 0x80,
                ref failures);

            var lowGradeInventory = CreateUnidentifiedEquipmentInventory(
                990511,
                unidentifiedEpicEquipmentId,
                71002);
            var lowGradeStore = CreateOperationStore(lowGradeInventory.CharacterId, 3);
            Check("player machine below PVF qualification returns machine grade error without mutation",
                !DisjointMachineService.TryDisjoint(
                    lowGradeInventory,
                    lowGradeInventory,
                    lowGradeStore,
                    37,
                    int.MaxValue,
                    out var lowGradeResult)
                && lowGradeResult.ErrorCode == DisjointMachineService.ErrorMachineGradeTooLow
                && ExpertJobStorePacketBuilder.BuildDisjointError(lowGradeResult.ErrorCode)
                    .SequenceEqual(new byte[] { 0, 0xD4 })
                && lowGradeInventory.GetItem(InventoryListType.Main, 37)?.AmplifyType == 0x80
                && lowGradeStore.DisjointMachine.Endurance == 300,
                ref failures);

            var qualifiedInventory = CreateUnidentifiedEquipmentInventory(
                990512,
                unidentifiedEpicEquipmentId,
                71003);
            var qualifiedStore = CreateOperationStore(qualifiedInventory.CharacterId, 4);
            Check("player machine at PVF qualification disjoints unidentified epic",
                DisjointMachineService.TryDisjoint(
                    qualifiedInventory,
                    qualifiedInventory,
                    qualifiedStore,
                    37,
                    int.MaxValue,
                    out var qualifiedResult)
                && qualifiedResult.ErrorCode == 0
                && qualifiedInventory.GetItem(InventoryListType.Main, 37) == null
                && qualifiedResult.DisjointResult.Materials.Any(material =>
                    material.ItemTemplateId == 10088692
                    && material.Count == 6)
                && qualifiedStore.DisjointMachine.Endurance < 300,
                ref failures);

            const int testEquipmentId = 33000;
            var testMetadata = ItemMetadataResolver.Resolve(testEquipmentId);
            Check("machine disjoint test equipment resolves from current PVF",
                testMetadata != null && testMetadata.ItemKind == "equipment", ref failures);
            var selfInventory = new InventoryService(990500, 990500);
            selfInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            selfInventory.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 70001,
            });
            selfInventory.ClearDirtyState();
            var operationStore = new ExpertJobStoreSession
            {
                OwnerCharacterId = selfInventory.CharacterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = 300,
                },
                Cost = 100,
            };
            var operationSucceeded = DisjointMachineService.TryDisjoint(
                    selfInventory,
                    selfInventory,
                    operationStore,
                    37,
                    int.MaxValue,
                    out var operationResult);
            var operationStateMatches = operationSucceeded
                && operationResult.ErrorCode == 0
                && selfInventory.GetItem(InventoryListType.Main, 37) == null
                && operationResult.DisjointResult.Materials.Count > 0
                && operationResult.DisjointResult.Materials.All(material =>
                    selfInventory.CountMainItem(material.ItemTemplateId) >= material.Count)
                && operationResult.RequesterGold == 5000
                && operationResult.Endurance >= 297
                && operationResult.Endurance <= 299;
            if (!operationStateMatches)
            {
                Console.WriteLine(
                    $"    operation diagnostic: success={operationSucceeded} " +
                    $"error={operationResult?.ErrorCode} rarity={testMetadata?.Rarity} " +
                    $"grade={testMetadata?.Grade} attach={testMetadata?.AttachType} " +
                    $"materials={operationResult?.DisjointResult?.Materials.Count ?? 0} " +
                    $"sourceExists={selfInventory.GetItem(InventoryListType.Main, 37) != null} " +
                    $"endurance={operationStore.DisjointMachine.Endurance}");
            }
            Check("self machine disjoint mutates inventory and endurance",
                operationStateMatches, ref failures);

            var poorRequester = new InventoryService(990501, 990501);
            poorRequester.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50);
            poorRequester.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = testEquipmentId,
                Uid = 70002,
            });
            var ownerInventory = new InventoryService(990502, 990502);
            ownerInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                1000);
            var paidStore = new ExpertJobStoreSession
            {
                OwnerCharacterId = ownerInventory.CharacterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = 1,
                    Endurance = 300,
                },
                Cost = 100,
            };
            Check("insufficient fee rejects without inventory mutation",
                !DisjointMachineService.TryDisjoint(
                    poorRequester,
                    ownerInventory,
                    paidStore,
                    37,
                    int.MaxValue,
                    out var rejectedOperation)
                && rejectedOperation.ErrorCode == DisjointMachineService.ErrorInsufficientGold
                && poorRequester.GetItem(InventoryListType.Main, 37)?.ItemId == testEquipmentId
                && poorRequester.GetMainVirtualCount(0)?.Count == 50
                && ownerInventory.GetMainVirtualCount(0)?.Count == 1000
                && paidStore.DisjointMachine.Endurance == 300,
                ref failures);

            var initSnapshot = new SelectCharacterDataSnapshot();
            ExpertJobStateCodec.ProjectToSnapshot(
                ExpertJobStateCodec.DisjointerType,
                new ExpertJobState
                {
                    DisjointMachine = new DisjointMachineState
                    {
                        MachineGrade = 1,
                        Endurance = 300,
                    },
                },
                initSnapshot.InitializationSnapshot.ExpertJobInfo);
            Check("expert-job initialization packet keeps client one-based grade",
                new ExpertJobInfoBodyBuilder().TryBuild(initSnapshot, 0, out var expertJobInfoBody)
                && expertJobInfoBody.SequenceEqual(new byte[]
                {
                    0, 3,
                    1, 0, 0, 0,
                    0x2C, 0x01, 0, 0,
                }),
                ref failures);
            var recipeState = new ExpertJobState
            {
                GiveUpCount = 1,
            };
            recipeState.LearnedRecipeIds.Add(51001);
            var recipeSnapshot = new ExpertJobInfoSnapshot();
            ExpertJobStateCodec.ProjectToSnapshot(1, recipeState, recipeSnapshot);
            Check("recipe profession projects normalized domain state",
                recipeSnapshot.State0 == 1
                && recipeSnapshot.Mode == 1
                && recipeSnapshot.Entries.SequenceEqual(new[] { 51001 })
                && recipeSnapshot.DisjointMachineGrade == 0
                && recipeSnapshot.DisjointMachineEndurance == 0,
                ref failures);

            var repairInventory = new InventoryService(990503, 990503);
            repairInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            repairInventory.ClearDirtyState();
            var repairState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 297,
            };
            Check("level-one repair uses current-client proportional PVF cost",
                DisjointMachineRepairService.TryRepair(
                    repairInventory,
                    repairState,
                    out var repairResult)
                && repairResult.Cost == 100
                && repairResult.Gold == 4900
                && repairResult.Endurance == 300
                && repairState.Endurance == 300
                && repairInventory.GetMainVirtualCount(0)?.Count == 4900,
                ref failures);
            Check("full endurance machine rejects repair",
                !DisjointMachineRepairService.TryRepair(
                    repairInventory,
                    repairState,
                    out var fullRepairResult)
                && fullRepairResult.ErrorCode == DisjointMachineRepairService.ErrorCannotRepair,
                ref failures);
            var partialRepairInventory = new InventoryService(990508, 990508);
            partialRepairInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                99);
            var partialRepairState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 297,
            };
            Check("insufficient full-repair gold buys official partial repair units",
                DisjointMachineRepairService.TryRepair(
                    partialRepairInventory,
                    partialRepairState,
                    out var partialRepairResult)
                && partialRepairResult.Cost == 99
                && partialRepairResult.Gold == 0
                && partialRepairResult.Endurance == 300
                && partialRepairState.Endurance == 300,
                ref failures);
            Check("repair notification field order",
                ExpertJobStorePacketBuilder.BuildRepairNotification(4900, 300)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0x24, 0x13, 0, 0,
                        0x2C, 0x01, 0, 0,
                    }),
                ref failures);

            var upgradeInventory = new InventoryService(990504, 990504);
            upgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            upgradeInventory.ClearDirtyState();
            var upgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("level-one machine upgrades from current PVF rules",
                DisjointMachineUpgradeService.TryUpgrade(
                    upgradeInventory,
                    upgradeState,
                    20,
                    85,
                    out var upgradeResult)
                && upgradeResult.Cost == 30000
                && upgradeResult.Gold == 20000
                && upgradeResult.Grade == 2
                && upgradeResult.Endurance == 300
                && upgradeState.MachineGrade == 2
                && upgradeState.Endurance == 300
                && upgradeInventory.GetMainVirtualCount(0)?.Count == 20000,
                ref failures);

            var wornUpgradeInventory = new InventoryService(990505, 990505);
            wornUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var wornUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 299,
            };
            Check("worn machine rejects upgrade without mutation",
                !DisjointMachineUpgradeService.TryUpgrade(
                    wornUpgradeInventory,
                    wornUpgradeState,
                    20,
                    85,
                    out var wornUpgradeResult)
                && wornUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorCannotUpgrade
                && wornUpgradeState.MachineGrade == 1
                && wornUpgradeState.Endurance == 299
                && wornUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var inexperiencedUpgradeInventory = new InventoryService(990506, 990506);
            inexperiencedUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var inexperiencedUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("insufficient expert-job experience rejects upgrade",
                !DisjointMachineUpgradeService.TryUpgrade(
                    inexperiencedUpgradeInventory,
                    inexperiencedUpgradeState,
                    19,
                    85,
                    out var inexperiencedUpgradeResult)
                && inexperiencedUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorCannotUpgrade
                && inexperiencedUpgradeState.MachineGrade == 1
                && inexperiencedUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var lowLevelUpgradeInventory = new InventoryService(990509, 990509);
            lowLevelUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                50000);
            var lowLevelUpgradeState = new DisjointMachineState
            {
                MachineGrade = 2,
                Endurance = 300,
            };
            Check("character level limit returns official upgrade error 14",
                !DisjointMachineUpgradeService.TryUpgrade(
                    lowLevelUpgradeInventory,
                    lowLevelUpgradeState,
                    133,
                    29,
                    out var lowLevelUpgradeResult)
                && lowLevelUpgradeResult.ErrorCode
                    == DisjointMachineUpgradeService.ErrorCharacterLevelTooLow
                && lowLevelUpgradeState.MachineGrade == 2
                && lowLevelUpgradeInventory.GetMainVirtualCount(0)?.Count == 50000,
                ref failures);

            var poorUpgradeInventory = new InventoryService(990507, 990507);
            poorUpgradeInventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                29999);
            var poorUpgradeState = new DisjointMachineState
            {
                MachineGrade = 1,
                Endurance = 300,
            };
            Check("insufficient gold returns client upgrade error 22",
                !DisjointMachineUpgradeService.TryUpgrade(
                    poorUpgradeInventory,
                    poorUpgradeState,
                    20,
                    85,
                    out var poorUpgradeResult)
                && poorUpgradeResult.ErrorCode == DisjointMachineUpgradeService.ErrorInsufficientGold
                && poorUpgradeState.MachineGrade == 1
                && poorUpgradeInventory.GetMainVirtualCount(0)?.Count == 29999,
                ref failures);
            Check("upgrade notification field order",
                ExpertJobStorePacketBuilder.BuildUpgradeNotification(20000, 2, 300)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0x20, 0x4E, 0, 0,
                        2, 0, 0, 0,
                        0x2C, 0x01, 0, 0,
                    }),
                ref failures);

            var packetResult = new DisjointMachineOperationResult
            {
                RequesterGold = 1234,
                OwnerGold = 1234,
                Endurance = 297,
                DisjointResult = new DisjointItemResult
                {
                    Request = new DisjointItemRequest
                    {
                        TargetSlotIndex = 37,
                        ItemSpace = InventoryListType.Main,
                    },
                },
            };
            packetResult.DisjointResult.Materials.Add(new DisjointMaterialResult
            {
                SlotIndex = 121,
                ItemTemplateId = 3037,
                Count = 5,
            });
            Check("machine disjoint success ack appends gold and endurance",
                ExpertJobStorePacketBuilder.BuildDisjointSuccess(packetResult).SequenceEqual(new byte[]
                {
                    1,
                    0x25, 0,
                    0,
                    1,
                    0x79, 0,
                    0xDD, 0x0B, 0, 0,
                    5, 0, 0, 0,
                    0xD2, 0x04, 0, 0,
                    0x29, 0x01, 0, 0,
                }), ref failures);
            Check("owner machine notification field order",
                ExpertJobStorePacketBuilder.BuildOwnerDisjointNotification(1234, 297)
                    .SequenceEqual(new byte[]
                    {
                        1,
                        0xD2, 0x04, 0, 0,
                        0x29, 0x01, 0, 0,
                    }), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool ok, ref int failures)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                failures++;
        }

        private static void RunPersistenceChecks(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dfo-expert-job-{Guid.NewGuid():N}.db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts (account_id, m_id) VALUES (990490, 'expert-job-test');
INSERT INTO characters (character_id, account_id, name)
VALUES (990490, 990490, 'expert-job-test');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990490, 3, 20);";
                        command.ExecuteNonQuery();
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        SqliteExpertJobStateRepository.InitializeInTransaction(
                            connection,
                            transaction,
                            990490,
                            ExpertJobStateCodec.DisjointerType);
                        transaction.Commit();
                    }
                }

                var repository = new SqliteExpertJobStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var initialized = repository.Load(
                    990490,
                    ExpertJobStateCodec.DisjointerType);
                Check("disjointer initialization persists explicit state",
                    initialized.DisjointMachine.MachineGrade == 1
                    && initialized.DisjointMachine.Endurance == 300,
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check("machine state and experience save atomically",
                            repository.SaveInTransaction(
                                connection,
                                transaction,
                                990490,
                                new DisjointMachineState
                                {
                                    MachineGrade = 7,
                                    Endurance = 269,
                                },
                                5),
                            ref failures);
                        transaction.Commit();
                    }
                }

                var reloaded = repository.Load(
                    990490,
                    ExpertJobStateCodec.DisjointerType);
                Check("explicit machine state reloads without init blob",
                    reloaded.DisjointMachine.MachineGrade == 7
                    && reloaded.DisjointMachine.Endurance == 269
                    && ReadExpertJobExperience(connectionString, 990490) == 25,
                    ref failures);
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static void RunLegacyMigrationChecks(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dfo-expert-job-migration-{Guid.NewGuid():N}.db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
ALTER TABLE character_init_flags ADD COLUMN expert_job_blob BLOB;
INSERT INTO accounts (account_id, m_id) VALUES (990489, 'expert-job-migration');
INSERT INTO characters (character_id, account_id, name)
VALUES (990489, 990489, 'expert-job-migration');
INSERT INTO character_subtype0_fields (
    character_id, expert_job_type, expert_job_exp)
VALUES (990489, 3, 594);
INSERT INTO character_init_flags (character_id, expert_job_blob)
VALUES (990489, X'0003070000000D01000000');
DELETE FROM character_expert_job WHERE character_id=990489;
PRAGMA user_version=47;";
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                }

                var repository = new SqliteExpertJobStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var migrated = repository.Load(
                    990489,
                    ExpertJobStateCodec.DisjointerType);
                Check("v48 migrates legacy machine state and removes packet blob",
                    migrated.DisjointMachine.MachineGrade == 7
                    && migrated.DisjointMachine.Endurance == 269
                    && !HasColumn(connectionString, "character_init_flags", "expert_job_blob"),
                    ref failures);
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }
        }

        private static long ReadExpertJobExperience(
            string connectionString,
            int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT expert_job_exp
FROM character_subtype0_fields
WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static bool HasColumn(
            string connectionString,
            string tableName,
            string columnName)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA table_info({tableName});";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (string.Equals(
                                    reader.GetString(1),
                                    columnName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
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
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static InventoryService CreateUnidentifiedEquipmentInventory(
            int characterId,
            int itemId,
            int uid)
        {
            var inventory = new InventoryService(characterId, characterId);
            inventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                5000);
            inventory.SetItem(InventoryListType.Main, 37, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = itemId,
                Uid = uid,
                AmplifyType = 0x80,
            });
            inventory.ClearDirtyState();
            return inventory;
        }

        private static ExpertJobStoreSession CreateOperationStore(int characterId, byte grade)
        {
            return new ExpertJobStoreSession
            {
                OwnerCharacterId = characterId,
                Kind = ExpertJobStoreKind.DisjointMachine,
                DisjointMachine = new DisjointMachineState
                {
                    MachineGrade = grade,
                    Endurance = 300,
                },
            };
        }

        private static DisjointItemRequest CreateDisjointRequest(short slotIndex)
        {
            return new DisjointItemRequest
            {
                TargetSlotIndex = slotIndex,
                ItemSpace = InventoryListType.Main,
                DisjointItemSlotIndex = -1,
            };
        }
    }
}
