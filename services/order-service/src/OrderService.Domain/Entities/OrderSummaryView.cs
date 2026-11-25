using OrderService.Domain.Enums;

namespace OrderService.Domain.Entities;

/// <summary>
/// Keyless read-only projection backed by MySQL ALGORITHM=MERGE view vw_order_summary.
/// </summary>
public sealed class OrderSummaryView
{
    public ulong Id { get; init; }

    public string OrderCode { get; init; } = string.Empty;

    public string CustomerId { get; init; } = string.Empty;

    public string Sku { get; init; } = string.Empty;

    public uint Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public decimal TotalAmount { get; init; }

    public OrderStatus Status { get; init; }

    public DateTime CreatedAt { get; init; }
}
