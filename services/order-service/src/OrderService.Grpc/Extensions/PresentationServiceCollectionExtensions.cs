using Microsoft.OpenApi.Models;
using OrderService.Grpc.Health;
using OrderService.Grpc.Interceptors;
using OrderService.Grpc.Services;

namespace OrderService.Grpc.Extensions;

public static class PresentationServiceCollectionExtensions
{
    public const string SwaggerDocumentName = "v1";
    public const string SwaggerRoutePrefix = "swagger";

    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGrpc(options =>
        {
            options.Interceptors.Add<GrpcExceptionInterceptor>();
            options.MaxReceiveMessageSize = 1 * 1024 * 1024;
            options.MaxSendMessageSize = 4 * 1024 * 1024;
        });

        services.AddGrpcReflection();

        return services;
    }

    public static IServiceCollection AddGrpcSwagger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(static options =>
        {
            options.SwaggerDoc(SwaggerDocumentName, new OpenApiInfo
            {
                Title = "Mini-Kubernetes Order Service (gRPC)",
                Version = SwaggerDocumentName,
                Description = "gRPC contract reference (abstractions, request/response schemas, and JSON templates) for order.v1.OrderService plus HTTP health probes."
            });
            options.DocumentFilter<GrpcSwaggerDocumentFilter>();
        });

        return services;
    }

    public static WebApplication MapOrderServiceEndpoints(
        this WebApplication app,
        bool enableReflection = true,
        bool enableSwagger = true)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (enableSwagger)
        {
            app.UseSwagger();
            app.UseSwaggerUI(static options =>
            {
                options.SwaggerEndpoint($"/swagger/{SwaggerDocumentName}/swagger.json", $"Order Service gRPC {SwaggerDocumentName}");
                options.RoutePrefix = SwaggerRoutePrefix;
            });
        }

        app.MapGrpcService<OrderGrpcService>();
        if (enableReflection)
        {
            app.MapGrpcReflectionService();
        }

        // Liveness probe: strictly process-alive only (zero DB/Kafka I/O to avoid cascading K8s restarts).
        app.MapGet("/healthz", static () => Results.Ok(new { status = "alive" }));

        // Readiness probe: returns 200 when ready and flips to 503 immediately on SIGTERM drain.
        app.MapGet("/readyz", static (ReadinessState readiness) =>
            readiness.IsReady
                ? Results.Ok(new { status = readiness.StatusLabel })
                : Results.Json(new { status = readiness.StatusLabel }, statusCode: StatusCodes.Status503ServiceUnavailable));

        return app;
    }
}
