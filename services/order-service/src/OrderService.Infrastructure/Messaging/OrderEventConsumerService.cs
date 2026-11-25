using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Application.Logging;
using OrderService.Application.Options;
using OrderService.Application.Services;

namespace OrderService.Infrastructure.Messaging;

// Pattern: Competing Consumers — manual-commit Kafka consumer delegating each event to a scoped OrderEventProcessor.
public sealed class OrderEventConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaOptions _kafkaOptions;
    private readonly ILogger<OrderEventConsumerService> _logger;

    public OrderEventConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<OrderEventConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(kafkaOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _kafkaOptions = kafkaOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_kafkaOptions.EnableBackgroundWorkers)
        {
            return;
        }

        // Yield immediately so blocking librdkafka polls never delay ASP.NET Core / Kestrel startup.
        await Task.Yield();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _kafkaOptions.BootstrapServers,
            GroupId = _kafkaOptions.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true,
            SessionTimeoutMs = 10000
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(_kafkaOptions.Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? consumeResult;
                try
                {
                    consumeResult = consumer.Consume(TimeSpan.FromMilliseconds(250));
                }
                catch (ConsumeException ex)
                {
                    _logger.LogKafkaConsumeError(ex.Error.Reason, ex);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                    continue;
                }

                if (consumeResult is null || consumeResult.IsPartitionEOF || consumeResult.Message is null)
                {
                    continue;
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OrderEventProcessor>();
                await processor.ProcessMessageAsync(
                    consumeResult.Message.Key ?? string.Empty,
                    consumeResult.Message.Value ?? string.Empty,
                    stoppingToken);

                consumer.StoreOffset(consumeResult);
                consumer.Commit(consumeResult);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful host shutdown requested.
        }
        finally
        {
            consumer.Close();
        }
    }
}
