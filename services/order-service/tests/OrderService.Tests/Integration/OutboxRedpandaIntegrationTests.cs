using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Events;
using OrderService.Application.Options;
using OrderService.Application.Pagination;
using OrderService.Application.Services;
using OrderService.Domain.Enums;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;
using Testcontainers.MySql;
using Testcontainers.Redpanda;

namespace OrderService.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class OutboxRedpandaIntegrationTests
{
    private const string TopicName = "order.created";

    private MySqlContainer _mysqlContainer = null!;
    private RedpandaContainer _redpandaContainer = null!;
    private string _mysqlConnectionString = string.Empty;
    private string _bootstrapServers = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mysqlContainer = new MySqlBuilder()
            .WithImage("mysql:8.4")
            .WithDatabase("mini_k8s_outbox_test")
            .WithUsername("minik8s")
            .WithPassword("minik8spass123")
            .WithCommand("--log-bin-trust-function-creators=1", "--performance-schema=OFF")
            .Build();

        _redpandaContainer = new RedpandaBuilder()
            .WithImage("redpandadata/redpanda:v24.2.12")
            .Build();

        // Start sequentially to avoid Docker Desktop memory spikes when booting MySQL 8.4 and Redpanda together.
        await _mysqlContainer.StartAsync();
        await _redpandaContainer.StartAsync();

        _mysqlConnectionString = OrderDbContextFactory.NormalizeConnectionString(_mysqlContainer.GetConnectionString());
        _bootstrapServers = _redpandaContainer.GetBootstrapAddress()
            .Replace("PLAINTEXT://", string.Empty, StringComparison.OrdinalIgnoreCase);

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _bootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification { Name = TopicName, NumPartitions = 6, ReplicationFactor = 1 }
            ]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(static r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Topic already exists.
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await Task.WhenAll(_mysqlContainer.DisposeAsync().AsTask(), _redpandaContainer.DisposeAsync().AsTask());
    }

    [Test]
    public async Task OutboxRelayAndConsumer_PublishesToRedpandaMarksPublishedConfirmsOrderAndDeduplicatesReplay()
    {
        // 1. Create order atomically with outbox row in real MySQL.
        CreateOrderResult created;
        await using (var db = CreateDbContext())
        {
            var appService = new OrderApplicationService(
                new OrderRepository(db),
                new OutboxRepository(db),
                new EfUnitOfWork(db),
                new CursorTokenCodec(),
                TimeProvider.System,
                NullLogger<OrderApplicationService>.Instance);

            created = await appService.CreateOrderAsync(new CreateOrderCommand(
                "redpanda-idem-1",
                "cust-rp-1",
                "SKU-RP-01",
                3,
                29.99m,
                "http://mock-webhook:8090/callback",
                5));
        }

        // 2. Relay pending outbox batch to real Redpanda container.
        var kafkaOptions = Microsoft.Extensions.Options.Options.Create(new KafkaOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = TopicName,
            ConsumerGroupId = "order-service-test-workers",
            LingerMs = 5,
            BatchSizeBytes = 65536
        });

        using var publisher = new KafkaOrderEventPublisher(kafkaOptions);

        await using (var relayDb = CreateDbContext())
        {
            var relayProcessor = new OutboxRelayProcessor(
                new EfUnitOfWork(relayDb),
                new OutboxRepository(relayDb),
                publisher,
                Microsoft.Extensions.Options.Options.Create(new OutboxOptions { BatchSize = 50, MaxRetries = 3 }),
                TimeProvider.System,
                NullLogger<OutboxRelayProcessor>.Instance);

            var relayedCount = await relayProcessor.ProcessNextBatchAsync();
            Assert.That(relayedCount, Is.EqualTo(1));
        }

        // 3. Verify outbox row in MySQL transitioned to PUBLISHED with ProcessedAt populated.
        await using (var verifyOutboxDb = CreateDbContext())
        {
            var outboxRow = await verifyOutboxDb.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Multiple(() =>
            {
                Assert.That(outboxRow.Status, Is.EqualTo(OutboxMessageStatus.Published));
                Assert.That(outboxRow.PartitionKey, Is.EqualTo(created.OrderCode));
                Assert.That(outboxRow.ProcessedAt, Is.Not.Null);
            });
        }

        // 4. Consume real message from Redpanda and verify Key == order_code and valid payload.
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = $"verify-group-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        ConsumeResult<string, string>? consumed = null;
        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        {
            consumer.Subscribe(TopicName);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && consumed is null)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is not null && result.Message.Key == created.OrderCode)
                {
                    consumed = result;
                }
            }

            consumer.Close();
        }

        Assert.That(consumed, Is.Not.Null, "Expected order.created message to arrive in Redpanda.");
        Assert.That(consumed!.Message.Key, Is.EqualTo(created.OrderCode));

        var parsedEvent = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(
            consumed.Message.Value,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(parsedEvent, Is.Not.Null);
        Assert.That(parsedEvent!.OrderCode, Is.EqualTo(created.OrderCode));

        // 5. Feed consumed message through OrderEventProcessor -> confirms order in MySQL + invokes webhook once; replay skips webhook.
        var webhookMock = new Mock<IWebhookDispatcher>();
        webhookMock
            .Setup(x => x.SendOrderCallbackAsync(It.IsAny<WebhookCallbackRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookDispatchResult(true, 200, 1, string.Empty));

        await using (var consumerDb = CreateDbContext())
        {
            var eventProcessor = new OrderEventProcessor(
                new OrderRepository(consumerDb),
                webhookMock.Object,
                TimeProvider.System,
                NullLogger<OrderEventProcessor>.Instance);

            var firstRun = await eventProcessor.ProcessMessageAsync(consumed.Message.Key, consumed.Message.Value);
            var replayRun = await eventProcessor.ProcessMessageAsync(consumed.Message.Key, consumed.Message.Value);

            Assert.Multiple(() =>
            {
                Assert.That(firstRun, Is.True);
                Assert.That(replayRun, Is.False);
            });
        }

        webhookMock.Verify(
            x => x.SendOrderCallbackAsync(
                It.Is<WebhookCallbackRequest>(r => r.OrderCode == created.OrderCode && r.Status == "CONFIRMED"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        await using var finalDb = CreateDbContext();
        var confirmed = await finalDb.Orders.AsNoTracking().SingleAsync(o => o.OrderCode == created.OrderCode);
        Assert.Multiple(() =>
        {
            Assert.That(confirmed.Status, Is.EqualTo(OrderStatus.Confirmed));
            Assert.That(confirmed.RowVersion, Is.EqualTo(2UL));
        });
    }

    private OrderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseMySql(_mysqlConnectionString, OrderDbContextFactory.MySqlVersion, static mysql => mysql.CommandTimeout(10))
            .Options;
        return new OrderDbContext(options);
    }
}
