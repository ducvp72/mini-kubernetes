using OrderService.Domain.Entities;
using OrderService.Domain.Enums;

namespace OrderService.Domain.Repositories;

public interface IOutboxRepository : IGenericRepository<OutboxMessage, ulong>
{
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(int batchSize, CancellationToken ct = default);

    Task MarkPublishedAsync(ulong outboxMessageId, DateTime processedAtUtc, CancellationToken ct = default);

    Task MarkRetryOrDeadLetterAsync(
        ulong outboxMessageId,
        byte nextRetryCount,
        OutboxMessageStatus nextStatus,
        DateTime? processedAtUtc,
        CancellationToken ct = default);
}
