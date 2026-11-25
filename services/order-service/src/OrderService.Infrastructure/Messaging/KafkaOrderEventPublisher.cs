using Confluent.Kafka;
using Microsoft.Extensions.Options;
using OrderService.Application.Abstractions;
using OrderService.Application.Options;

namespace OrderService.Infrastructure.Messaging;

// Pattern: Adapter — wraps a singleton LZ4-compressed, batched Confluent.Kafka producer for Redpanda publishing.
public sealed class KafkaOrderEventPublisher : IOrderEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _defaultTopic;
    private bool _disposed;

    public KafkaOrderEventPublisher(IOptions<KafkaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var kafkaOptions = options.Value;
        _defaultTopic = kafkaOptions.Topic;

        var config = new ProducerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            CompressionType = CompressionType.Lz4,
            LingerMs = kafkaOptions.LingerMs,
            BatchSize = kafkaOptions.BatchSizeBytes,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(string eventType, string partitionKey, string payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var topic = string.IsNullOrWhiteSpace(eventType) ? _defaultTopic : eventType;
        var message = new Message<string, string>
        {
            Key = partitionKey,
            Value = payload
        };

        await _producer.ProduceAsync(topic, message, ct);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            // Producer already disposed during shutdown.
        }
        finally
        {
            _producer.Dispose();
        }
    }
}
