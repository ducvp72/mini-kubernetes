using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Pagination;
using OrderService.Domain.Repositories;

namespace OrderService.Infrastructure.Persistence.Repositories;

public sealed class OrderRepository : GenericRepository<Order, ulong>, IOrderRepository
{
    private static readonly Func<OrderDbContext, string, CancellationToken, Task<Order?>> GetByOrderCodeCompiled =
        EF.CompileAsyncQuery(
            static (OrderDbContext db, string orderCode, CancellationToken _) =>
                db.Orders.AsNoTracking().FirstOrDefault(o => o.OrderCode == orderCode));

    private static readonly Func<OrderDbContext, string, CancellationToken, Task<Order?>> GetByIdempotencyKeyCompiled =
        EF.CompileAsyncQuery(
            static (OrderDbContext db, string idempotencyKey, CancellationToken _) =>
                db.Orders.AsNoTracking().FirstOrDefault(o => o.IdempotencyKey == idempotencyKey));

    public OrderRepository(OrderDbContext dbContext)
        : base(dbContext)
    {
    }

    public Task<Order?> GetByOrderCodeAsync(string orderCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderCode);
        return GetByOrderCodeCompiled(DbContext, orderCode, ct);
    }

    public Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return GetByIdempotencyKeyCompiled(DbContext, idempotencyKey, ct);
    }

    // Pattern: Keyset Pagination — SARGable tuple predicate against ALGORITHM=MERGE view vw_order_summary using composite indexes.
    public async Task<IReadOnlyList<OrderSummaryView>> ListSummariesByCursorAsync(
        string? customerId,
        int fetchLimit,
        CursorPosition? cursor,
        CursorDirection direction,
        CancellationToken ct = default)
    {
        if (fetchLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fetchLimit), "fetchLimit must be positive.");
        }

        IQueryable<OrderSummaryView> query;

        if (string.IsNullOrWhiteSpace(customerId))
        {
            if (cursor is null)
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     ORDER BY created_at DESC, id DESC
                     LIMIT {fetchLimit}
                     """);
            }
            else if (direction == CursorDirection.Forward)
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     WHERE created_at < {cursor.CreatedAtUtc}
                        OR (created_at = {cursor.CreatedAtUtc} AND id < {cursor.Id})
                     ORDER BY created_at DESC, id DESC
                     LIMIT {fetchLimit}
                     """);
            }
            else
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     WHERE created_at > {cursor.CreatedAtUtc}
                        OR (created_at = {cursor.CreatedAtUtc} AND id > {cursor.Id})
                     ORDER BY created_at ASC, id ASC
                     LIMIT {fetchLimit}
                     """);
            }
        }
        else
        {
            if (cursor is null)
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     WHERE customer_id = {customerId}
                     ORDER BY created_at DESC, id DESC
                     LIMIT {fetchLimit}
                     """);
            }
            else if (direction == CursorDirection.Forward)
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     WHERE customer_id = {customerId}
                       AND (created_at < {cursor.CreatedAtUtc}
                            OR (created_at = {cursor.CreatedAtUtc} AND id < {cursor.Id}))
                     ORDER BY created_at DESC, id DESC
                     LIMIT {fetchLimit}
                     """);
            }
            else
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM vw_order_summary
                     WHERE customer_id = {customerId}
                       AND (created_at > {cursor.CreatedAtUtc}
                            OR (created_at = {cursor.CreatedAtUtc} AND id > {cursor.Id}))
                     ORDER BY created_at ASC, id ASC
                     LIMIT {fetchLimit}
                     """);
            }
        }

        return await query.AsNoTracking().ToListAsync(ct);
    }

    // Pattern: Optimistic Concurrency — atomic conditional SQL update prevents race conditions and duplicate webhooks.
    public async Task<bool> TryConfirmOrderAsync(ulong orderId, ulong expectedRowVersion, CancellationToken ct = default)
    {
        var affected = await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders
             SET status = {(byte)OrderStatus.Confirmed},
                 row_version = row_version + 1,
                 updated_at = UTC_TIMESTAMP(6)
             WHERE id = {orderId}
               AND status = {(byte)OrderStatus.Pending}
               AND row_version = {expectedRowVersion}
             """,
            ct);

        return affected == 1;
    }
}
