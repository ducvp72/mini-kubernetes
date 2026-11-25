using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderService.Grpc.Configuration;

/// <summary>
/// Result of loading and validating the startup YAML configuration.
/// </summary>
public sealed record ConfigLoadResult(
    bool IsSuccess,
    string ConfigPath,
    OrderServiceConfig? Config,
    IReadOnlyList<string> Errors)
{
    public static ConfigLoadResult Success(string configPath, OrderServiceConfig config) =>
        new(true, configPath, config, Array.Empty<string>());

    public static ConfigLoadResult Failure(string configPath, IReadOnlyList<string> errors) =>
        new(false, configPath, null, errors);
}

/// <summary>
/// Resolves, parses, merges environment overrides, and strictly validates
/// OrderService YAML configuration prior to host startup.
/// </summary>
public static class YamlConfigLoader
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Resolves the YAML config file path using:
    /// 1. CONFIG_PATH env var (if set)
    /// 2. APP_ENV (falling back to ASPNETCORE_ENVIRONMENT, defaulting to "local")
    ///    -&gt; &lt;baseDir&gt;/config/&lt;env&gt;/config.yml
    /// </summary>
    public static string ResolveConfigPath(string baseDirectory, Func<string, string?>? getEnv = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var envLookup = getEnv ?? Environment.GetEnvironmentVariable;

        var explicitPath = envLookup("CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Trim();
        }

        var rawEnv = envLookup("APP_ENV");
        if (string.IsNullOrWhiteSpace(rawEnv))
        {
            rawEnv = envLookup("ASPNETCORE_ENVIRONMENT");
        }

        var normalizedEnv = string.IsNullOrWhiteSpace(rawEnv)
            ? "local"
            : rawEnv.Trim().ToLowerInvariant();

        return Path.Combine(baseDirectory, "config", normalizedEnv, "config.yml");
    }

    /// <summary>
    /// Resolves the base directory containing the config/ folder, preferring the current working directory
    /// and falling back to AppContext.BaseDirectory.
    /// </summary>
    public static string ResolveBaseDirectory()
    {
        var currentDir = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(currentDir, "config")))
        {
            return currentDir;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Loads, overrides via environment variables, and strictly validates the YAML configuration.
    /// Never throws for missing files, malformed YAML, or invalid config values.
    /// </summary>
    public static ConfigLoadResult TryLoad(string? baseDirectory = null, Func<string, string?>? getEnv = null)
    {
        var envLookup = getEnv ?? Environment.GetEnvironmentVariable;
        var resolvedBaseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? ResolveBaseDirectory()
            : baseDirectory;

        var configPath = ResolveConfigPath(resolvedBaseDir, envLookup);

        if (!File.Exists(configPath))
        {
            return ConfigLoadResult.Failure(
                configPath,
                [$"Configuration file not found at '{configPath}'."]);
        }

        string yamlContent;
        try
        {
            yamlContent = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigLoadResult.Failure(
                configPath,
                [$"Failed to read configuration file '{configPath}': {ex.Message}"]);
        }

        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            return ConfigLoadResult.Failure(
                configPath,
                [$"Configuration file '{configPath}' is empty."]);
        }

        OrderServiceConfig? config;
        try
        {
            config = Deserializer.Deserialize<OrderServiceConfig?>(yamlContent);
        }
        catch (YamlException ex)
        {
            return ConfigLoadResult.Failure(
                configPath,
                [$"Malformed YAML in '{configPath}': {ex.Message}"]);
        }

        config ??= new OrderServiceConfig();
        EnsureSectionsInitialized(config);

        var errors = new List<string>();
        ApplyEnvironmentOverrides(config, envLookup, errors);
        Validate(config, errors);

        return errors.Count == 0
            ? ConfigLoadResult.Success(configPath, config)
            : ConfigLoadResult.Failure(configPath, errors);
    }

    private static void EnsureSectionsInitialized(OrderServiceConfig config)
    {
        config.ConnectionStrings ??= new ConnectionStringsConfig();
        config.Database ??= new DatabaseConfig();
        config.Kafka ??= new KafkaConfig();
        config.Outbox ??= new OutboxConfig();
        config.WebhookClient ??= new WebhookClientConfig();
        config.Grpc ??= new GrpcConfig();
        config.Health ??= new PortConfig();
    }

    private static void ApplyEnvironmentOverrides(
        OrderServiceConfig config,
        Func<string, string?> getEnv,
        List<string> errors)
    {
        OverrideString(getEnv, "ConnectionStrings__OrderDb", v => config.ConnectionStrings.OrderDb = v);

        OverrideBool(getEnv, "Database__ApplyMigrationsOnStartup", "Database.ApplyMigrationsOnStartup", v => config.Database.ApplyMigrationsOnStartup = v, errors);
        OverrideInt(getEnv, "Database__CommandTimeoutSeconds", "Database.CommandTimeoutSeconds", v => config.Database.CommandTimeoutSeconds = v, errors);
        OverrideInt(getEnv, "Database__MigrationMaxRetries", "Database.MigrationMaxRetries", v => config.Database.MigrationMaxRetries = v, errors);
        OverrideInt(getEnv, "Database__MigrationRetryDelayMs", "Database.MigrationRetryDelayMs", v => config.Database.MigrationRetryDelayMs = v, errors);
        OverrideInt(getEnv, "Database__DbContextPoolSize", "Database.DbContextPoolSize", v => config.Database.DbContextPoolSize = v, errors);

        OverrideString(getEnv, "Kafka__BootstrapServers", v => config.Kafka.BootstrapServers = v);
        OverrideString(getEnv, "Kafka__Topic", v => config.Kafka.Topic = v);
        OverrideString(getEnv, "Kafka__ConsumerGroupId", v => config.Kafka.ConsumerGroupId = v);
        OverrideInt(getEnv, "Kafka__LingerMs", "Kafka.LingerMs", v => config.Kafka.LingerMs = v, errors);
        OverrideInt(getEnv, "Kafka__BatchSizeBytes", "Kafka.BatchSizeBytes", v => config.Kafka.BatchSizeBytes = v, errors);
        OverrideBool(getEnv, "Kafka__EnableBackgroundWorkers", "Kafka.EnableBackgroundWorkers", v => config.Kafka.EnableBackgroundWorkers = v, errors);

        OverrideInt(getEnv, "Outbox__BatchSize", "Outbox.BatchSize", v => config.Outbox.BatchSize = v, errors);
        OverrideInt(getEnv, "Outbox__PollIntervalMs", "Outbox.PollIntervalMs", v => config.Outbox.PollIntervalMs = v, errors);
        OverrideInt(getEnv, "Outbox__MaxRetries", "Outbox.MaxRetries", v => config.Outbox.MaxRetries = v, errors);

        OverrideString(getEnv, "WebhookClient__Address", v => config.WebhookClient.Address = v);
        OverrideInt(getEnv, "WebhookClient__TimeoutSeconds", "WebhookClient.TimeoutSeconds", v => config.WebhookClient.TimeoutSeconds = v, errors);

        OverrideInt(getEnv, "Grpc__Port", "Grpc.Port", v => config.Grpc.Port = v, errors);
        OverrideBool(getEnv, "Grpc__EnableReflection", "Grpc.EnableReflection", v => config.Grpc.EnableReflection = v, errors);
        OverrideInt(getEnv, "Health__Port", "Health.Port", v => config.Health.Port = v, errors);
    }

    private static void OverrideString(Func<string, string?> getEnv, string envVar, Action<string> setter)
    {
        var value = getEnv(envVar);
        if (value is not null)
        {
            setter(value.Trim());
        }
    }

    private static void OverrideInt(
        Func<string, string?> getEnv,
        string envVar,
        string fieldName,
        Action<int> setter,
        List<string> errors)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            setter(parsed);
        }
        else
        {
            errors.Add($"{fieldName}: environment variable '{envVar}' must be a valid integer (got '{raw}').");
        }
    }

    private static void OverrideBool(
        Func<string, string?> getEnv,
        string envVar,
        string fieldName,
        Action<bool> setter,
        List<string> errors)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (bool.TryParse(raw.Trim(), out var parsed))
        {
            setter(parsed);
        }
        else
        {
            errors.Add($"{fieldName}: environment variable '{envVar}' must be a valid boolean (got '{raw}').");
        }
    }

    private static void Validate(OrderServiceConfig config, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(config.ConnectionStrings.OrderDb))
        {
            errors.Add("ConnectionStrings.OrderDb must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(config.Kafka.BootstrapServers))
        {
            errors.Add("Kafka.BootstrapServers must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(config.Kafka.Topic))
        {
            errors.Add("Kafka.Topic must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(config.Kafka.ConsumerGroupId))
        {
            errors.Add("Kafka.ConsumerGroupId must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(config.WebhookClient.Address)
            || !Uri.TryCreate(config.WebhookClient.Address, UriKind.Absolute, out var webhookUri)
            || (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("WebhookClient.Address must be a valid absolute HTTP or HTTPS URI.");
        }

        if (config.WebhookClient.TimeoutSeconds <= 0)
        {
            errors.Add("WebhookClient.TimeoutSeconds must be greater than 0.");
        }

        if (config.Outbox.BatchSize <= 0)
        {
            errors.Add("Outbox.BatchSize must be greater than 0.");
        }

        if (config.Outbox.PollIntervalMs <= 0)
        {
            errors.Add("Outbox.PollIntervalMs must be greater than 0.");
        }

        if (config.Outbox.MaxRetries <= 0)
        {
            errors.Add("Outbox.MaxRetries must be greater than 0.");
        }

        var grpcPortValid = config.Grpc.Port is >= MinPort and <= MaxPort;
        if (!grpcPortValid)
        {
            errors.Add($"Grpc.Port must be between {MinPort} and {MaxPort}.");
        }

        var healthPortValid = config.Health.Port is >= MinPort and <= MaxPort;
        if (!healthPortValid)
        {
            errors.Add($"Health.Port must be between {MinPort} and {MaxPort}.");
        }

        if (grpcPortValid && healthPortValid && config.Grpc.Port == config.Health.Port)
        {
            errors.Add($"Grpc.Port and Health.Port must not be equal (both are {config.Grpc.Port}).");
        }

        if (config.Database.CommandTimeoutSeconds <= 0)
        {
            errors.Add("Database.CommandTimeoutSeconds must be greater than 0.");
        }

        if (config.Database.DbContextPoolSize <= 0)
        {
            errors.Add("Database.DbContextPoolSize must be greater than 0.");
        }
    }
}
