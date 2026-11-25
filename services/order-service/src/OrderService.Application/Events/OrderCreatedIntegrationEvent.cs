namespace OrderService.Application.Events;

public static class OrderEventConstants
{
    public const string OrderCreatedEventType = "order.created";
}

public sealed record OrderCreatedIntegrationEvent(
    Guid EventId,
    ulong OrderId,
    string OrderCode,
    string CustomerId,
    string Sku,
    uint Quantity,
    decimal UnitPrice,
    string? CallbackUrl,
    uint SimulateWorkMs,
    ulong ExpectedRowVersion,
    DateTime CreatedAtUtc);
