using System;
using System.Collections.Generic;
using System.Text.Json;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonPersistentEffectKinds
    {
        internal const string SettlementExperienceGrant =
            "settlement-experience-grant";
        internal const string SuitableDungeonLuckyStar =
            "suitable-dungeon-lucky-star";
    }

    internal sealed class SuitableDungeonLuckyStarResult
    {
        internal bool Granted { get; set; }
        internal ushort NewTotal { get; set; }
    }

    internal sealed class DungeonPersistentEffectRecoveryResult
    {
        internal ExperienceGrantResult LatestExperienceGrant { get; set; }
        internal int CommittedCount { get; set; }
        internal int DeadLetterCount { get; set; }
        internal int FailedCount { get; set; }
    }

    internal sealed class SettlementExperienceEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public byte PreviousLevel { get; set; }
        public uint PreviousExp { get; set; }
        public uint RawGain { get; set; }
        public bool NormalizeMaxLevelExp { get; set; }
        public byte ExpectedDatabaseLevel { get; set; }
        public uint ExpectedDatabaseExp { get; set; }
    }

    internal sealed class SettlementExperienceEffectResult
    {
        public uint RawGain { get; set; }
        public uint HonorExpGain { get; set; }
        public uint NormalExpGain { get; set; }
        public byte PreviousLevel { get; set; }
        public uint PreviousExp { get; set; }
        public byte NewLevel { get; set; }
        public uint NewExp { get; set; }
        public bool NormalizedMaxLevelExp { get; set; }
        public bool Persisted { get; set; }
        public uint GrowthCapsuleExpGain { get; set; }
        public ulong TotalHonorExp { get; set; }
        public uint TotalGrowthCapsuleExp { get; set; }
    }

    internal sealed class SuitableDungeonLuckyStarEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int DungeonId { get; set; }
        public int ClearLevel { get; set; }
        public int Amount { get; set; }
    }

    internal sealed class SuitableDungeonLuckyStarEffectResult
    {
        public bool Granted { get; set; }
        public ushort NewTotal { get; set; }
    }

    // Typed persistent effect dispatcher. Only registered payload kinds can
    // mutate state; unknown versions are moved to dead-letter without execution.
    internal sealed class DungeonPersistentEffectApplicationService
    {
        private const int PayloadVersion = 1;
        private const int ResultVersion = 1;
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
            };

        private readonly string _connectionString;
        private readonly DungeonPersistentEffectOutbox _outbox;

        internal DungeonPersistentEffectApplicationService(
            string connectionString,
            DungeonPersistentEffectOutbox outbox = null)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException(
                    "A database connection string is required.",
                    nameof(connectionString));
            _outbox = outbox
                ?? new DungeonPersistentEffectOutbox(connectionString);
        }

        internal DungeonPersistentEffectOutbox Outbox => _outbox;

        internal bool TryApplySettlementExperience(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            byte previousLevel,
            uint previousExp,
            uint rawGain,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    DungeonPersistentEffectKinds.SettlementExperienceGrant,
                    characterId);
                var record = _outbox.Get(effectId);
                if (record == null)
                {
                    LoadCharacterProgress(
                        characterId,
                        out var expectedLevel,
                        out var expectedExp);
                    var payload = new SettlementExperienceEffectPayload
                    {
                        CharacterId = characterId,
                        AccountId = accountId,
                        PreviousLevel = previousLevel,
                        PreviousExp = previousExp,
                        RawGain = rawGain,
                        NormalizeMaxLevelExp = true,
                        ExpectedDatabaseLevel = expectedLevel,
                        ExpectedDatabaseExp = expectedExp,
                    };
                    _outbox.Enqueue(CreateDefinition(
                        effectId,
                        characterId,
                        accountId,
                        payload));
                    record = _outbox.Get(effectId);
                }

                var storedPayload = DeserializePayload<SettlementExperienceEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SettlementExperienceGrant);
                if (storedPayload.CharacterId != characterId
                    || storedPayload.AccountId != accountId
                    || storedPayload.PreviousLevel != previousLevel
                    || storedPayload.PreviousExp != previousExp
                    || storedPayload.RawGain != rawGain)
                {
                    throw new InvalidOperationException(
                        "Settlement experience effect was retried with different inputs.");
                }

                return TryExecuteSettlementExperience(record, out result, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TryApplySuitableDungeonLuckyStar(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            int dungeonId,
            int clearLevel,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar,
                    characterId);
                var payload = new SuitableDungeonLuckyStarEffectPayload
                {
                    CharacterId = characterId,
                    AccountId = accountId,
                    DungeonId = dungeonId,
                    ClearLevel = clearLevel,
                    Amount = 1,
                };
                _outbox.Enqueue(CreateDefinition(
                    effectId,
                    characterId,
                    accountId,
                    payload));
                var record = _outbox.Get(effectId);
                var storedPayload = DeserializePayload<SuitableDungeonLuckyStarEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar);
                if (storedPayload.CharacterId != characterId
                    || storedPayload.AccountId != accountId
                    || storedPayload.DungeonId != dungeonId
                    || storedPayload.ClearLevel != clearLevel
                    || storedPayload.Amount != 1)
                {
                    throw new InvalidOperationException(
                        "Suitable-dungeon lucky-star effect was retried with different inputs.");
                }

                return TryExecuteSuitableDungeonLuckyStar(
                    record,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal DungeonPersistentEffectRecoveryResult RecoverCharacter(
            int characterId)
        {
            var result = new DungeonPersistentEffectRecoveryResult();
            if (characterId <= 0)
                return result;

            foreach (var record in _outbox.LoadRecoverableForCharacter(characterId))
            {
                try
                {
                    switch (record.EffectId.EffectKind)
                    {
                        case DungeonPersistentEffectKinds.SettlementExperienceGrant:
                            if (TryExecuteSettlementExperience(
                                    record,
                                    out var experience,
                                    out var experienceError))
                            {
                                result.LatestExperienceGrant = experience;
                                result.CommittedCount++;
                            }
                            else
                            {
                                result.FailedCount++;
                                LogRecoveryFailure(record, experienceError);
                            }
                            break;
                        case DungeonPersistentEffectKinds.SuitableDungeonLuckyStar:
                            if (TryExecuteSuitableDungeonLuckyStar(
                                    record,
                                    out _,
                                    out var luckyStarError))
                            {
                                result.CommittedCount++;
                            }
                            else
                            {
                                result.FailedCount++;
                                LogRecoveryFailure(record, luckyStarError);
                            }
                            break;
                        default:
                            if (TryDeadLetterUnknown(record))
                                result.DeadLetterCount++;
                            else
                                result.FailedCount++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    LogRecoveryFailure(record, ex.Message);
                }
            }

            return result;
        }

        private bool TryExecuteSettlementExperience(
            DungeonPersistentEffectRecord initialRecord,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadExperienceResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            try
            {
                var payload = DeserializePayload<SettlementExperienceEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SettlementExperienceGrant);
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        LoadCharacterProgress(
                            connection,
                            transaction,
                            payload.CharacterId,
                            out var currentLevel,
                            out var currentExp);
                        if (currentLevel != payload.ExpectedDatabaseLevel
                            || currentExp != payload.ExpectedDatabaseExp)
                        {
                            throw new PermanentPersistentEffectException(
                                $"Settlement experience expected database " +
                                $"{payload.ExpectedDatabaseLevel}/{payload.ExpectedDatabaseExp} " +
                                $"but found {currentLevel}/{currentExp}.");
                        }

                        result = CharacterExperienceService.GrantInTransaction(
                            connection,
                            transaction,
                            payload.CharacterId,
                            payload.AccountId,
                            payload.PreviousLevel,
                            payload.PreviousExp,
                            payload.RawGain,
                            payload.NormalizeMaxLevelExp);
                        if ((result.LeveledUp
                                || result.NormalExpGain > 0
                                || result.NormalizedMaxLevelExp)
                            && !result.Persisted)
                        {
                            throw new InvalidOperationException(
                                "Settlement experience did not persist character progress.");
                        }
                        var persistedResult = FromExperienceGrant(result);
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Settlement experience effect lease was lost before commit.");
                        }
                        transaction.Commit();
                    }
                }
                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedExperienceAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    return true;
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryExecuteSuitableDungeonLuckyStar(
            DungeonPersistentEffectRecord initialRecord,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadLuckyStarResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            try
            {
                var payload = DeserializePayload<SuitableDungeonLuckyStarEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar);
                if (payload.Amount != 1)
                    throw new PermanentPersistentEffectException(
                        "Suitable-dungeon lucky-star amount is not supported.");

                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var wallet = CurrencyService.LoadWallet(
                            connection,
                            transaction,
                            payload.CharacterId);
                        var granted = wallet.LuckyStar
                            < RentalCatalogCodec.MaxLuckyStar;
                        if (granted)
                        {
                            CurrencyService.GrantLuckyStar(
                                connection,
                                transaction,
                                payload.AccountId,
                                payload.Amount);
                        }
                        var newTotal = (ushort)Math.Min(
                            RentalCatalogCodec.MaxLuckyStar,
                            wallet.LuckyStar + (granted ? payload.Amount : 0));
                        result = new SuitableDungeonLuckyStarResult
                        {
                            Granted = granted,
                            NewTotal = newTotal,
                        };
                        var persistedResult = new SuitableDungeonLuckyStarEffectResult
                        {
                            Granted = result.Granted,
                            NewTotal = result.NewTotal,
                        };
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Lucky-star effect lease was lost before commit.");
                        }
                        transaction.Commit();
                    }
                }
                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedLuckyStarAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    return true;
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryDeadLetterUnknown(DungeonPersistentEffectRecord record)
        {
            var claim = _outbox.TryClaim(
                record.EffectId,
                LeaseDuration,
                out var reservation,
                out _);
            return claim == DungeonPersistentEffectClaimResult.DeadLetter
                || (claim == DungeonPersistentEffectClaimResult.Claimed
                    && _outbox.TryDeadLetter(
                        reservation,
                        $"Unknown persistent dungeon effect kind " +
                        $"'{record.EffectId.EffectKind}' version " +
                        $"{record.PayloadVersion}."));
        }

        private bool TryReadCommittedExperienceAfterError(
            DungeonEffectId effectId,
            out ExperienceGrantResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadExperienceResult(record, out result, out _);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadCommittedLuckyStarAfterError(
            DungeonEffectId effectId,
            out SuitableDungeonLuckyStarResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadLuckyStarResult(record, out result, out _);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadExperienceResult(
            DungeonPersistentEffectRecord record,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out SettlementExperienceEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = ToExperienceGrant(persisted);
            return true;
        }

        private static bool TryReadLuckyStarResult(
            DungeonPersistentEffectRecord record,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out SuitableDungeonLuckyStarEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = new SuitableDungeonLuckyStarResult
            {
                Granted = persisted.Granted,
                NewTotal = persisted.NewTotal,
            };
            return true;
        }

        private static T DeserializePayload<T>(
            DungeonPersistentEffectRecord record,
            string expectedKind)
        {
            if (record == null)
                throw new InvalidOperationException(
                    "Persistent dungeon effect record is missing.");
            if (!string.Equals(
                    record.EffectId.EffectKind,
                    expectedKind,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Persistent dungeon effect kind does not match its dispatcher.");
            if (record.PayloadVersion != PayloadVersion)
                throw new PermanentPersistentEffectException(
                    $"Unsupported {expectedKind} payload version " +
                    $"{record.PayloadVersion}.");
            try
            {
                return JsonSerializer.Deserialize<T>(
                           record.PayloadJson,
                           JsonOptions)
                       ?? throw new InvalidOperationException(
                           "Persistent dungeon effect payload is empty.");
            }
            catch (JsonException ex)
            {
                throw new PermanentPersistentEffectException(
                    "Persistent dungeon effect payload is invalid JSON.",
                    ex);
            }
        }

        private static bool TryDeserializeResult<T>(
            DungeonPersistentEffectRecord record,
            out T result,
            out string error)
        {
            result = default;
            error = null;
            if (record == null
                || record.State != DungeonPersistentEffectState.Committed
                || record.ResultVersion != ResultVersion
                || string.IsNullOrWhiteSpace(record.ResultJson))
            {
                error = "Committed persistent effect has no supported result.";
                return false;
            }
            try
            {
                result = JsonSerializer.Deserialize<T>(
                    record.ResultJson,
                    JsonOptions);
                if (result == null)
                {
                    error = "Committed persistent effect result is empty.";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = "Committed persistent effect result is invalid: " +
                    ex.Message;
                return false;
            }
        }

        private static DungeonPersistentEffectDefinition CreateDefinition<T>(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            T payload)
            => new DungeonPersistentEffectDefinition
            {
                EffectId = effectId,
                CharacterId = characterId,
                AccountId = Math.Max(0, accountId),
                PayloadVersion = PayloadVersion,
                PayloadJson = Serialize(payload),
            };

        private static string Serialize<T>(T value)
            => JsonSerializer.Serialize(value, JsonOptions);

        private void LoadCharacterProgress(
            int characterId,
            out byte level,
            out uint exp)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                LoadCharacterProgress(
                    connection,
                    transaction: null,
                    characterId,
                    out level,
                    out exp);
            }
        }

        private static void LoadCharacterProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out byte level,
            out uint exp)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT level, exp
FROM characters
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new InvalidOperationException(
                            $"Character {characterId} does not exist.");
                    level = (byte)Math.Max(0, Math.Min(255, reader.GetInt32(0)));
                    exp = (uint)Math.Min(
                        uint.MaxValue,
                        Math.Max(0L, reader.GetInt64(1)));
                }
            }
        }

        private static SettlementExperienceEffectResult FromExperienceGrant(
            ExperienceGrantResult result)
            => new SettlementExperienceEffectResult
            {
                RawGain = result.RawGain,
                HonorExpGain = result.HonorExpGain,
                NormalExpGain = result.NormalExpGain,
                PreviousLevel = result.PreviousLevel,
                PreviousExp = result.PreviousExp,
                NewLevel = result.NewLevel,
                NewExp = result.NewExp,
                NormalizedMaxLevelExp = result.NormalizedMaxLevelExp,
                Persisted = result.Persisted,
                GrowthCapsuleExpGain = result.GrowthCapsuleExpGain,
                TotalHonorExp = result.TotalHonorExp,
                TotalGrowthCapsuleExp = result.TotalGrowthCapsuleExp,
            };

        private static ExperienceGrantResult ToExperienceGrant(
            SettlementExperienceEffectResult result)
            => new ExperienceGrantResult
            {
                RawGain = result.RawGain,
                HonorExpGain = result.HonorExpGain,
                NormalExpGain = result.NormalExpGain,
                PreviousLevel = result.PreviousLevel,
                PreviousExp = result.PreviousExp,
                NewLevel = result.NewLevel,
                NewExp = result.NewExp,
                NormalizedMaxLevelExp = result.NormalizedMaxLevelExp,
                Persisted = result.Persisted,
                GrowthCapsuleExpGain = result.GrowthCapsuleExpGain,
                TotalHonorExp = result.TotalHonorExp,
                TotalGrowthCapsuleExp = result.TotalGrowthCapsuleExp,
            };

        private static void ValidateEffectIdentity(
            DungeonEffectId effectId,
            string expectedKind,
            int characterId)
        {
            if (!string.Equals(
                    effectId.EffectKind,
                    expectedKind,
                    StringComparison.Ordinal)
                || effectId.Scope != DungeonEffectScope.Player
                || effectId.ScopeTarget <= 0
                || characterId <= 0)
            {
                throw new ArgumentException(
                    "Persistent dungeon effect identity is invalid.",
                    nameof(effectId));
            }
        }

        private static void LogRecoveryFailure(
            DungeonPersistentEffectRecord record,
            string error)
            => FileLogger.Log(
                $"[DungeonPersistentEffect] recovery failed: " +
                $"cid={record?.CharacterId ?? 0} " +
                $"kind={record?.EffectId.EffectKind ?? "unknown"} " +
                $"event={record?.EffectId.SourceEventId.ToString("N") ?? "none"} " +
                $"error={error ?? "unknown"}");

        private sealed class PermanentPersistentEffectException : Exception
        {
            internal PermanentPersistentEffectException(string message)
                : base(message)
            {
            }

            internal PermanentPersistentEffectException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}
