using OrderService.Application.Abstractions;
using OrderService.Application.Pagination;
using OrderService.Application.Services;

namespace OrderService.Grpc.Extensions;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICursorTokenCodec, CursorTokenCodec>();
        services.AddScoped<IOrderApplicationService, OrderApplicationService>();
        services.AddScoped<OutboxRelayProcessor>();
        services.AddScoped<OrderEventProcessor>();

        return services;
    }
}
