using System.ComponentModel.DataAnnotations;

namespace OrderService.Application.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public bool ApplyMigrationsOnStartup { get; init; }

    [Range(1, 60)]
    public int CommandTimeoutSeconds { get; init; } = 5;

    [Range(1, 60)]
    public int MigrationMaxRetries { get; init; } = 15;

    [Range(100, 30000)]
    public int MigrationRetryDelayMs { get; init; } = 2000;

    [Range(1, 1024)]
    public int DbContextPoolSize { get; init; } = 128;
}

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    [Required]
    public string BootstrapServers { get; init; } = "localhost:19092";

    [Required]
    public string Topic { get; init; } = "order.created";

    [Required]
    public string ConsumerGroupId { get; init; } = "order-service-workers";

    [Range(0, 1000)]
    public int LingerMs { get; init; } = 5;

    [Range(1024, 16_777_216)]
    public int BatchSizeBytes { get; init; } = 65536;

    public bool EnableBackgroundWorkers { get; init; } = true;
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    [Range(10, 60000)]
    public int PollIntervalMs { get; init; } = 200;

    [Range(1, 50)]
    public int MaxRetries { get; init; } = 5;
}

public sealed class WebhookClientOptions
{
    public const string SectionName = "WebhookClient";

    [Required]
    [Url]
    public string Address { get; init; } = "http://localhost:5002";

    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 10;
}
