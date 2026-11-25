using Grpc.Core;
using OrderService.Domain.Exceptions;

namespace OrderService.Grpc.Services;

public static class GrpcStatusMapper
{
    public static RpcException? TryMapDomainException(Exception exception, CancellationToken ct)
    {
        return exception switch
        {
            RpcException rpcEx => rpcEx,
            DomainValidationException validationEx => new RpcException(new Status(StatusCode.InvalidArgument, validationEx.Message)),
            ArgumentException argEx => new RpcException(new Status(StatusCode.InvalidArgument, argEx.Message)),
            OrderNotFoundException notFoundEx => new RpcException(new Status(StatusCode.NotFound, notFoundEx.Message)),
            ConcurrencyConflictException conflictEx => new RpcException(new Status(StatusCode.Aborted, conflictEx.Message)),
            TimeoutException timeoutEx => new RpcException(new Status(StatusCode.DeadlineExceeded, timeoutEx.Message)),
            OperationCanceledException when ct.IsCancellationRequested => new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled.")),
            _ => null
        };
    }
}
