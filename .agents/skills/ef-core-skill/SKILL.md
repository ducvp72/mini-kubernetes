---
name: ef-core-skill
description: Use when writing EF Core models, repositories, LINQ queries, or raw SQL against MySQL in .NET 8 services. Covers 200k RPS query optimization, N+1 prevention, SQL injection safety, resource disposal, generic base repository, and concurrency/deadlock prevention.
---

# EF Core & High-Throughput SQL (.NET 8 + MySQL)

Patterns for building fast, safe, and deadlock-resistant data access for high-load workloads (1M users / 200k RPS mindset) in `mini-kubernetes`.

## 1. High-Throughput Query Performance

- **Pool `DbContext`**: Register with `AddDbContextPool<AppDbContext>(..., poolSize: 128)` to avoid per-request context initialization overhead. Keep `DbContext` short-lived (one unit of work per request).
- **Read-only queries**: Always chain `.AsNoTracking()` on read queries to skip change-tracker snapshot overhead.
- **Project early**: Use `.Select(x => new OrderSummaryDto(...))` to fetch only required columns — never load full entities when returning DTOs.
- **Compiled queries on hot paths**: Use `EF.CompileAsyncQuery` for high-frequency point lookups (e.g., `GetOrderById`).
- **Batch writes**: Rely on EF Core batching (`SaveChangesAsync` once per unit of work) and `ExecuteUpdateAsync` / `ExecuteDeleteAsync` for bulk set-based updates without loading entities into memory.

## 2. Prevent N+1 Queries (LINQ & Raw SQL)

Never issue queries inside a `foreach` loop.
- **LINQ**: Project directly with `.Select(...)` (preferred) or load relations explicitly with `.Include(...)`. For multiple collection navigations, use `.AsSplitQuery()` to prevent Cartesian explosion.
- **Raw SQL**: Use explicit `JOIN`s or a single batched `WHERE id IN (...)` query rather than per-row lookups.

## 3. Deliberate Loading Strategy

Choose loading strategy strictly per use case:

| Strategy | When to Use | When to Avoid |
| --- | --- | --- |
| **Projection (`.Select`)** | Default for all read APIs & hot paths | When full domain aggregate mutation is required |
| **Eager (`.Include`)** | Loading an aggregate root + small bounded children for a write operation | Deep/wide graphs without `.AsSplitQuery()` |
| **Explicit (`Entry.Collection.LoadAsync`)** | Conditional loading based on runtime branch | Inside loops (causes N+1) |
| **Lazy Loading** | Avoid in high-throughput APIs | **Always avoid on hot paths** (hidden N+1 & unexpected DB round-trips) |

## 4. Zero SQL Injection (Public REST & Internal gRPC)

Treat **every** input — including internal gRPC payloads and Kafka messages — as untrusted.
- **LINQ**: Parameterized automatically.
- **EF Core Raw SQL**: Use `FromSqlInterpolated($"SELECT ... WHERE id = {id}")` or `ExecuteSqlInterpolatedAsync(...)` (EF converts interpolated values into `DbParameter`s).
- **ADO.NET / Dapper**: Always use named `DbParameter` / anonymous parameter objects.
- **Forbidden**: `FromSqlRaw($"... {userInput}")`, string concatenation, or unvalidated dynamic `ORDER BY` (use a strict allowlist `switch` for sort columns).

## 5. Always Dispose DB Resources

Every `DbConnection`, `DbCommand`, `DbDataReader`, and `IDbContextTransaction` must be scoped with `await using` (or `using` for sync):

```csharp
await using var tx = await _db.Database.BeginTransactionAsync(ct);
try
{
    // Execute atomic writes
    await _db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
}
catch
{
    await tx.RollbackAsync(ct);
    throw;
}
```

## 6. Pragmatic LINQ vs. Raw SQL

Be pragmatic, never dogmatic:
- **Use LINQ** when the generated SQL is clean, indexed, and readable (CRUD, simple filters, projections).
- **Use parameterized Raw SQL** when LINQ produces suboptimal SQL (complex aggregations, MySQL-specific hints/window functions, hot-path upsert/conditional update). Always verify generated SQL via `ToQueryString()` or `EXPLAIN`.

## 7. Generic Base Entity & CRUD Repository

Share standard audit/concurrency metadata and base CRUD operations:

```csharp
public abstract class BaseEntity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>(); // Optimistic concurrency token
}

public interface IGenericRepository<TEntity, TId>
    where TEntity : BaseEntity<TId>
    where TId : notnull
{
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task AddAsync(TEntity entity, CancellationToken ct = default);
    void Update(TEntity entity);
    void Remove(TEntity entity);
}
```

Concrete entities inherit `BaseEntity<TId>`; concrete repositories inherit `GenericRepository<TEntity, TId>` and add domain-specific optimized queries.

## 8. Atomicity, Race Conditions & Deadlock Prevention

- **Optimistic concurrency**: Configure `RowVersion` as `[Timestamp]` / `IsConcurrencyToken()` and handle `DbUpdateConcurrencyException`.
- **Atomic conditional updates**: Prevent read-modify-write races with single-statement SQL:
  ```csharp
  var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
      $"UPDATE inventory SET stock = stock - {qty} WHERE product_id = {productId} AND stock >= {qty}",
      ct);
  if (affected == 0) throw new InsufficientStockException(productId);
  ```
- **Consistent lock ordering**: When locking/updating multiple rows or tables in one transaction, always acquire locks in a deterministic order (e.g., ascending `Id`).
- **Bounded timeouts**: Always pass `CancellationToken` and configure a bounded `CommandTimeout` (e.g., 5–15s for OLTP) so blocked queries fail fast instead of hanging threads infinitely.
- **Short transactions**: Never make external HTTP, gRPC, or Kafka network calls inside an open DB transaction.
