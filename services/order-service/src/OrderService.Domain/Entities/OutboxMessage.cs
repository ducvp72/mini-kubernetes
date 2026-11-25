using OrderService.Domain.Common;
using OrderService.Domain.Enums;
using OrderService.Domain.Exceptions;

namespace OrderService.Domain.Entities;

// Pattern: Outbox — represents an integration event persisted atomically with its source Order transaction.
public sealed class OutboxMessage : BaseEntity<ulong>
{
    public const int MaxEventTypeLength = 64;
    public const int MaxPartitionKeyLength = 64;

    private OutboxMessage()
    {
    }

    public ulong OrderId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string PartitionKey { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public OutboxMessageStatus Status { get; private set; } = OutboxMessageStatus.Pending;

    public byte RetryCount { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public static OutboxMessage Create(
        ulong orderId,
        string eventType,
        string partitionKey,
        string payload,
        DateTime utcNow)
    {
        if (orderId == 0)
        {
            throw new DomainValidationException("Outbox orderId must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(eventType) || eventType.Trim().Length > MaxEventTypeLength)
        {
            throw new DomainValidationException($"Outbox eventType is required and cannot exceed {MaxEventTypeLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(partitionKey) || partitionKey.Trim().Length > MaxPartitionKeyLength)
        {
            throw new DomainValidationException($"Outbox partitionKey is required and cannot exceed {MaxPartitionKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new DomainValidationException("Outbox payload is required.");
        }

        var timestamp = TimestampPrecision.ToUtcMicroseconds(utcNow);
        return new OutboxMessage
        {
            OrderId = orderId,
            EventType = eventType.Trim(),
            PartitionKey = partitionKey.Trim(),
            Payload = payload,
            Status = OutboxMessageStatus.Pending,
            RetryCount = 0,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            ProcessedAt = null
        };
    }

    public static OutboxMessage Rehydrate(
        ulong id,
        ulong orderId,
        string eventType,
        string partitionKey,
        string payload,
        OutboxMessageStatus status,
        byte retryCount,
        DateTime createdAt,
        DateTime? processedAt)
    {
        var normalizedCreated = TimestampPrecision.ToUtcMicroseconds(createdAt);
        return new OutboxMessage
        {
            Id = id,
            OrderId = orderId,
            EventType = eventType,
            PartitionKey = partitionKey,
            Payload = payload,
            Status = status,
            RetryCount = retryCount,
            CreatedAt = normalizedCreated,
            UpdatedAt = normalizedCreated,
            ProcessedAt = processedAt.HasValue ? TimestampPrecision.ToUtcMicroseconds(processedAt.Value) : null
        };
    }

    public void MarkPublished(DateTime utcNow)
    {
        Status = OutboxMessageStatus.Published;
        ProcessedAt = TimestampPrecision.ToUtcMicroseconds(utcNow);
    }

    public void RecordFailure(int maxRetries, DateTime utcNow)
    {
        if (maxRetries <= 0)
        {
            throw new DomainValidationException("maxRetries must be positive.");
        }

        RetryCount = RetryCount == byte.MaxValue ? byte.MaxValue : (byte)(RetryCount + 1);
        if (RetryCount >= maxRetries)
        {
            Status = OutboxMessageStatus.DeadLetter;
            ProcessedAt = TimestampPrecision.ToUtcMicroseconds(utcNow);
        }
    }
}
