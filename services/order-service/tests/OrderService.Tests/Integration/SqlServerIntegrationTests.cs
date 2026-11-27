using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OrderService.Application.Dtos;
using OrderService.Application.Options;
using OrderService.Application.Pagination;
using OrderService.Application.Services;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;
using Testcontainers.MsSql;

namespace OrderService.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class SqlServerIntegrationTests
{
    private MsSqlContainer _sqlContainer = null!;
    private string _connectionString = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _sqlContainer = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        await _sqlContainer.StartAsync();
        _connectionString = _sqlContainer.GetConnectionString();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _sqlContainer.DisposeAsync();
    }

    [SetUp]
    public async Task CleanTablesAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.outbox_messages;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.orders;");
    }

    [Test]
    public async Task Migration_CreatesSchemaObjectsWithZeroPendingModelDiffs_AndUsesCompositeIndexes()
    {
        // Verify DatabaseMigrationRunner works idempotently with ApplyMigrationsOnStartup = true.
        var services = new ServiceCollection();
        services.AddDbContextPool<OrderDbContext>(o => o.UseSqlServer(_connectionString));
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

        // Verify dbo.fn_order_status_label returns expected status labels.
        var pendingLabel = await ExecuteScalarStringAsync(db, "SELECT dbo.fn_order_status_label(1);");
        var confirmedLabel = await ExecuteScalarStringAsync(db, "SELECT dbo.fn_order_status_label(2);");
        var failedLabel = await ExecuteScalarStringAsync(db, "SELECT dbo.fn_order_status_label(3);");
        var unspecifiedLabel = await ExecuteScalarStringAsync(db, "SELECT dbo.fn_order_status_label(0);");

        Assert.Multiple(() =>
        {
            Assert.That(pendingLabel, Is.EqualTo("PENDING"));
            Assert.That(confirmedLabel, Is.EqualTo("CONFIRMED"));
            Assert.That(failedLabel, Is.EqualTo("FAILED"));
            Assert.That(unspecifiedLabel, Is.EqualTo("UNSPECIFIED"));
        });

        // Seed 500 rows across 4 customers and update statistics so SQL Server chooses composite indexes.
        var baseTime = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 500; i++)
        {
            var customer = $"cust-{i % 4}";
            var order = Order.Create($"explain-idem-{i}", customer, "SKU-EXP", 2, 19.99m, null, 0, baseTime.AddSeconds(i));
            await db.Orders.AddAsync(order);
        }

        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE STATISTICS dbo.orders;");

        var pivot = await db.Orders.AsNoTracking().OrderByDescending(o => o.Id).Skip(400).FirstAsync();

        var globalPlanXml = await GetShowPlanXmlAsync(
            """
            SELECT TOP (21) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
            FROM dbo.vw_order_summary
            WHERE created_at < @c OR (created_at = @c AND id < @id)
            ORDER BY created_at DESC, id DESC
            """,
            pivot.CreatedAt,
            pivot.Id,
            customerId: null);

        Assert.That(globalPlanXml, Does.Contain("ix_orders_created_id"));

        var customerPlanXml = await GetShowPlanXmlAsync(
            """
            SELECT TOP (21) id, order_code, customer_id, sku, quantity, unit_price, total_amount, status, created_at
            FROM dbo.vw_order_summary
            WHERE customer_id = @cust
              AND (created_at < @c OR (created_at = @c AND id < @id))
            ORDER BY created_at DESC, id DESC
            """,
            pivot.CreatedAt,
            pivot.Id,
            customerId: pivot.CustomerId);

        Assert.That(customerPlanXml, Does.Contain("ix_orders_customer_created_id"));
    }

    [Test]
    public async Task CreateOrder_IdempotencyAndBidirectionalCursorPagination_WorkEndToEnd()
    {
        var baseTime = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

        await using (var db = CreateDbContext())
        {
            var appService = CreateAppService(db);
            var first = await appService.CreateOrderAsync(new CreateOrderCommand("idem-1", "cust-a", "SKU-1", 2, 10.50m, null, 0));
            var duplicate = await appService.CreateOrderAsync(new CreateOrderCommand("idem-1", "cust-a", "SKU-1", 2, 10.50m, null, 0));

            Assert.Multiple(() =>
            {
                Assert.That(first.Deduplicated, Is.False);
                Assert.That(duplicate.Deduplicated, Is.True);
                Assert.That(duplicate.OrderCode, Is.EqualTo(first.OrderCode));
            });

            var fetched = await appService.GetOrderAsync(first.OrderCode);
            Assert.That(fetched.TotalAmount, Is.EqualTo(21.00m));
        }

        await using (var seedDb = CreateDbContext())
        {
            for (var i = 0; i < 6; i++)
            {
                var order = Order.Create($"page-idem-{i}", "cust-page", $"SKU-{i}", 1, 10m + i, null, 0, baseTime.AddSeconds(i));
                await seedDb.Orders.AddAsync(order);
            }

            await seedDb.SaveChangesAsync();
        }

        await using var readDb = CreateDbContext();
        var reader = CreateAppService(readDb);

        var page1 = await reader.ListOrdersAsync(new ListOrdersQuery("cust-page", 2, null, null));
        Assert.Multiple(() =>
        {
            Assert.That(page1.Items, Has.Count.EqualTo(2));
            Assert.That(page1.HasNext, Is.True);
            Assert.That(page1.HasPrev, Is.False);
            Assert.That(page1.NextCursor, Is.Not.Null.And.Not.Empty);
        });

        var page2 = await reader.ListOrdersAsync(new ListOrdersQuery("cust-page", 2, page1.NextCursor, null));
        Assert.Multiple(() =>
        {
            Assert.That(page2.Items, Has.Count.EqualTo(2));
            Assert.That(page2.HasNext, Is.True);
            Assert.That(page2.HasPrev, Is.True);
            Assert.That(page2.Items[0].OrderCode, Is.Not.EqualTo(page1.Items[0].OrderCode));
        });

        var backToPage1 = await reader.ListOrdersAsync(new ListOrdersQuery("cust-page", 2, null, page2.PrevCursor));
        Assert.That(
            backToPage1.Items.Select(static x => x.OrderCode).ToArray(),
            Is.EqualTo(page1.Items.Select(static x => x.OrderCode).ToArray()));
    }

    [Test]
    public async Task TryConfirmOrder_AndOutboxClaimBatchSkipLocked_HandleConcurrencyCleanly()
    {
        ulong orderId;
        await using (var db = CreateDbContext())
        {
            var appService = CreateAppService(db);
            var created = await appService.CreateOrderAsync(new CreateOrderCommand("conc-1", "cust-c", "SKU-C", 1, 25m, null, 0));
            await appService.CreateOrderAsync(new CreateOrderCommand("conc-2", "cust-c", "SKU-C", 1, 25m, null, 0));

            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.OrderCode == created.OrderCode);
            orderId = order.Id;

            var repo = new OrderRepository(db);
            var confirmedFirst = await repo.TryConfirmOrderAsync(orderId, 1UL);
            var confirmedSecond = await repo.TryConfirmOrderAsync(orderId, 1UL);

            Assert.Multiple(() =>
            {
                Assert.That(confirmedFirst, Is.True);
                Assert.That(confirmedSecond, Is.False);
            });
        }

        // Verify two concurrent transactions claiming from dbo.sp_claim_outbox_batch (UPDLOCK, READPAST, ROWLOCK)
        // receive disjoint outbox messages without blocking each other.
        await using var db1 = CreateDbContext();
        await using var db2 = CreateDbContext();
        var uow1 = new EfUnitOfWork(db1);
        var uow2 = new EfUnitOfWork(db2);
        var outbox1 = new OutboxRepository(db1);
        var outbox2 = new OutboxRepository(db2);

        await using var tx1 = await uow1.BeginTransactionAsync();
        var batch1 = await outbox1.ClaimPendingBatchAsync(1);

        await using var tx2 = await uow2.BeginTransactionAsync();
        var batch2 = await outbox2.ClaimPendingBatchAsync(1);

        Assert.Multiple(() =>
        {
            Assert.That(batch1, Has.Count.EqualTo(1));
            Assert.That(batch2, Has.Count.EqualTo(1));
            Assert.That(batch1[0].Id, Is.Not.EqualTo(batch2[0].Id));
        });

        await tx2.CommitAsync();
        await tx1.CommitAsync();
    }

    private OrderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseSqlServer(_connectionString, static sql => sql.CommandTimeout(15))
            .Options;
        return new OrderDbContext(options);
    }

    private static OrderApplicationService CreateAppService(OrderDbContext db) =>
        new(
            new OrderRepository(db),
            new OutboxRepository(db),
            new EfUnitOfWork(db),
            new CursorTokenCodec(),
            TimeProvider.System,
            NullLogger<OrderApplicationService>.Instance);

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

    private async Task<string> GetShowPlanXmlAsync(
        string querySql,
        DateTime cursorCreatedAt,
        ulong cursorId,
        string? customerId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using (var onCmd = connection.CreateCommand())
        {
            onCmd.CommandText = "SET STATISTICS XML ON;";
            await onCmd.ExecuteNonQueryAsync();
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = querySql;

            var cParam = cmd.CreateParameter();
            cParam.ParameterName = "@c";
            cParam.DbType = DbType.DateTime2;
            cParam.Value = cursorCreatedAt;
            cmd.Parameters.Add(cParam);

            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "@id";
            idParam.DbType = DbType.Int64;
            idParam.Value = checked((long)cursorId);
            cmd.Parameters.Add(idParam);

            if (customerId is not null)
            {
                var custParam = cmd.CreateParameter();
                custParam.ParameterName = "@cust";
                custParam.DbType = DbType.AnsiString;
                custParam.Value = customerId;
                cmd.Parameters.Add(custParam);
            }

            var plans = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0))
                    {
                        plans.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
                    }
                }
            }
            while (await reader.NextResultAsync());

            return string.Join(Environment.NewLine, plans);
        }
        finally
        {
            await using var offCmd = connection.CreateCommand();
            offCmd.CommandText = "SET STATISTICS XML OFF;";
            await offCmd.ExecuteNonQueryAsync();
        }
    }
}
