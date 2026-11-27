using NUnit.Framework;
using OrderService.Grpc.Configuration;

namespace OrderService.Tests.Unit.Grpc;

[TestFixture]
[Category("Unit")]
public sealed class YamlConfigLoaderTests
{
    private string _serviceRoot = null!;
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _serviceRoot = FindServiceRoot();
        _tempDir = Path.Combine(Path.GetTempPath(), $"order-service-config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestCase("local", "127.0.0.1:19092", "http://127.0.0.1:5002", 5001, 8081)]
    [TestCase("staging", "redpanda.mini-k8s-staging.svc.cluster.local:9092", "http://webhook-service.mini-k8s-staging.svc.cluster.local:5002", 5001, 8081)]
    [TestCase("production", "redpanda.mini-k8s-prod.svc.cluster.local:9092", "http://webhook-service.mini-k8s-prod.svc.cluster.local:5002", 5001, 8081)]
    [TestCase("sample-config", "127.0.0.1:19092", "http://127.0.0.1:5002", 5001, 8081)]
    public void TryLoad_BundledEnvironmentConfigs_ParseAndValidateSuccessfully(
        string envName,
        string expectedBootstrapServers,
        string expectedWebhookAddress,
        int expectedGrpcPort,
        int expectedHealthPort)
    {
        var env = new Dictionary<string, string?>
        {
            ["APP_ENV"] = envName
        };

        var result = YamlConfigLoader.TryLoad(_serviceRoot, key => env.GetValueOrDefault(key));

        Assert.That(result.IsSuccess, Is.True, () => string.Join("; ", result.Errors));
        Assert.That(result.Config, Is.Not.Null);
        Assert.That(result.Errors, Is.Empty);

        var config = result.Config!;
        Assert.That(config.Kafka.BootstrapServers, Is.EqualTo(expectedBootstrapServers));
        Assert.That(config.WebhookClient.Address, Is.EqualTo(expectedWebhookAddress));
        Assert.That(config.Grpc.Port, Is.EqualTo(expectedGrpcPort));
        Assert.That(config.Health.Port, Is.EqualTo(expectedHealthPort));

        var dict = config.ToConfigurationDictionary();
        Assert.That(dict["ConnectionStrings:OrderDb"], Is.EqualTo(config.ConnectionStrings.OrderDb));
        Assert.That(dict["Grpc:Port"], Is.EqualTo(expectedGrpcPort.ToString()));
        Assert.That(dict["Health:Port"], Is.EqualTo(expectedHealthPort.ToString()));
    }

    [Test]
    public void ResolveConfigPath_FollowsPriorityOrder_ConfigPathThenAppEnvThenAspNetCoreEnvThenLocal()
    {
        var customPath = Path.Combine(_tempDir, "mounted-config.yml");

        var fromConfigPath = YamlConfigLoader.ResolveConfigPath(
            _serviceRoot,
            key => key switch
            {
                "CONFIG_PATH" => customPath,
                "APP_ENV" => "staging",
                "ASPNETCORE_ENVIRONMENT" => "production",
                _ => null
            });
        Assert.That(fromConfigPath, Is.EqualTo(customPath));

        var fromAppEnv = YamlConfigLoader.ResolveConfigPath(
            _serviceRoot,
            key => key switch
            {
                "APP_ENV" => "  STAGING  ",
                "ASPNETCORE_ENVIRONMENT" => "production",
                _ => null
            });
        Assert.That(fromAppEnv, Is.EqualTo(Path.Combine(_serviceRoot, "config", "staging", "config.yml")));

        var fromAspNetCoreEnv = YamlConfigLoader.ResolveConfigPath(
            _serviceRoot,
            key => key == "ASPNETCORE_ENVIRONMENT" ? "Production" : null);
        Assert.That(fromAspNetCoreEnv, Is.EqualTo(Path.Combine(_serviceRoot, "config", "production", "config.yml")));

        var defaultLocal = YamlConfigLoader.ResolveConfigPath(_serviceRoot, _ => null);
        Assert.That(defaultLocal, Is.EqualTo(Path.Combine(_serviceRoot, "config", "local", "config.yml")));
    }

    [Test]
    public void TryLoad_EnvironmentVariables_OverrideYamlValues()
    {
        var env = new Dictionary<string, string?>
        {
            ["APP_ENV"] = "staging",
            ["ConnectionStrings__OrderDb"] = "Server=sql-override,1433;Database=orders;User Id=sa;Password=Secret@123456;Encrypt=True;TrustServerCertificate=True;",
            ["Kafka__BootstrapServers"] = "redpanda-override:9092",
            ["Kafka__Topic"] = "order.created.v2",
            ["Kafka__ConsumerGroupId"] = "custom-group",
            ["WebhookClient__Address"] = "https://webhook.internal:8443",
            ["WebhookClient__TimeoutSeconds"] = "25",
            ["Outbox__BatchSize"] = "250",
            ["Outbox__PollIntervalMs"] = "50",
            ["Database__ApplyMigrationsOnStartup"] = "true",
            ["Grpc__Port"] = "6001",
            ["Health__Port"] = "9081"
        };

        var result = YamlConfigLoader.TryLoad(_serviceRoot, key => env.GetValueOrDefault(key));

        Assert.That(result.IsSuccess, Is.True, () => string.Join("; ", result.Errors));
        var config = result.Config!;
        Assert.That(config.ConnectionStrings.OrderDb, Is.EqualTo(env["ConnectionStrings__OrderDb"]));
        Assert.That(config.Kafka.BootstrapServers, Is.EqualTo("redpanda-override:9092"));
        Assert.That(config.Kafka.Topic, Is.EqualTo("order.created.v2"));
        Assert.That(config.Kafka.ConsumerGroupId, Is.EqualTo("custom-group"));
        Assert.That(config.WebhookClient.Address, Is.EqualTo("https://webhook.internal:8443"));
        Assert.That(config.WebhookClient.TimeoutSeconds, Is.EqualTo(25));
        Assert.That(config.Outbox.BatchSize, Is.EqualTo(250));
        Assert.That(config.Outbox.PollIntervalMs, Is.EqualTo(50));
        Assert.That(config.Database.ApplyMigrationsOnStartup, Is.True);
        Assert.That(config.Grpc.Port, Is.EqualTo(6001));
        Assert.That(config.Health.Port, Is.EqualTo(9081));
    }

    [Test]
    public void TryLoad_MissingFile_ReturnsFailFastError()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.yml");

        var result = YamlConfigLoader.TryLoad(
            _serviceRoot,
            key => key == "CONFIG_PATH" ? missingPath : null);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Config, Is.Null);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0], Does.Contain("not found").IgnoreCase);
    }

    [Test]
    public void TryLoad_MalformedYaml_ReturnsFailFastError()
    {
        var malformedPath = Path.Combine(_tempDir, "malformed.yml");
        File.WriteAllText(malformedPath, "kafka:\n  bootstrapServers: [unclosed\n");

        var result = YamlConfigLoader.TryLoad(
            _serviceRoot,
            key => key == "CONFIG_PATH" ? malformedPath : null);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Config, Is.Null);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0], Does.Contain("Malformed YAML"));
    }

    [Test]
    public void TryLoad_IncompleteAndInvalidConfig_ReportsAllValidationErrors()
    {
        var invalidPath = Path.Combine(_tempDir, "invalid.yml");
        File.WriteAllText(
            invalidPath,
            """
            connectionStrings:
              orderDb: ""
            database:
              commandTimeoutSeconds: 0
              dbContextPoolSize: -1
            kafka:
              bootstrapServers: ""
              topic: ""
              consumerGroupId: ""
            outbox:
              batchSize: 0
              pollIntervalMs: 0
              maxRetries: 0
            webhookClient:
              address: "ftp://invalid-scheme:5002"
              timeoutSeconds: 0
            grpc:
              port: 5001
            health:
              port: 5001
            """);

        var result = YamlConfigLoader.TryLoad(
            _serviceRoot,
            key => key == "CONFIG_PATH" ? invalidPath : null);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Config, Is.Null);

        var combined = string.Join("\n", result.Errors);
        Assert.That(combined, Does.Contain("ConnectionStrings.OrderDb"));
        Assert.That(combined, Does.Contain("Kafka.BootstrapServers"));
        Assert.That(combined, Does.Contain("Kafka.Topic"));
        Assert.That(combined, Does.Contain("Kafka.ConsumerGroupId"));
        Assert.That(combined, Does.Contain("WebhookClient.Address"));
        Assert.That(combined, Does.Contain("WebhookClient.TimeoutSeconds"));
        Assert.That(combined, Does.Contain("Outbox.BatchSize"));
        Assert.That(combined, Does.Contain("Outbox.PollIntervalMs"));
        Assert.That(combined, Does.Contain("Outbox.MaxRetries"));
        Assert.That(combined, Does.Contain("Grpc.Port and Health.Port must not be equal"));
        Assert.That(combined, Does.Contain("Database.CommandTimeoutSeconds"));
        Assert.That(combined, Does.Contain("Database.DbContextPoolSize"));
    }

    [Test]
    public void TryLoad_InvalidNumericEnvOverride_ReturnsFailFastError()
    {
        var env = new Dictionary<string, string?>
        {
            ["APP_ENV"] = "local",
            ["Grpc__Port"] = "not-a-port"
        };

        var result = YamlConfigLoader.TryLoad(_serviceRoot, key => env.GetValueOrDefault(key));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("Grpc__Port"));
    }

    private static string FindServiceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrderService.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate OrderService.sln from test base directory.");
    }
}
