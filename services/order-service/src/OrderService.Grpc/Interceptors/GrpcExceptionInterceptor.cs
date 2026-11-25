using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using OrderService.Application.Logging;
using OrderService.Grpc.Services;

namespace OrderService.Grpc.Interceptors;

// Pattern: Interceptor — centralizes domain-to-gRPC status mapping and prevents leaking unhandled exception details.
public sealed class GrpcExceptionInterceptor : Interceptor
{
    private readonly ILogger<GrpcExceptionInterceptor> _logger;

    public GrpcExceptionInterceptor(ILogger<GrpcExceptionInterceptor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex)
        {
            var mapped = GrpcStatusMapper.TryMapDomainException(ex, context.CancellationToken);
            if (mapped is not null)
            {
                throw mapped;
            }

            _logger.LogUnhandledGrpcException(context.Method, ex);
            throw new RpcException(new Status(StatusCode.Internal, "An internal error occurred."));
        }
    }
}
