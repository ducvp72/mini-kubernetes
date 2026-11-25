using OrderService.Domain.Enums;

namespace OrderService.Application.Dtos;

public sealed record CreateOrderCommand(
    string IdempotencyKey,
    string CustomerId,
    string Sku,
    uint Quantity,
    decimal UnitPrice,
    string? CallbackUrl,
    uint SimulateWorkMs);

public sealed record CreateOrderResult(
    string OrderCode,
    OrderStatus Status,
    bool Deduplicated,
    DateTime CreatedAtUtc);

public sealed record OrderDetailsDto(
    string OrderCode,
    string CustomerId,
    string Sku,
    uint Quantity,
    decimal UnitPrice,
    decimal TotalAmount,
    OrderStatus Status,
    string? CallbackUrl,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ListOrdersQuery(
    string? CustomerId,
    int PageSize,
    string? NextCursor,
    string? PrevCursor);

public sealed record OrderSummaryDto(
    string OrderCode,
    string CustomerId,
    string Sku,
    uint Quantity,
    decimal UnitPrice,
    decimal TotalAmount,
    OrderStatus Status,
    DateTime CreatedAtUtc);

public sealed record ListOrdersResult(
    IReadOnlyList<OrderSummaryDto> Items,
    string? NextCursor,
    string? PrevCursor,
    bool HasNext,
    bool HasPrev);

public sealed record WebhookCallbackRequest(
    string OrderCode,
    string Status,
    string CallbackUrl,
    string CorrelationId,
    long EventTimestampUnixMs);

public sealed record WebhookDispatchResult(
    bool Delivered,
    int HttpStatusCode,
    int Attempts,
    string ErrorMessage);
