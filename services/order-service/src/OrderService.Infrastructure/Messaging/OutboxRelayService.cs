using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Application.Logging;
using OrderService.Application.Options;
using OrderService.Application.Services;

namespace OrderService.Infrastructure.Messaging;

// Pattern: Outbox — managed BackgroundService creating a fresh DI scope per batch to prevent scoped DbContext capture.
public sealed class OutboxRelayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _outboxOptions;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<OutboxRelayService> _logger;

    public OutboxRelayService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> outboxOptions,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<OutboxRelayService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        ArgumentNullException.ThrowIfNull(kafkaOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _outboxOptions = outboxOptions.Value;
        _kafkaOptions = kafkaOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_kafkaOptions.EnableBackgroundWorkers)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_outboxOptions.PollIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedCount = 0;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxRelayProcessor>();
                processedCount = await processor.ProcessNextBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogOutboxRelayCycleError(ex);
            }

            // Drain full batches immediately without waiting for the idle poll interval.
            if (processedCount >= _outboxOptions.BatchSize)
            {
                continue;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
