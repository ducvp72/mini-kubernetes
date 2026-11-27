using Microsoft.EntityFrameworkCore;
using MiniK8s.Contracts.Webhook.V1;
using OrderService.Application.Abstractions;
using OrderService.Application.Options;
using OrderService.Domain.Repositories;
using OrderService.Infrastructure.Grpc;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;

namespace OrderService.Grpc.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("OrderDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:OrderDb is required.");
        }

        var dbOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var webhookOptions = configuration.GetSection(WebhookClientOptions.SectionName).Get<WebhookClientOptions>() ?? new WebhookClientOptions();

        services.AddDbContextPool<OrderDbContext>(
            options => options.UseSqlServer(
                connectionString,
                sql =>
                {
                    sql.CommandTimeout(dbOptions.CommandTimeoutSeconds);
                    sql.MigrationsAssembly(typeof(OrderDbContext).Assembly.FullName);
                }),
            poolSize: dbOptions.DbContextPoolSize);

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddSingleton<DatabaseMigrationRunner>();
        services.AddSingleton<IOrderEventPublisher, KafkaOrderEventPublisher>();

        // Enable cleartext HTTP/2 (h2c) for internal gRPC communication with Go webhook-service.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        services.AddGrpcClient<WebhookService.WebhookServiceClient>(options =>
            {
                options.Address = new Uri(webhookOptions.Address);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            });

        services.AddScoped<IWebhookDispatcher, GrpcWebhookDispatcher>();

        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<OrderEventConsumerService>();

        return services;
    }
}
