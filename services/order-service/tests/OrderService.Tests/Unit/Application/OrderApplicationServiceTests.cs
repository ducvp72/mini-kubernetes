using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OrderService.Application.Dtos;
using OrderService.Application.Pagination;
using OrderService.Application.Services;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Pagination;
using OrderService.Domain.Repositories;

namespace OrderService.Tests.Unit.Application;

[TestFixture]
[Category("Unit")]
public sealed class OrderApplicationServiceTests
{
    private Mock<IOrderRepository> _orderRepoMock = null!;
    private Mock<IOutboxRepository> _outboxRepoMock = null!;
    private Mock<IUnitOfWork> _unitOfWorkMock = null!;
    private Mock<ITransactionScope> _txMock = null!;
    private CursorTokenCodec _cursorCodec = null!;
    private FixedTimeProvider _timeProvider = null!;
    private OrderApplicationService _sut = null!;

    private static readonly DateTime FixedUtc = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _orderRepoMock = new Mock<IOrderRepository>();
        _outboxRepoMock = new Mock<IOutboxRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _txMock = new Mock<ITransactionScope>();
        _cursorCodec = new CursorTokenCodec();
        _timeProvider = new FixedTimeProvider(FixedUtc);

        _unitOfWorkMock
            .Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_txMock.Object);

        _sut = new OrderApplicationService(
            _orderRepoMock.Object,
            _outboxRepoMock.Object,
            _unitOfWorkMock.Object,
            _cursorCodec,
            _timeProvider,
            NullLogger<OrderApplicationService>.Instance);
    }

    [Test]
    public async Task CreateOrderAsync_HappyPath_PersistsOrderAndOutboxInSingleTransaction()
    {
        Order? addedOrder = null;
        OutboxMessage? addedOutbox = null;

        _orderRepoMock
            .Setup(x => x.GetByIdempotencyKeyAsync("idem-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        _orderRepoMock
            .Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) =>
            {
                SetEntityId(o, 101UL);
                addedOrder = o;
            })
            .Returns(Task.CompletedTask);

        _outboxRepoMock
            .Setup(x => x.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboxMessage, CancellationToken>((m, _) => addedOutbox = m)
            .Returns(Task.CompletedTask);

        _unitOfWorkMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var command = new CreateOrderCommand("idem-1", "cust-1", "SKU-1", 2, 25.50m, "http://mock-webhook:8090/callback", 10);
        var result = await _sut.CreateOrderAsync(command);

        Assert.Multiple(() =>
        {
            Assert.That(result.Deduplicated, Is.False);
            Assert.That(result.Status, Is.EqualTo(OrderStatus.Pending));
            Assert.That(result.OrderCode, Is.EqualTo(addedOrder!.OrderCode));
            Assert.That(addedOutbox, Is.Not.Null);
            Assert.That(addedOutbox!.OrderId, Is.EqualTo(101UL));
            Assert.That(addedOutbox.PartitionKey, Is.EqualTo(addedOrder.OrderCode));
            Assert.That(addedOutbox.EventType, Is.EqualTo("order.created"));
            Assert.That(addedOrder.DomainEvents, Is.Empty);
        });

        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _txMock.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CreateOrderAsync_ExistingIdempotencyKey_ReturnsDeduplicatedWithoutOpeningTransaction()
    {
        var existing = Order.Create("idem-dup", "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc);
        _orderRepoMock
            .Setup(x => x.GetByIdempotencyKeyAsync("idem-dup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await _sut.CreateOrderAsync(new CreateOrderCommand("idem-dup", "cust-1", "SKU-1", 1, 10m, null, 0));

        Assert.Multiple(() =>
        {
            Assert.That(result.Deduplicated, Is.True);
            Assert.That(result.OrderCode, Is.EqualTo(existing.OrderCode));
        });

        _unitOfWorkMock.Verify(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CreateOrderAsync_ConcurrentDuplicateKey_RollsBackAndReturnsExistingOrder()
    {
        var existing = Order.Create("idem-race", "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc);
        var duplicateEx = new InvalidOperationException("Duplicate entry");

        _orderRepoMock
            .SetupSequence(x => x.GetByIdempotencyKeyAsync("idem-race", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null)
            .ReturnsAsync(existing);

        _orderRepoMock
            .Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _unitOfWorkMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(duplicateEx);

        _unitOfWorkMock
            .Setup(x => x.IsDuplicateKeyException(duplicateEx))
            .Returns(true);

        var result = await _sut.CreateOrderAsync(new CreateOrderCommand("idem-race", "cust-1", "SKU-1", 1, 10m, null, 0));

        Assert.Multiple(() =>
        {
            Assert.That(result.Deduplicated, Is.True);
            Assert.That(result.OrderCode, Is.EqualTo(existing.OrderCode));
        });

        _txMock.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(x => x.ClearChangeTracker(), Times.Once);
    }

    [Test]
    public void CreateOrderAsync_ConcurrentDuplicateKeyWithoutExistingRow_ThrowsConcurrencyConflict()
    {
        var duplicateEx = new InvalidOperationException("Duplicate entry");

        _orderRepoMock
            .Setup(x => x.GetByIdempotencyKeyAsync("idem-missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        _unitOfWorkMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(duplicateEx);

        _unitOfWorkMock
            .Setup(x => x.IsDuplicateKeyException(duplicateEx))
            .Returns(true);

        Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            _sut.CreateOrderAsync(new CreateOrderCommand("idem-missing", "cust-1", "SKU-1", 1, 10m, null, 0)));
    }

    [Test]
    public async Task CreateOrderAsync_TransientDeadlock_RetriesAndSucceeds()
    {
        var deadlockEx = new InvalidOperationException("Deadlock");
        var callCount = 0;

        _orderRepoMock
            .Setup(x => x.GetByIdempotencyKeyAsync("idem-dl", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        _orderRepoMock
            .Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) => SetEntityId(o, 200UL))
            .Returns(Task.CompletedTask);

        _unitOfWorkMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw deadlockEx;
                }

                return Task.FromResult(1);
            });

        _unitOfWorkMock
            .Setup(x => x.IsTransientDeadlockException(deadlockEx))
            .Returns(true);

        var result = await _sut.CreateOrderAsync(new CreateOrderCommand("idem-dl", "cust-1", "SKU-1", 1, 10m, null, 0));

        Assert.That(result.Deduplicated, Is.False);
        _txMock.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void CreateOrderAsync_OutboxWriteThrows_RollsBackTransactionAndRethrows()
    {
        _orderRepoMock
            .Setup(x => x.GetByIdempotencyKeyAsync("idem-fail", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        _orderRepoMock
            .Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) => SetEntityId(o, 10UL))
            .Returns(Task.CompletedTask);

        _outboxRepoMock
            .Setup(x => x.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated outbox error"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateOrderAsync(new CreateOrderCommand("idem-fail", "cust-1", "SKU-1", 1, 10m, null, 0)));

        _txMock.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWorkMock.Verify(x => x.ClearChangeTracker(), Times.Once);
    }

    [Test]
    public void CreateOrderAsync_NullCommand_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _sut.CreateOrderAsync(null!));
    }

    [Test]
    public async Task GetOrderAsync_Found_ReturnsMappedOrderDetails()
    {
        var order = Order.Create("idem-get", "cust-1", "SKU-1", 4, 12.50m, "https://cb.test/hook", 0, FixedUtc);
        _orderRepoMock
            .Setup(x => x.GetByOrderCodeAsync(order.OrderCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        var dto = await _sut.GetOrderAsync(order.OrderCode);

        Assert.Multiple(() =>
        {
            Assert.That(dto.OrderCode, Is.EqualTo(order.OrderCode));
            Assert.That(dto.TotalAmount, Is.EqualTo(50.00m));
            Assert.That(dto.CallbackUrl, Is.EqualTo("https://cb.test/hook"));
        });
    }

    [Test]
    public void GetOrderAsync_InvalidOrMissingOrderCode_ThrowsExpectedException()
    {
        Assert.ThrowsAsync<DomainValidationException>(() => _sut.GetOrderAsync(""));
        Assert.ThrowsAsync<DomainValidationException>(() => _sut.GetOrderAsync("short-code"));

        var missingCode = Guid.NewGuid().ToString("D");
        _orderRepoMock
            .Setup(x => x.GetByOrderCodeAsync(missingCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        Assert.ThrowsAsync<OrderNotFoundException>(() => _sut.GetOrderAsync(missingCode));
    }

    [Test]
    public async Task ListOrdersAsync_ForwardAndBackwardPagination_ComputesCursorsAndReversesBackwardSlice()
    {
        var r3 = CreateSummary(3, FixedUtc.AddSeconds(3));
        var r2 = CreateSummary(2, FixedUtc.AddSeconds(2));
        var r1 = CreateSummary(1, FixedUtc.AddSeconds(1));

        // Page 1 (forward, pageSize=2, repository returns 3 rows -> hasNext=true, hasPrev=false)
        _orderRepoMock
            .Setup(x => x.ListSummariesByCursorAsync(null, 3, null, CursorDirection.Forward, It.IsAny<CancellationToken>()))
            .ReturnsAsync([r3, r2, r1]);

        var page1 = await _sut.ListOrdersAsync(new ListOrdersQuery(null, 2, null, null));

        Assert.Multiple(() =>
        {
            Assert.That(page1.Items.Select(x => x.OrderCode), Is.EqualTo(new[] { r3.OrderCode, r2.OrderCode }));
            Assert.That(page1.HasNext, Is.True);
            Assert.That(page1.HasPrev, Is.False);
            Assert.That(page1.NextCursor, Is.Not.Null);
            Assert.That(page1.PrevCursor, Is.Null);
        });

        // Backward from r1 (repository returns [r2, r3] in ASC order, no extra row -> hasPrev=false, hasNext=true, reversed to [r3, r2])
        var prevToken = _cursorCodec.Encode(new CursorPosition(r1.CreatedAt, r1.Id));
        _orderRepoMock
            .Setup(x => x.ListSummariesByCursorAsync(
                "cust-1",
                3,
                It.Is<CursorPosition>(c => c.Id == 1UL),
                CursorDirection.Backward,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([r2, r3]);

        var backPage = await _sut.ListOrdersAsync(new ListOrdersQuery(" cust-1 ", 2, null, prevToken));

        Assert.Multiple(() =>
        {
            Assert.That(backPage.Items.Select(x => x.OrderCode), Is.EqualTo(new[] { r3.OrderCode, r2.OrderCode }));
            Assert.That(backPage.HasPrev, Is.False);
            Assert.That(backPage.HasNext, Is.True);
            Assert.That(backPage.PrevCursor, Is.Null);
            Assert.That(backPage.NextCursor, Is.Not.Null);
        });
    }

    [Test]
    public async Task ListOrdersAsync_EmptyResultAndValidationGuards_BehaveCorrectly()
    {
        _orderRepoMock
            .Setup(x => x.ListSummariesByCursorAsync(null, 21, null, CursorDirection.Forward, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OrderSummaryView>());

        var empty = await _sut.ListOrdersAsync(new ListOrdersQuery(null, 0, null, null));
        Assert.Multiple(() =>
        {
            Assert.That(empty.Items, Is.Empty);
            Assert.That(empty.HasNext, Is.False);
            Assert.That(empty.HasPrev, Is.False);
            Assert.That(empty.NextCursor, Is.Null);
            Assert.That(empty.PrevCursor, Is.Null);
        });

        Assert.ThrowsAsync<ArgumentNullException>(() => _sut.ListOrdersAsync(null!));
        Assert.ThrowsAsync<DomainValidationException>(() =>
            _sut.ListOrdersAsync(new ListOrdersQuery(null, 10, "a", "b")));
        Assert.ThrowsAsync<DomainValidationException>(() =>
            _sut.ListOrdersAsync(new ListOrdersQuery(null, -1, null, null)));
        Assert.ThrowsAsync<DomainValidationException>(() =>
            _sut.ListOrdersAsync(new ListOrdersQuery(null, 101, null, null)));
        Assert.ThrowsAsync<DomainValidationException>(() =>
            _sut.ListOrdersAsync(new ListOrdersQuery(new string('c', 65), 10, null, null)));
        Assert.ThrowsAsync<DomainValidationException>(() =>
            _sut.ListOrdersAsync(new ListOrdersQuery(null, 10, "malformed", null)));
    }

    private static OrderSummaryView CreateSummary(ulong id, DateTime createdAt) => new()
    {
        Id = id,
        OrderCode = Guid.NewGuid().ToString("D"),
        CustomerId = "cust-1",
        Sku = "SKU-1",
        Quantity = 1,
        UnitPrice = 10.00m,
        TotalAmount = 10.00m,
        Status = OrderStatus.Pending,
        CreatedAt = createdAt
    };

    private static void SetEntityId(Order order, ulong id)
    {
        typeof(Order).BaseType!.GetProperty(nameof(Order.Id))!.SetValue(order, id);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
