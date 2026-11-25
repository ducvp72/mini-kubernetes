using System.Globalization;
using Grpc.Core;
using MiniK8s.Contracts.Order.V1;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Domain.Exceptions;
using DomainOrderStatus = OrderService.Domain.Enums.OrderStatus;
using ProtoOrderStatus = MiniK8s.Contracts.Order.V1.OrderStatus;

namespace OrderService.Grpc.Services;

public sealed class OrderGrpcService : MiniK8s.Contracts.Order.V1.OrderService.OrderServiceBase
{
    private readonly IOrderApplicationService _applicationService;

    public OrderGrpcService(IOrderApplicationService applicationService)
    {
        ArgumentNullException.ThrowIfNull(applicationService);
        _applicationService = applicationService;
    }

    public override async Task<CreateOrderResponse> CreateOrder(
        CreateOrderRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            if (string.IsNullOrWhiteSpace(request.UnitPrice) ||
                !decimal.TryParse(request.UnitPrice.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var unitPrice))
            {
                throw new DomainValidationException("unit_price must be a valid decimal string.");
            }

            var command = new CreateOrderCommand(
                request.IdempotencyKey,
                request.CustomerId,
                request.Sku,
                request.Quantity,
                unitPrice,
                string.IsNullOrWhiteSpace(request.CallbackUrl) ? null : request.CallbackUrl,
                request.SimulateWorkMs);

            var result = await _applicationService.CreateOrderAsync(command, context.CancellationToken);

            return new CreateOrderResponse
            {
                OrderCode = result.OrderCode,
                Status = MapStatus(result.Status),
                Deduplicated = result.Deduplicated,
                CreatedAtUnixMs = ToUnixMilliseconds(result.CreatedAtUtc)
            };
        }
        catch (Exception ex) when (GrpcStatusMapper.TryMapDomainException(ex, context.CancellationToken) is { } rpcEx)
        {
            throw rpcEx;
        }
    }

    public override async Task<GetOrderResponse> GetOrder(
        GetOrderRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var order = await _applicationService.GetOrderAsync(request.OrderCode, context.CancellationToken);

            return new GetOrderResponse
            {
                Order = new OrderItem
                {
                    OrderCode = order.OrderCode,
                    CustomerId = order.CustomerId,
                    Sku = order.Sku,
                    Quantity = order.Quantity,
                    UnitPrice = FormatDecimal(order.UnitPrice),
                    TotalAmount = FormatDecimal(order.TotalAmount),
                    Status = MapStatus(order.Status),
                    CallbackUrl = order.CallbackUrl ?? string.Empty,
                    CreatedAtUnixMs = ToUnixMilliseconds(order.CreatedAtUtc),
                    UpdatedAtUnixMs = ToUnixMilliseconds(order.UpdatedAtUtc)
                }
            };
        }
        catch (Exception ex) when (GrpcStatusMapper.TryMapDomainException(ex, context.CancellationToken) is { } rpcEx)
        {
            throw rpcEx;
        }
    }

    public override async Task<ListOrdersResponse> ListOrders(
        ListOrdersRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var pageSize = request.PageSize > int.MaxValue
                ? throw new DomainValidationException("page_size exceeds maximum supported range.")
                : (int)request.PageSize;

            var query = new ListOrdersQuery(
                string.IsNullOrWhiteSpace(request.CustomerId) ? null : request.CustomerId,
                pageSize,
                string.IsNullOrWhiteSpace(request.NextCursor) ? null : request.NextCursor,
                string.IsNullOrWhiteSpace(request.PrevCursor) ? null : request.PrevCursor);

            var result = await _applicationService.ListOrdersAsync(query, context.CancellationToken);

            var response = new ListOrdersResponse
            {
                NextCursor = result.NextCursor ?? string.Empty,
                PrevCursor = result.PrevCursor ?? string.Empty,
                HasNext = result.HasNext,
                HasPrev = result.HasPrev
            };

            foreach (var item in result.Items)
            {
                response.Items.Add(new OrderSummary
                {
                    OrderCode = item.OrderCode,
                    CustomerId = item.CustomerId,
                    Sku = item.Sku,
                    Quantity = item.Quantity,
                    UnitPrice = FormatDecimal(item.UnitPrice),
                    TotalAmount = FormatDecimal(item.TotalAmount),
                    Status = MapStatus(item.Status),
                    CreatedAtUnixMs = ToUnixMilliseconds(item.CreatedAtUtc)
                });
            }

            return response;
        }
        catch (Exception ex) when (GrpcStatusMapper.TryMapDomainException(ex, context.CancellationToken) is { } rpcEx)
        {
            throw rpcEx;
        }
    }

    internal static ProtoOrderStatus MapStatus(DomainOrderStatus status) => status switch
    {
        DomainOrderStatus.Pending => ProtoOrderStatus.Pending,
        DomainOrderStatus.Confirmed => ProtoOrderStatus.Confirmed,
        DomainOrderStatus.Failed => ProtoOrderStatus.Failed,
        _ => ProtoOrderStatus.Unspecified
    };

    private static string FormatDecimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static long ToUnixMilliseconds(DateTime utcDateTime)
    {
        var normalized = utcDateTime.Kind == DateTimeKind.Utc
            ? utcDateTime
            : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return new DateTimeOffset(normalized).ToUnixTimeMilliseconds();
    }
}
