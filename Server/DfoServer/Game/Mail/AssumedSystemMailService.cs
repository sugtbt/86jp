using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mail
{
    /// <summary>
    /// Temporary PR1 composition seam used while the real mail module is
    /// treated as an already-provided dependency. It deliberately does not
    /// create mailbox rows; it only validates the transactional call and
    /// models source-key idempotency for the current server process.
    /// </summary>
    internal sealed class AssumedSystemMailService : ISystemMailService
    {
        private readonly object _sync = new object();
        private readonly HashSet<string> _acceptedSourceKeys =
            new HashSet<string>(StringComparer.Ordinal);

        public SystemMailEnqueueResult Enqueue(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SystemMailMessage message)
        {
            var validationError = Validate(
                connection,
                transaction,
                message);
            if (validationError != null)
            {
                return new SystemMailEnqueueResult(
                    SystemMailEnqueueStatus.Rejected,
                    validationError);
            }

            lock (_sync)
            {
                if (!_acceptedSourceKeys.Add(message.SourceKey))
                {
                    return new SystemMailEnqueueResult(
                        SystemMailEnqueueStatus.AlreadyExists);
                }
            }

            FileLogger.Log(
                $"[MailAssumed] accepted sourceKey={message.SourceKey} recipientAid={message.RecipientAccountId} recipientCid={message.RecipientCharacterId} gold={message.Gold} items={message.Items.Count}");
            return new SystemMailEnqueueResult(
                SystemMailEnqueueStatus.Enqueued);
        }

        private static string Validate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SystemMailMessage message)
        {
            if (connection == null)
                return "connection is required";
            if (transaction == null
                || !ReferenceEquals(transaction.Connection, connection))
            {
                return "transaction must belong to the supplied connection";
            }
            if (message == null)
                return "message is required";
            if (string.IsNullOrWhiteSpace(message.SourceKey))
                return "source key is required";
            if (message.RecipientAccountId <= 0
                || message.RecipientCharacterId <= 0)
            {
                return "recipient identity is invalid";
            }
            if (message.Gold < 0)
                return "gold cannot be negative";
            if (message.Items == null)
                return "items are required";

            foreach (var item in message.Items)
            {
                if (item == null
                    || item.ItemCore == null
                    || item.ItemCore.Length == 0
                    || item.Quantity <= 0)
                {
                    return "item attachment is invalid";
                }
            }

            return null;
        }
    }
}
