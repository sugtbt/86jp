using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mail
{
    internal enum SystemMailEnqueueStatus
    {
        Rejected = 0,
        Enqueued,
        AlreadyExists,
    }

    internal readonly struct SystemMailEnqueueResult
    {
        public SystemMailEnqueueResult(
            SystemMailEnqueueStatus status,
            string error = null)
        {
            Status = status;
            Error = error;
        }

        public SystemMailEnqueueStatus Status { get; }
        public string Error { get; }
        public bool Success =>
            Status == SystemMailEnqueueStatus.Enqueued
            || Status == SystemMailEnqueueStatus.AlreadyExists;
    }

    internal sealed class SystemMailItemAttachment
    {
        public byte[] ItemCore { get; set; }
        public int Quantity { get; set; }
    }

    internal sealed class SystemMailMessage
    {
        public string SourceKey { get; set; }
        public int RecipientAccountId { get; set; }
        public int RecipientCharacterId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public long Gold { get; set; }
        public IReadOnlyList<SystemMailItemAttachment> Items { get; set; } =
            Array.Empty<SystemMailItemAttachment>();
    }

    /// <summary>
    /// Transactional boundary supplied by the mail module. Implementations must
    /// persist the message on the supplied connection and transaction, and treat
    /// SourceKey as an idempotency key.
    /// </summary>
    internal interface ISystemMailService
    {
        SystemMailEnqueueResult Enqueue(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SystemMailMessage message);
    }
}
