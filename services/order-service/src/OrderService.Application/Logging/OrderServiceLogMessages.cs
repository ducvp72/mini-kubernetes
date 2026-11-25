using Microsoft.Extensions.Logging;

namespace OrderService.Application.Logging;

public static partial class OrderServiceLogMessages
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Created order {OrderCode} for customer {CustomerId} with SKU {Sku} (Qty={Quantity})")]
    public static partial void LogOrderCreated(this ILogger logger, string orderCode, string customerId, string sku, uint quantity);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Deduplicated create order request for idempotency key returning existing order {OrderCode}")]
    public static partial void LogOrderDeduplicated(this ILogger logger, string orderCode);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Transient database deadlock during order creation (attempt {Attempt}); retrying")]
    public static partial void LogOrderCreateDeadlockRetry(this ILogger logger, int attempt, Exception exception);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Outbox relay published message {OutboxId} for key {PartitionKey} to event type {EventType}")]
    public static partial void LogOutboxMessagePublished(this ILogger logger, ulong outboxId, string partitionKey, string eventType);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Outbox relay failed for message {OutboxId} (retry {RetryCount}, nextStatus={NextStatus})")]
    public static partial void LogOutboxMessageFailed(this ILogger logger, ulong outboxId, byte retryCount, string nextStatus, Exception exception);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Outbox relay cycle encountered an unhandled error")]
    public static partial void LogOutboxRelayCycleError(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Confirmed order {OrderCode} (OrderId={OrderId})")]
    public static partial void LogOrderConfirmed(this ILogger logger, string orderCode, ulong orderId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Skipped duplicate or already-processed order event for {OrderCode} (OrderId={OrderId})")]
    public static partial void LogOrderEventSkippedDuplicate(this ILogger logger, string orderCode, ulong orderId);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Order {OrderCode} has no callbackUrl configured; skipping webhook dispatch")]
    public static partial void LogWebhookSkippedEmptyUrl(this ILogger logger, string orderCode);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Webhook delivered for order {OrderCode} (HttpStatus={HttpStatusCode}, Attempts={Attempts})")]
    public static partial void LogWebhookDelivered(this ILogger logger, string orderCode, int httpStatusCode, int attempts);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Webhook delivery failed for order {OrderCode} (HttpStatus={HttpStatusCode}, Attempts={Attempts}, Error={ErrorMessage})")]
    public static partial void LogWebhookDeliveryFailed(this ILogger logger, string orderCode, int httpStatusCode, int attempts, string errorMessage);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "Discarded malformed order event message for key {MessageKey}")]
    public static partial void LogMalformedOrderEvent(this ILogger logger, string messageKey, Exception? exception = null);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Kafka consumer encountered a consume error: {Reason}")]
    public static partial void LogKafkaConsumeError(this ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Applying EF Core MySQL migrations (attempt {Attempt}/{MaxAttempts})")]
    public static partial void LogApplyingMigrations(this ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "EF Core MySQL migrations applied successfully")]
    public static partial void LogMigrationsApplied(this ILogger logger);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Migration attempt {Attempt}/{MaxAttempts} failed; retrying in {DelayMs}ms")]
    public static partial void LogMigrationRetry(this ILogger logger, int attempt, int maxAttempts, int delayMs, Exception exception);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Unhandled exception processing gRPC method {Method}")]
    public static partial void LogUnhandledGrpcException(this ILogger logger, string method, Exception exception);
}
