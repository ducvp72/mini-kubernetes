using OrderService.Domain.Entities;
using OrderService.Domain.Pagination;

namespace OrderService.Domain.Repositories;

public interface IOrderRepository : IGenericRepository<Order, ulong>
{
    Task<Order?> GetByOrderCodeAsync(string orderCode, CancellationToken ct = default);

    Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    Task<IReadOnlyList<OrderSummaryView>> ListSummariesByCursorAsync(
        string? customerId,
        int fetchLimit,
        CursorPosition? cursor,
        CursorDirection direction,
        CancellationToken ct = default);

    Task<bool> TryConfirmOrderAsync(ulong orderId, ulong expectedRowVersion, CancellationToken ct = default);
}
