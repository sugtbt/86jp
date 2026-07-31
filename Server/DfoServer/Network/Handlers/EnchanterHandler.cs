using System;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;

namespace DfoServer.Network.Handlers
{
    internal sealed class EnchanterHandler
    {
        private const ushort ExtractionCommand = (ushort)CmdPacketType.EXPERT_EXTRACTION;
        private const ushort RepairCommand = (ushort)CmdPacketType.REPAIR_EXPERT_JOB_STORE;
        private const ushort CompoundCommand =
            (ushort)CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB;

        private readonly ExpertJobStoreRuntimeService _stores;
        private readonly IEnchanterMachineStateRepository _machines;
        private readonly ICharacterRepository _characters;
        private readonly SqliteSubtype0FieldsRepository _subtype0;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly ExpertJobPersistenceService _persistence;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly ExpertJobOperationCoordinator _operations;

        internal EnchanterHandler(
            ExpertJobStoreRuntimeService stores,
            IEnchanterMachineStateRepository machines,
            ICharacterRepository characters,
            SqliteSubtype0FieldsRepository subtype0,
            HonorLevelSyncService honorLevel,
            ExpertJobPersistenceService persistence,
            InventoryRefreshSender inventoryRefresh,
            ExpertJobOperationCoordinator operations)
        {
            _stores = stores ?? throw new ArgumentNullException(nameof(stores));
            _machines = machines ?? throw new ArgumentNullException(nameof(machines));
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _subtype0 = subtype0 ?? throw new ArgumentNullException(nameof(subtype0));
            _honorLevel = honorLevel ?? throw new ArgumentNullException(nameof(honorLevel));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _inventoryRefresh = inventoryRefresh ?? throw new ArgumentNullException(nameof(inventoryRefresh));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        internal async Task HandleExtraction(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!EnchanterExtractionRequest.TryParse(body, out var command)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.EnchanterType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendError(session, EnchanterExtractionService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            try
            {
                EnchanterExtractionResult result;
                bool success;
                var previousExperience = player.Subtype0Tail.ExpertJobExp;
                lock (lease.SyncRoot)
                {
                    success = EnchanterExtractionService.TryExtract(
                        lease.Inventory,
                        command,
                        previousExperience,
                        out result);
                }
                if (!success)
                {
                    await SendError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _machines.SaveEnchanterProgressInTransaction(
                            connection, transaction, player.CharacterId,
                            result.ExperienceGain, result.LearnedRecipeIds)))
                {
                    await SendError(session, EnchanterExtractionService.ErrorInvalidState);
                    return;
                }

                player.Subtype0Tail.ExpertJobExp = result.FinalExperience;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    ExtractionCommand,
                    EnchanterExtractionPacketBuilder.BuildSuccess(result)));
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    BuildRefreshSlots(result));
                await UserInfoBroadcastService.SendSubtype0Async(
                    session,
                    _characters,
                    _subtype0,
                    _honorLevel,
                    "EXPERT_JOB_EXP_REFRESH");
                if (EnchanterConfigProvider.Config.GetLevel(previousExperience)
                    != EnchanterConfigProvider.Config.GetLevel(result.FinalExperience))
                {
                    var state = _machines.Load(
                        player.CharacterId,
                        ExpertJobStateCodec.EnchanterType);
                    var expertJobBody = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                        ExpertJobStateCodec.EnchanterType,
                        state,
                        result.FinalExperience);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        expertJobBody));
                }
                FileLogger.Log(
                    $"[Enchanter] EXTRACT cid={player.CharacterId} " +
                    $"extractorSlot={command.ExtractorSlotIndex} " +
                    $"targetSlot={command.TargetSlotIndex} " +
                    $"results={result.Materials.Count} exp={result.ExperienceGain}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        internal async Task HandleRepair(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!RepairExpertJobStoreRequest.IsValid(body)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.EnchanterType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendRepairError(session, EnchanterMachineRepairService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendRepairError(session, EnchanterMachineRepairService.ErrorInvalidState);
                    return;
                }

                var machine = _machines.ResolveEnchanter(player.CharacterId);
                ExpertJobMachineRepairResult result;
                bool success;
                lock (lease.SyncRoot)
                {
                    success = EnchanterMachineRepairService.TryRepair(
                        lease.Inventory,
                        machine,
                        player.Subtype0Tail.ExpertJobExp,
                        out result);
                }
                if (!success)
                {
                    await SendRepairError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _machines.SaveEnchanterInTransaction(
                            connection,
                            transaction,
                            player.CharacterId,
                            machine,
                            0,
                            null)))
                {
                    await SendRepairError(
                        session,
                        EnchanterMachineRepairService.ErrorInvalidState);
                    return;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    RepairCommand,
                    ExpertJobStorePacketBuilder.BuildRepairNotification(
                        result.Gold,
                        result.Endurance)));
                FileLogger.Log(
                    $"[Enchanter] REPAIR cid={player.CharacterId} cost={result.Cost} " +
                    $"endurance={result.Endurance} gold={result.Gold}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        internal async Task HandleCompound(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!EnchanterCompoundRequest.TryParse(body, out var command)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.EnchanterType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendCompoundError(session, EnchanterCompoundService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            var responded = false;
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendCompoundError(
                        session,
                        EnchanterCompoundService.ErrorInvalidState);
                    return;
                }

                var previousExperience = player.Subtype0Tail.ExpertJobExp;
                var expertJobState = command.IsProductCraft
                    ? _machines.Load(
                        player.CharacterId,
                        ExpertJobStateCodec.EnchanterType)
                    : null;
                EnchanterCompoundResult result;
                bool success;
                lock (lease.SyncRoot)
                {
                    success = EnchanterCompoundService.TryCraft(
                        lease.Inventory,
                        command,
                        previousExperience,
                        expertJobState,
                        out result);
                }
                if (!success)
                {
                    await SendCompoundError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) =>
                            _machines.SaveEnchanterProgressInTransaction(
                                connection,
                                transaction,
                                player.CharacterId,
                                result.ExperienceGain,
                                result.LearnedRecipeIds)))
                {
                    await SendCompoundError(
                        session,
                        EnchanterCompoundService.ErrorInvalidState);
                    return;
                }

                player.Subtype0Tail.ExpertJobExp = result.FinalExperience;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    CompoundCommand,
                    EnchanterCompoundPacketBuilder.BuildSuccess(result)));
                responded = true;
                if (result.ExtractorInventoryChanged)
                {
                    await _inventoryRefresh.SendItemListRefresh(
                        session,
                        InventoryListType.Main);
                }
                else
                {
                    await _inventoryRefresh.SendUpdateItemList(
                        session,
                        InventoryListType.Main,
                        result.ChangedMainSlots);
                }
                if (result.GoldSpent > 0 && !result.ExtractorInventoryChanged)
                    await _inventoryRefresh.SendGoldUpdate(session);
                if (result.ExperienceGain > 0)
                {
                    await UserInfoBroadcastService.SendSubtype0Async(
                        session,
                        _characters,
                        _subtype0,
                        _honorLevel,
                        "EXPERT_JOB_EXP_REFRESH");
                }
                if (result.RequiresExpertJobInfoRefresh
                    || result.ExtractorInventoryChanged)
                {
                    var refreshedState = _machines.Load(
                        player.CharacterId,
                        ExpertJobStateCodec.EnchanterType);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        ExpertJobInfoBodyBuilder.BuildProjectedBody(
                            ExpertJobStateCodec.EnchanterType,
                            refreshedState,
                            result.FinalExperience)));
                }

                FileLogger.Log(
                    $"[Enchanter] COMPOUND cid={player.CharacterId} " +
                    $"kind={(command.IsProductCraft ? "product" : "bead")} " +
                    $"recipe={command.RecipeItemId} count={command.RequestedCount} " +
                    $"cardSlot={command.CardSlotIndex} outputs={result.Outputs.Count} " +
                    $"failure={result.FailureCount} exp={result.ExperienceGain}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Enchanter] COMPOUND failed cid={player.CharacterId}: {ex.Message}");
                if (!responded)
                {
                    await SendCompoundError(
                        session,
                        EnchanterCompoundService.ErrorInvalidState);
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static Task SendError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                ExtractionCommand,
                CommonPacketBodyBuilder.BuildCmdError(errorCode)));

        private static Task SendRepairError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                RepairCommand,
                ExpertJobStorePacketBuilder.BuildRepairError(errorCode)));

        private static Task SendCompoundError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                CompoundCommand,
                EnchanterCompoundPacketBuilder.BuildError(errorCode)));

        private static short[] BuildRefreshSlots(EnchanterExtractionResult result)
        {
            var slots = new short[result.Materials.Count + 1];
            slots[0] = result.TargetSlotIndex;
            for (var index = 0; index < result.Materials.Count; index++)
                slots[index + 1] = result.Materials[index].SlotIndex;
            return slots;
        }
    }
}
