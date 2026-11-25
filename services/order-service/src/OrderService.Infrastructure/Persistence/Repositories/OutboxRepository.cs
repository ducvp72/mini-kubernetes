using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Repositories;

namespace OrderService.Infrastructure.Persistence.Repositories;

public sealed class OutboxRepository : GenericRepository<OutboxMessage, ulong>, IOutboxRepository
{
    public OutboxRepository(OrderDbContext dbContext)
        : base(dbContext)
    {
    }

    // Pattern: Outbox — invokes sp_claim_outbox_batch (FOR UPDATE SKIP LOCKED) inside an active transaction for lock-free concurrency.
    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "batchSize must be positive.");
        }

        var currentTransaction = DbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException("sp_claim_outbox_batch must execute inside an active database transaction.");

        var connection = DbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await DbContext.Database.OpenConnectionAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = currentTransaction.GetDbTransaction();
        command.CommandText = "CALL sp_claim_outbox_batch(@batchSize);";
        command.CommandType = CommandType.Text;

        var batchSizeParam = command.CreateParameter();
        batchSizeParam.ParameterName = "@batchSize";
        batchSizeParam.DbType = DbType.Int32;
        batchSizeParam.Value = batchSize;
        command.Parameters.Add(batchSizeParam);

        var claimed = new List<OutboxMessage>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Convert.ToUInt64(reader.GetValue(0));
            var orderId = Convert.ToUInt64(reader.GetValue(1));
            var eventType = reader.GetString(2);
            var partitionKey = reader.GetString(3);
            var payload = reader.GetString(4);
            var status = (OutboxMessageStatus)Convert.ToByte(reader.GetValue(5));
            var retryCount = Convert.ToByte(reader.GetValue(6));
            var createdAt = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);
            var processedAt = reader.IsDBNull(8)
                ? (DateTime?)null
                : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);

            claimed.Add(OutboxMessage.Rehydrate(
                id,
                orderId,
                eventType,
                partitionKey,
                payload,
                status,
                retryCount,
                createdAt,
                processedAt));
        }

        return claimed;
    }

    public async Task MarkPublishedAsync(ulong outboxMessageId, DateTime processedAtUtc, CancellationToken ct = default)
    {
        await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE outbox_messages
             SET status = {(byte)OutboxMessageStatus.Published},
                 processed_at = {processedAtUtc}
             WHERE id = {outboxMessageId}
             """,
            ct);
    }

    public async Task MarkRetryOrDeadLetterAsync(
        ulong outboxMessageId,
        byte nextRetryCount,
        OutboxMessageStatus nextStatus,
        DateTime? processedAtUtc,
        CancellationToken ct = default)
    {
        await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE outbox_messages
             SET retry_count = {nextRetryCount},
                 status = {(byte)nextStatus},
                 processed_at = {processedAtUtc}
             WHERE id = {outboxMessageId}
             """,
            ct);
    }
}
