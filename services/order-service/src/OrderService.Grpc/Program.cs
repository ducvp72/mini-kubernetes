using Microsoft.AspNetCore.Server.Kestrel.Core;
using OrderService.Grpc.Configuration;
using OrderService.Grpc.Extensions;
using OrderService.Grpc.Health;
using OrderService.Infrastructure.Persistence;

// Parse and strictly validate YAML + env-override configuration before starting Kestrel,
// running database migrations, or launching background workers.
var configResult = YamlConfigLoader.TryLoad();
if (!configResult.IsSuccess || configResult.Config is null)
{
    using var startupLoggerFactory = LoggerFactory.Create(static logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddSimpleConsole(static options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.UseUtcTimestamp = true;
        });
    });

    var startupLogger = startupLoggerFactory.CreateLogger("OrderService.Startup");
    startupLogger.LogCritical(
        "Startup aborted due to invalid configuration at {ConfigPath}. Errors ({ErrorCount}): {Errors}",
        configResult.ConfigPath,
        configResult.Errors.Count,
        string.Join(" | ", configResult.Errors));

    return 1;
}

var serviceConfig = configResult.Config;
var builder = WebApplication.CreateBuilder(args);

// Overlay the validated YAML + env-override configuration onto the host configuration
// so all IOptions<T> and DI registrations consume the exact validated values.
builder.Configuration.AddInMemoryCollection(serviceConfig.ToConfigurationDictionary());

var grpcPort = serviceConfig.Grpc.Port;
var healthPort = serviceConfig.Health.Port;

// Configure dual-port Kestrel: cleartext HTTP/2 (h2c) for internal gRPC and HTTP/1.1 for K8s health probes.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort, static listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
    options.ListenAnyIP(healthPort, static listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
});

if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(static options =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });
}

builder.Services
    .AddGlobalServices(builder.Configuration)
    .AddApplicationServices()
    .AddInfrastructureServices(builder.Configuration)
    .AddPresentationServices();

await using var app = builder.Build();

var migrationRunner = app.Services.GetRequiredService<DatabaseMigrationRunner>();
await migrationRunner.ApplyMigrationsIfEnabledAsync(app.Lifetime.ApplicationStopping);

var readiness = app.Services.GetRequiredService<ReadinessState>();
readiness.MarkReady();
app.Lifetime.ApplicationStopping.Register(readiness.MarkDraining);

app.MapOrderServiceEndpoints(serviceConfig.Grpc.EnableReflection);

await app.RunAsync();
return 0;

public partial class Program;
