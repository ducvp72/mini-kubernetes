---
name: database-performance
description: Use when designing MySQL schemas, pagination endpoints, indexes, read-optimized views, query performance tuning, SQL functions, or stored procedures for mini-kubernetes. Covers bidirectional cursor pagination, composite/covering indexes, ALGORITHM=MERGE views, EXPLAIN ANALYZE tuning, and EF Core migration versioning.
---

# Database Performance (MySQL + High-Throughput OLTP)

Design queries, pagination, indexes, views, and execution plans to sustain low-latency reads and high write throughput on large tables.

## 1. Bidirectional Cursor (Keyset) Pagination

**Never use `OFFSET N` on large or high-churn tables** — MySQL must scan and discard `N` rows, degrading to O(N) latency and causing duplicate/skipped rows when new records arrive.

Use **opaque bidirectional cursors** encoded as Base64url JSON/delimited tuples over a deterministic tie-breaker key, typically `(created_at, id)`:

- **Forward (`next` cursor, `ORDER BY created_at DESC, id DESC`)**:
  ```sql
  SELECT id, customer_id, status, amount, created_at
  FROM orders
  WHERE (created_at < @cursorCreatedAt)
     OR (created_at = @cursorCreatedAt AND id < @cursorId)
  ORDER BY created_at DESC, id DESC
  LIMIT @pageSizePlusOne;
  ```
- **Backward (`prev` cursor)**:
  Query with reversed comparison (`>` instead of `<`) and `ORDER BY created_at ASC, id ASC LIMIT @pageSizePlusOne`, then reverse the result slice in memory so the API always returns consistent display order.
- **Fetch `pageSize + 1` rows**: Check if an extra row exists to compute `hasNext` / `hasPrev` without running an expensive `SELECT COUNT(*)`.
- **Response contract**: Return `{ items, nextCursor, prevCursor, hasNext, hasPrev }`.

## 2. Reasonable Indexing Strategy

Design indexes from actual query shapes and verify with `EXPLAIN`:

- **Leftmost-prefix & `WHERE` + `ORDER BY` alignment**:
  1. Equality predicates (`=`) first.
  2. Range predicates (`<`, `>`, `BETWEEN`) or `ORDER BY` columns next.
  - Example for cursor pagination per customer: `INDEX ix_orders_customer_created_id (customer_id, created_at DESC, id DESC)`.
  - Example for global cursor pagination: `INDEX ix_orders_created_id (created_at DESC, id DESC)`.
- **Covering indexes for hot reads**: Include small projected columns when a hot read can be satisfied purely from the index (`Using index` in `EXPLAIN`) without touching the clustered table.
- **Avoid over-indexing**: Every extra secondary index amplifies `INSERT`/`UPDATE` I/O and InnoDB buffer pool pressure. Drop redundant prefix indexes (e.g., `(customer_id)` is redundant if `(customer_id, created_at, id)` already exists).
- **Primary key discipline**: Prefer monotonically increasing keys (`BIGINT AUTO_INCREMENT` or UUIDv7) for InnoDB clustered PKs to avoid B-tree page splits under heavy inserts.

## 3. Read-Optimized Database Views

Use SQL Views (`CREATE OR REPLACE ALGORITHM=MERGE VIEW vw_...`) when read endpoints benefit from a clean, narrow, reusable read projection (e.g., list/summary queries that strip wide payload/URL columns or expose standardized computed fields):

- **Prefer `ALGORITHM=MERGE` over `TEMPTABLE`:** `MERGE` pushes `WHERE`, `ORDER BY`, and `LIMIT` predicates down into the underlying table's indexes; `TEMPTABLE` materializes the full result set into a temporary table and destroys cursor-pagination performance.
- **Avoid index-breaking constructs inside hot views:** Never wrap indexed filter/sort columns in non-SARGable functions, avoid `DISTINCT`/`GROUP BY` in OLTP hot-path views unless backed by dedicated summary tables.
- **EF Core mapping:** Map read views to keyless read-only entities via `modelBuilder.Entity<OrderSummaryView>().ToView("vw_order_summary").HasNoKey();` and always query with `.AsNoTracking()`.
- **Version in EF Core Code-First migrations:** Create via `migrationBuilder.Sql("CREATE OR REPLACE ALGORITHM=MERGE VIEW ...")` in `Up()` and `DROP VIEW IF EXISTS ...` in `Down()`.

## 4. Query Performance Tuning (`EXPLAIN` & SARGable SQL)

Treat every hot query as if the table has **10M–100M+ rows** and verify execution plans with `EXPLAIN` / `EXPLAIN ANALYZE`:

- **Target access types:** Aim for `const`, `eq_ref`, `ref`, or `range`. Flag and fix `ALL` (full table scan) and wide `index` scans on large tables.
- **Inspect `Extra` warnings:**
  - Prefer `Using index` (covering index) and `Backward index scan`.
  - Eliminate `Using filesort` and `Using temporary` on hot OLTP queries by aligning composite index column order with `WHERE` + `ORDER BY`.
- **Keep predicates SARGable (index-friendly):**
  - Never apply functions or arithmetic to indexed columns (`WHERE DATE(created_at) = '2026-11-23'` breaks the index; use `WHERE created_at >= '2026-11-23 00:00:00' AND created_at < '2026-11-24 00:00:00'`).
  - Match parameter data types exactly to column types (avoid implicit `VARCHAR` ↔ `INT` or charset/collation mismatches that disable index seeks).
- **Shape queries for minimal I/O:**
  - Project only required columns (never `SELECT *` on wide tables).
  - Prefer `UNION ALL` over `UNION` when duplicates are impossible (avoids sort/dedup temp table).
  - Prefer `EXISTS` (semi-join short-circuit) over large `IN (SELECT ...)` subqueries when checking existence.
  - Batch writes (`INSERT ... VALUES (...), (...)`) and use `FOR UPDATE SKIP LOCKED` for concurrent queue/outbox workers to eliminate lock-wait stalls.

## 5. SQL Functions & Stored Procedures

Use database-side routines selectively and **version them inside EF Core Code-First migrations** (`migrationBuilder.Sql(...)` in `Up` and `DROP ... IF EXISTS` in `Down`):

| Construct | When to Use | Guidelines |
| --- | --- | --- |
| **SQL Function** | Reusable pure calculation/formatting shared across multiple queries (e.g., deterministic status bucket or normalized key) | Mark `DETERMINISTIC` / `NO SQL` where applicable; avoid calling functions on indexed columns in `WHERE` clauses (breaks index usage) |
| **Stored Procedure** | Multi-step atomic DB operation where reducing app-to-DB network round-trips or encapsulating row-level locking improves throughput | Keep concise and set-based; always pass typed parameters; avoid embedding complex business workflows that belong in C# |

```csharp
// Inside EF Core Migration Up()
migrationBuilder.Sql("""
    CREATE PROCEDURE sp_claim_outbox_batch(IN p_batch_size INT)
    BEGIN
        SELECT id, event_type, payload
        FROM outbox_messages
        WHERE processed_at IS NULL
        ORDER BY created_at ASC, id ASC
        LIMIT p_batch_size
        FOR UPDATE SKIP LOCKED;
    END
    """);
```

## Checklist
- [ ] List endpoints use bidirectional `(created_at, id)` cursor pagination (`nextCursor` / `prevCursor`), never `OFFSET`.
- [ ] Every hot query is backed by an aligned composite index verified with `EXPLAIN` / `EXPLAIN ANALYZE` (no `ALL`, no `Using filesort` / `Using temporary`).
- [ ] Read-heavy projections use narrow `ALGORITHM=MERGE` SQL Views (or `.Select(...)`) mapped via `.ToView(...).HasNoKey()` + `.AsNoTracking()`.
- [ ] All predicates are SARGable (no functions/implicit casts on indexed columns, no `SELECT *`).
- [ ] No redundant or unused secondary indexes exist on write-heavy tables.
- [ ] Any View, SQL Function, or Stored Procedure is versioned in an EF Core migration with clean `Down()` teardown.
