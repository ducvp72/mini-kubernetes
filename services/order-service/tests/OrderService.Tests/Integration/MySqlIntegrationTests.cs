using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using OrderService.Application.Dtos;
using OrderService.Application.Options;
using OrderService.Application.Pagination;
using OrderService.Application.Services;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Pagination;
using OrderService.Domain.Repositories;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;
using Testcontainers.MySql;

namespace OrderService.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class MySqlIntegrationTests
{
    private MySqlContainer _mysqlContainer = null!;
    private string _connectionString = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mysqlContainer = new MySqlBuilder()
            .WithImage("mysql:8.4")
            .WithDatabase("mini_k8s_test")
            .WithUsername("minik8s")
            .WithPassword("minik8spass123")
            .WithCommand("--log-bin-trust-function-creators=1")
            .Build();

        await _mysqlContainer.StartAsync();
        _connectionString = OrderDbContextFactory.NormalizeConnectionString(_mysqlContainer.GetConnectionString());

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _mysqlContainer.DisposeAsync();
    }

    [SetUp]
    public async Task CleanTablesAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM outbox_messages;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM orders;");
    }

    [Test]
    public async Task Migration_CreatesSchemaObjectsWithZeroPendingModelDiffs_AndExplainUsesCompositeIndexes()
    {
        // Verify DatabaseMigrationRunner works idempotently with ApplyMigrationsOnStartup = true.
        var services = new ServiceCollection();
        services.AddDbContextPool<OrderDbContext>(o => o.UseMySql(_connectionString, OrderDbContextFactory.MySqlVersion));
        await using var sp = services.BuildServiceProvider();

        var runner = new DatabaseMigrationRunner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new DatabaseOptions { ApplyMigrationsOnStartup = true }),
            NullLogger<DatabaseMigrationRunner>.Instance);
        await runner.ApplyMigrationsIfEnabledAsync();

        await using var db = CreateDbContext();

        // Verify EF Core runtime model matches the InitialCreate snapshot with zero pending changes.
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var modelDiffer = db.GetService<IMigrationsModelDiffer>();
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var modelRuntimeInitializer = db.GetService<IModelRuntimeInitializer>();
        var snapshotModel = modelRuntimeInitializer.Initialize(migrationsAssembly.ModelSnapshot!.Model);

        var hasPendingDiffs = modelDiffer.HasDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel());
        Assert.That(hasPendingDiffs, Is.False, "EF Core model has pending differences not captured in InitialCreate migration.");

        // Verify fn_order_status_label returns expected status labels.
        var pendingLabel = await ExecuteScalarStringAsync(db, "SELECT fn_order_status_label(1);");
        var confirmedLabel = await ExecuteScalarStringAsync(db, "SELECT fn_order_status_label(2);");
        var failedLabel = await ExecuteScalarStringAsync(db, "SELECT fn_order_status_label(3);");
        var unspecifiedLabel = await ExecuteScalarStringAsync(db, "SELECT fn_order_status_label(0);");

        Assert.Multiple(() =>
        {
            Assert.That(pendingLabel, Is.EqualTo("PENDING"));
            Assert.That(confirmedLabel, Is.EqualTo("CONFIRMED"));
            Assert.That(failedLabel, Is.EqualTo("FAILED"));
            Assert.That(unspecifiedLabel, Is.EqualTo("UNSPECIFIED"));
        });

        // Seed 500 rows across 4 customers and run ANALYZE TABLE so MySQL 8.4 spans multiple InnoDB pages
        // and uses realistic index cost statistics rather than falling back to a single-page table scan.
        var baseTime = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 500; i++)
        {
            var customer = $"cust-{i % 4}";
            var order = Order.Create($"explain-idem-{i}", customer, "SKU-EXP", 2, 19.99m, null, 0, baseTime.AddSeconds(i));
            await db.Orders.AddAsync(order);
        }

        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ANALYZE TABLE orders;");

        var pivot = await db.Orders.AsNoTracking().OrderByDescending(o => o.Id).Skip(400).FirstAsync();

        // EXPLAIN global cursor query on vw_order_summary.
        var globalPlan = await ExplainAsync(
            db,
            """
            EXPLAIN
            SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
            FROM vw_order_summary
            WHERE created_at < @c OR (created_at = @c AND id < @id)
            ORDER BY created_at DESC, id DESC
            LIMIT 21
            """,
            pivot.CreatedAt,
            pivot.Id,
            customerId: null);

        Assert.Multiple(() =>
        {
            Assert.That(globalPlan.Key, Is.EqualTo("ix_orders_created_id"));
            Assert.That(globalPlan.Type, Is.Not.EqualTo("ALL"));
            Assert.That(globalPlan.Extra, Does.Not.Contain("Using filesort"));
            Assert.That(globalPlan.Extra, Does.Not.Contain("Using temporary"));
        });

        // EXPLAIN customer-filtered cursor query on vw_order_summary.
        var customerPlan = await ExplainAsync(
            db,
            """
            EXPLAIN
            SELECT id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
            FROM vw_order_summary
            WHERE customer_id = @cust
              AND (created_at < @c OR (created_at = @c AND id < @id))
            ORDER BY created_at DESC, id DESC
            LIMIT 21
            """,
            pivot.CreatedAt,
            pivot.Id,
            customerId: pivot.CustomerId);

        Assert.Multiple(() =>
        {
            Assert.That(customerPlan.Key, Is.EqualTo("ix_orders_customer_created_id"));
            Assert.That(customerPlan.Type, Is.Not.EqualTo("ALL"));
            Assert.That(customerPlan.Extra, Does.Not.Contain("Using filesort"));
            Assert.That(customerPlan.Extra, Does.Not.Contain("Using temporary"));
        });

        // Verify Down() and Up() migration teardown/rebuild round-trip works cleanly.
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("0");
        await migrator.MigrateAsync();
    }

    [Test]
    public async Task CreateOrder_WhenOutboxWriteFails_RollsBackOrderInsertAtomically()
    {
        await using var db = CreateDbContext();
        var orderRepo = new OrderRepository(db);
        var failingOutboxRepo = new FaultInjectingOutboxRepository(new OutboxRepository(db));
        var uow = new EfUnitOfWork(db);
        var appService = new OrderApplicationService(
            orderRepo,
            failingOutboxRepo,
            uow,
            new CursorTokenCodec(),
            TimeProvider.System,
            NullLogger<OrderApplicationService>.Instance);

        var command = new CreateOrderCommand(
            "rollback-idem-1",
            "cust-rollback",
            "SKU-FAIL",
            2,
            39.99m,
            "http://mock-webhook:8090/callback",
            0);

        Assert.ThrowsAsync<InvalidOperationException>(() => appService.CreateOrderAsync(command));

        await using var verifyDb = CreateDbContext();
        var orderCount = await verifyDb.Orders.CountAsync();
        var outboxCount = await verifyDb.OutboxMessages.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(orderCount, Is.EqualTo(0), "Order row must be rolled back when outbox insert fails.");
            Assert.That(outboxCount, Is.EqualTo(0), "Outbox table must remain empty on rollback.");
        });
    }

    [Test]
    public async Task Concurrency_ParallelDuplicateCreates_SkipLockedClaims_AndAtomicConfirm()
    {
        // 1. Fire 10 parallel CreateOrder calls with the exact same idempotency_key.
        const string sharedIdempotencyKey = "concurrent-idem-10x";
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var createTasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await startGate.Task;
            await using var scopedDb = CreateDbContext();
            var svc = CreateApplicationService(scopedDb);
            return await svc.CreateOrderAsync(new CreateOrderCommand(
                sharedIdempotencyKey,
                "cust-race",
                "SKU-RACE",
                1,
                99.95m,
                "http://mock-webhook:8090/callback",
                0));
        }).ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(createTasks);

        var distinctCodes = results.Select(static r => r.OrderCode).Distinct().ToArray();
        var nonDeduplicatedCount = results.Count(static r => !r.Deduplicated);

        await using (var verifyDb = CreateDbContext())
        {
            var persistedOrders = await verifyDb.Orders.CountAsync();
            var persistedOutbox = await verifyDb.OutboxMessages.CountAsync();

            Assert.Multiple(() =>
            {
                Assert.That(distinctCodes, Has.Length.EqualTo(1));
                Assert.That(nonDeduplicatedCount, Is.EqualTo(1));
                Assert.That(persistedOrders, Is.EqualTo(1));
                Assert.That(persistedOutbox, Is.EqualTo(1));
            });
        }

        // 2. Seed 5 more orders (6 total pending outbox rows) and verify 2 concurrent sp_claim_outbox_batch calls claim disjoint sets.
        await using (var seedDb = CreateDbContext())
        {
            var svc = CreateApplicationService(seedDb);
            for (var i = 1; i <= 5; i++)
            {
                await svc.CreateOrderAsync(new CreateOrderCommand(
                    $"skiplock-idem-{i}",
                    "cust-lock",
                    "SKU-LOCK",
                    1,
                    10.00m,
                    null,
                    0));
            }
        }

        var worker1Claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWorker1Commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claimTask1 = Task.Run(async () =>
        {
            await using var db1 = CreateDbContext();
            var uow1 = new EfUnitOfWork(db1);
            var repo1 = new OutboxRepository(db1);
            await using var tx1 = await uow1.BeginTransactionAsync();
            var batch1 = await repo1.ClaimPendingBatchAsync(3);
            worker1Claimed.SetResult();
            await allowWorker1Commit.Task;
            await tx1.CommitAsync();
            return batch1.Select(static m => m.Id).ToArray();
        });

        await worker1Claimed.Task;

        ulong[] batch2Ids;
        await using (var db2 = CreateDbContext())
        {
            var uow2 = new EfUnitOfWork(db2);
            var repo2 = new OutboxRepository(db2);
            await using var tx2 = await uow2.BeginTransactionAsync();
            var batch2 = await repo2.ClaimPendingBatchAsync(3);
            batch2Ids = batch2.Select(static m => m.Id).ToArray();
            await tx2.CommitAsync();
        }

        allowWorker1Commit.SetResult();
        var batch1Ids = await claimTask1;

        Assert.Multiple(() =>
        {
            Assert.That(batch1Ids, Has.Length.EqualTo(3));
            Assert.That(batch2Ids, Has.Length.EqualTo(3));
            Assert.That(batch1Ids.Intersect(batch2Ids), Is.Empty, "FOR UPDATE SKIP LOCKED must claim strictly disjoint batches.");
        });

        // 3. Fire 5 concurrent TryConfirmOrderAsync calls on the same order -> exactly 1 succeeds.
        ulong targetOrderId;
        await using (var lookupDb = CreateDbContext())
        {
            targetOrderId = (await lookupDb.Orders.AsNoTracking().FirstAsync(o => o.IdempotencyKey == sharedIdempotencyKey)).Id;
        }

        var confirmGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmTasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await confirmGate.Task;
            await using var dbConfirm = CreateDbContext();
            var repo = new OrderRepository(dbConfirm);
            return await repo.TryConfirmOrderAsync(targetOrderId, 1UL);
        }).ToArray();

        confirmGate.SetResult();
        var confirmOutcomes = await Task.WhenAll(confirmTasks);

        await using var finalDb = CreateDbContext();
        var confirmedOrder = await finalDb.Orders.AsNoTracking().FirstAsync(o => o.Id == targetOrderId);

        Assert.Multiple(() =>
        {
            Assert.That(confirmOutcomes.Count(static x => x), Is.EqualTo(1));
            Assert.That(confirmOutcomes.Count(static x => !x), Is.EqualTo(4));
            Assert.That(confirmedOrder.Status, Is.EqualTo(OrderStatus.Confirmed));
            Assert.That(confirmedOrder.RowVersion, Is.EqualTo(2UL));
        });
    }

    [Test]
    public async Task BidirectionalCursorPagination_HandlesTiedTimestampsForwardBackwardFilterAndBoundaries()
    {
        // Seed 25 orders with intentional identical-microsecond timestamp clusters to verify deterministic id tie-breaking.
        var baseTime = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        var seeded = new List<Order>(25);

        await using (var seedDb = CreateDbContext())
        {
            for (var i = 0; i < 25; i++)
            {
                // Groups of 5 share the exact same microsecond timestamp.
                var bucketTimestamp = baseTime.AddSeconds(i / 5);
                var customer = i % 2 == 0 ? "cust-alpha" : "cust-beta";
                var order = Order.Create($"page-idem-{i:D2}", customer, $"SKU-{i:D2}", (uint)(i + 1), 10.00m + i, null, 0, bucketTimestamp);
                await seedDb.Orders.AddAsync(order);
                seeded.Add(order);
            }

            await seedDb.SaveChangesAsync();
        }

        var expectedGlobalOrder = seeded
            .OrderByDescending(static o => o.CreatedAt)
            .ThenByDescending(static o => o.Id)
            .Select(static o => o.OrderCode)
            .ToList();

        await using var db = CreateDbContext();
        var svc = CreateApplicationService(db);

        // Traverse forward across all pages (pageSize = 6 -> 6, 6, 6, 6, 1 = 25).
        var forwardPages = new List<ListOrdersResult>();
        string? nextCursor = null;
        while (true)
        {
            var page = await svc.ListOrdersAsync(new ListOrdersQuery(null, 6, nextCursor, null));
            forwardPages.Add(page);

            if (forwardPages.Count == 1)
            {
                Assert.That(page.HasPrev, Is.False);
                Assert.That(page.PrevCursor, Is.Null);
            }
            else
            {
                Assert.That(page.HasPrev, Is.True);
                Assert.That(page.PrevCursor, Is.Not.Null);
            }

            if (!page.HasNext)
            {
                Assert.That(page.NextCursor, Is.Null);
                break;
            }

            Assert.That(page.NextCursor, Is.Not.Null);
            nextCursor = page.NextCursor;
        }

        var traversedForward = forwardPages.SelectMany(static p => p.Items).Select(static i => i.OrderCode).ToList();
        Assert.That(traversedForward, Is.EqualTo(expectedGlobalOrder));

        // Traverse backward from the last page back to the very beginning.
        var backwardCollected = new List<string>();
        var currentPage = forwardPages[^1];
        backwardCollected.AddRange(currentPage.Items.Select(static i => i.OrderCode).Reverse());

        string? prevCursor = currentPage.PrevCursor;
        while (prevCursor is not null)
        {
            var prevPage = await svc.ListOrdersAsync(new ListOrdersQuery(null, 6, null, prevCursor));
            Assert.That(prevPage.HasNext, Is.True);
            backwardCollected.AddRange(prevPage.Items.Select(static i => i.OrderCode).Reverse());

            if (!prevPage.HasPrev)
            {
                Assert.That(prevPage.PrevCursor, Is.Null);
                break;
            }

            prevCursor = prevPage.PrevCursor;
        }

        backwardCollected.Reverse();
        Assert.That(backwardCollected, Is.EqualTo(expectedGlobalOrder));

        // Customer-filtered pagination.
        var expectedAlpha = seeded
            .Where(static o => o.CustomerId == "cust-alpha")
            .OrderByDescending(static o => o.CreatedAt)
            .ThenByDescending(static o => o.Id)
            .Select(static o => o.OrderCode)
            .ToList();

        var alphaCollected = new List<string>();
        string? alphaNext = null;
        do
        {
            var alphaPage = await svc.ListOrdersAsync(new ListOrdersQuery("cust-alpha", 4, alphaNext, null));
            alphaCollected.AddRange(alphaPage.Items.Select(static i => i.OrderCode));
            alphaNext = alphaPage.NextCursor;
        } while (alphaNext is not null);

        Assert.That(alphaCollected, Is.EqualTo(expectedAlpha));

        // Empty customer filter & malformed cursor rejection.
        var emptyPage = await svc.ListOrdersAsync(new ListOrdersQuery("cust-does-not-exist", 10, null, null));
        Assert.Multiple(() =>
        {
            Assert.That(emptyPage.Items, Is.Empty);
            Assert.That(emptyPage.HasNext, Is.False);
            Assert.That(emptyPage.HasPrev, Is.False);
        });

        Assert.ThrowsAsync<DomainValidationException>(() =>
            svc.ListOrdersAsync(new ListOrdersQuery(null, 10, "tampered-cursor", null)));
    }

    private OrderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseMySql(_connectionString, OrderDbContextFactory.MySqlVersion, static mysql => mysql.CommandTimeout(10))
            .Options;
        return new OrderDbContext(options);
    }

    private static OrderApplicationService CreateApplicationService(OrderDbContext db)
    {
        return new OrderApplicationService(
            new OrderRepository(db),
            new OutboxRepository(db),
            new EfUnitOfWork(db),
            new CursorTokenCodec(),
            TimeProvider.System,
            NullLogger<OrderApplicationService>.Instance);
    }

    private static async Task<string> ExecuteScalarStringAsync(OrderDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToString(result) ?? string.Empty;
    }

    private static async Task<ExplainRow> ExplainAsync(
        OrderDbContext db,
        string explainSql,
        DateTime cursorCreatedAt,
        ulong cursorId,
        string? customerId)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = explainSql;

        var pCreated = cmd.CreateParameter();
        pCreated.ParameterName = "@c";
        pCreated.Value = cursorCreatedAt;
        cmd.Parameters.Add(pCreated);

        var pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        pId.Value = cursorId;
        cmd.Parameters.Add(pId);

        if (customerId is not null)
        {
            var pCust = cmd.CreateParameter();
            pCust.ParameterName = "@cust";
            pCust.Value = customerId;
            cmd.Parameters.Add(pCust);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);

        var type = reader["type"]?.ToString() ?? string.Empty;
        var key = reader["key"]?.ToString() ?? string.Empty;
        var extra = reader["Extra"]?.ToString() ?? string.Empty;
        return new ExplainRow(type, key, extra);
    }

    private sealed record ExplainRow(string Type, string Key, string Extra);

    // Fault-injection decorator to force an outbox persistence error inside an active CreateOrder transaction.
    private sealed class FaultInjectingOutboxRepository(IOutboxRepository inner) : IOutboxRepository
    {
        public Task<OutboxMessage?> GetByIdAsync(ulong id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task AddAsync(OutboxMessage entity, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated outbox write failure for rollback verification.");

        public void Update(OutboxMessage entity) => inner.Update(entity);

        public void Remove(OutboxMessage entity) => inner.Remove(entity);

        public Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(int batchSize, CancellationToken ct = default) =>
            inner.ClaimPendingBatchAsync(batchSize, ct);

        public Task MarkPublishedAsync(ulong outboxMessageId, DateTime processedAtUtc, CancellationToken ct = default) =>
            inner.MarkPublishedAsync(outboxMessageId, processedAtUtc, ct);

        public Task MarkRetryOrDeadLetterAsync(
            ulong outboxMessageId,
            byte nextRetryCount,
            OutboxMessageStatus nextStatus,
            DateTime? processedAtUtc,
            CancellationToken ct = default) =>
            inner.MarkRetryOrDeadLetterAsync(outboxMessageId, nextRetryCount, nextStatus, processedAtUtc, ct);
    }
}
