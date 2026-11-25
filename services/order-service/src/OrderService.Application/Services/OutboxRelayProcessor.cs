using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Application.Abstractions;
using OrderService.Application.Logging;
using OrderService.Application.Options;
using OrderService.Domain.Enums;
using OrderService.Domain.Repositories;

namespace OrderService.Application.Services;

// Pattern: Outbox — claims pending outbox batches via FOR UPDATE SKIP LOCKED and relays events to Redpanda.
public sealed class OutboxRelayProcessor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOrderEventPublisher _eventPublisher;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxRelayProcessor> _logger;

    public OutboxRelayProcessor(
        IUnitOfWork unitOfWork,
        IOutboxRepository outboxRepository,
        IOrderEventPublisher eventPublisher,
        IOptions<OutboxOptions> options,
        TimeProvider timeProvider,
        ILogger<OutboxRelayProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(outboxRepository);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _unitOfWork = unitOfWork;
        _outboxRepository = outboxRepository;
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ProcessNextBatchAsync(CancellationToken ct = default)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            var messages = await _outboxRepository.ClaimPendingBatchAsync(_options.BatchSize, ct);
            if (messages.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await _eventPublisher.PublishAsync(message.EventType, message.PartitionKey, message.Payload, ct);

                    var publishedAt = _timeProvider.GetUtcNow().UtcDateTime;
                    await _outboxRepository.MarkPublishedAsync(message.Id, publishedAt, ct);
                    _logger.LogOutboxMessagePublished(message.Id, message.PartitionKey, message.EventType);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    message.RecordFailure(_options.MaxRetries, nowUtc);

                    await _outboxRepository.MarkRetryOrDeadLetterAsync(
                        message.Id,
                        message.RetryCount,
                        message.Status,
                        message.ProcessedAt,
                        ct);

                    _logger.LogOutboxMessageFailed(message.Id, message.RetryCount, message.Status.ToString(), ex);
                }
            }

            await transaction.CommitAsync(ct);
            return messages.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
