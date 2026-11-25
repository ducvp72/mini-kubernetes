using System.Globalization;

namespace OrderService.Grpc.Configuration;

/// <summary>
/// Strongly-typed startup configuration parsed from config/&lt;env&gt;/config.yml
/// and merged with environment variable overrides.
/// </summary>
public sealed class OrderServiceConfig
{
    public ConnectionStringsConfig ConnectionStrings { get; set; } = new();

    public DatabaseConfig Database { get; set; } = new();

    public KafkaConfig Kafka { get; set; } = new();

    public OutboxConfig Outbox { get; set; } = new();

    public WebhookClientConfig WebhookClient { get; set; } = new();

    public GrpcConfig Grpc { get; set; } = new();

    public PortConfig Health { get; set; } = new();

    /// <summary>
    /// Converts the validated configuration into standard .NET configuration keys
    /// for seamless binding into existing IOptions&lt;T&gt; and DI registrations.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ToConfigurationDictionary()
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:OrderDb"] = ConnectionStrings.OrderDb,
            ["Database:ApplyMigrationsOnStartup"] = Database.ApplyMigrationsOnStartup.ToString(),
            ["Database:CommandTimeoutSeconds"] = Database.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["Database:MigrationMaxRetries"] = Database.MigrationMaxRetries.ToString(CultureInfo.InvariantCulture),
            ["Database:MigrationRetryDelayMs"] = Database.MigrationRetryDelayMs.ToString(CultureInfo.InvariantCulture),
            ["Database:DbContextPoolSize"] = Database.DbContextPoolSize.ToString(CultureInfo.InvariantCulture),
            ["Kafka:BootstrapServers"] = Kafka.BootstrapServers,
            ["Kafka:Topic"] = Kafka.Topic,
            ["Kafka:ConsumerGroupId"] = Kafka.ConsumerGroupId,
            ["Kafka:LingerMs"] = Kafka.LingerMs.ToString(CultureInfo.InvariantCulture),
            ["Kafka:BatchSizeBytes"] = Kafka.BatchSizeBytes.ToString(CultureInfo.InvariantCulture),
            ["Kafka:EnableBackgroundWorkers"] = Kafka.EnableBackgroundWorkers.ToString(),
            ["Outbox:BatchSize"] = Outbox.BatchSize.ToString(CultureInfo.InvariantCulture),
            ["Outbox:PollIntervalMs"] = Outbox.PollIntervalMs.ToString(CultureInfo.InvariantCulture),
            ["Outbox:MaxRetries"] = Outbox.MaxRetries.ToString(CultureInfo.InvariantCulture),
            ["WebhookClient:Address"] = WebhookClient.Address,
            ["WebhookClient:TimeoutSeconds"] = WebhookClient.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["Grpc:Port"] = Grpc.Port.ToString(CultureInfo.InvariantCulture),
            ["Grpc:EnableReflection"] = Grpc.EnableReflection.ToString(),
            ["Health:Port"] = Health.Port.ToString(CultureInfo.InvariantCulture)
        };
    }
}

public sealed class ConnectionStringsConfig
{
    public string? OrderDb { get; set; }
}

public sealed class DatabaseConfig
{
    public bool ApplyMigrationsOnStartup { get; set; }

    public int CommandTimeoutSeconds { get; set; }

    public int MigrationMaxRetries { get; set; } = 15;

    public int MigrationRetryDelayMs { get; set; } = 2000;

    public int DbContextPoolSize { get; set; }
}

public sealed class KafkaConfig
{
    public string? BootstrapServers { get; set; }

    public string? Topic { get; set; }

    public string? ConsumerGroupId { get; set; }

    public int LingerMs { get; set; } = 5;

    public int BatchSizeBytes { get; set; } = 65536;

    public bool EnableBackgroundWorkers { get; set; } = true;
}

public sealed class OutboxConfig
{
    public int BatchSize { get; set; }

    public int PollIntervalMs { get; set; }

    public int MaxRetries { get; set; }
}

public sealed class WebhookClientConfig
{
    public string? Address { get; set; }

    public int TimeoutSeconds { get; set; }
}

public sealed class GrpcConfig
{
    public int Port { get; set; }

    public bool EnableReflection { get; set; } = true;
}

public sealed class PortConfig
{
    public int Port { get; set; }
}
