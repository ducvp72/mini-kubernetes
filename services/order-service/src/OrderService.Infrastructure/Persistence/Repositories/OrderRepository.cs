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

    // Pattern: Keyset Pagination — SARGable predicate against dbo.vw_order_summary using composite indexes.
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
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     ORDER BY created_at DESC, id DESC
                     """);
            }
            else if (direction == CursorDirection.Forward)
            {
                var cursorId = checked((long)cursor.Id);
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     WHERE created_at < {cursor.CreatedAtUtc}
                        OR (created_at = {cursor.CreatedAtUtc} AND id < {cursorId})
                     ORDER BY created_at DESC, id DESC
                     """);
            }
            else
            {
                var cursorId = checked((long)cursor.Id);
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     WHERE created_at > {cursor.CreatedAtUtc}
                        OR (created_at = {cursor.CreatedAtUtc} AND id > {cursorId})
                     ORDER BY created_at ASC, id ASC
                     """);
            }
        }
        else
        {
            if (cursor is null)
            {
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     WHERE customer_id = {customerId}
                     ORDER BY created_at DESC, id DESC
                     """);
            }
            else if (direction == CursorDirection.Forward)
            {
                var cursorId = checked((long)cursor.Id);
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     WHERE customer_id = {customerId}
                       AND (created_at < {cursor.CreatedAtUtc}
                            OR (created_at = {cursor.CreatedAtUtc} AND id < {cursorId}))
                     ORDER BY created_at DESC, id DESC
                     """);
            }
            else
            {
                var cursorId = checked((long)cursor.Id);
                query = DbContext.OrderSummaries.FromSqlInterpolated(
                    $"""
                     SELECT TOP ({fetchLimit}) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
                     FROM dbo.vw_order_summary
                     WHERE customer_id = {customerId}
                       AND (created_at > {cursor.CreatedAtUtc}
                            OR (created_at = {cursor.CreatedAtUtc} AND id > {cursorId}))
                     ORDER BY created_at ASC, id ASC
                     """);
            }
        }

        return await query.AsNoTracking().ToListAsync(ct);
    }

    // Pattern: Optimistic Concurrency — atomic conditional SQL update prevents race conditions and duplicate webhooks.
    public async Task<bool> TryConfirmOrderAsync(ulong orderId, ulong expectedRowVersion, CancellationToken ct = default)
    {
        var id = checked((long)orderId);
        var expectedRv = checked((long)expectedRowVersion);

        var affected = await DbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE dbo.orders
             SET status = {(byte)OrderStatus.Confirmed},
                 row_version = row_version + 1,
                 updated_at = SYSUTCDATETIME()
             WHERE id = {id}
               AND status = {(byte)OrderStatus.Pending}
               AND row_version = {expectedRv}
             """,
            ct);

        return affected == 1;
    }
}
