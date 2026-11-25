using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using OrderService.Application.Abstractions;
using OrderService.Application.Options;
using OrderService.Application.Services;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Repositories;

namespace OrderService.Tests.Unit.Application;

[TestFixture]
[Category("Unit")]
public sealed class OutboxRelayProcessorTests
{
    private Mock<IUnitOfWork> _unitOfWorkMock = null!;
    private Mock<ITransactionScope> _txMock = null!;
    private Mock<IOutboxRepository> _outboxRepoMock = null!;
    private Mock<IOrderEventPublisher> _publisherMock = null!;
    private OutboxRelayProcessor _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _txMock = new Mock<ITransactionScope>();
        _outboxRepoMock = new Mock<IOutboxRepository>();
        _publisherMock = new Mock<IOrderEventPublisher>();

        _unitOfWorkMock
            .Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_txMock.Object);

        _sut = new OutboxRelayProcessor(
            _unitOfWorkMock.Object,
            _outboxRepoMock.Object,
            _publisherMock.Object,
            Microsoft.Extensions.Options.Options.Create(new OutboxOptions { BatchSize = 10, MaxRetries = 3 }),
            TimeProvider.System,
            NullLogger<OutboxRelayProcessor>.Instance);
    }

    [Test]
    public async Task ProcessNextBatchAsync_WhenEmpty_CommitsAndReturnsZero()
    {
        _outboxRepoMock
            .Setup(x => x.ClaimPendingBatchAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OutboxMessage>());

        var count = await _sut.ProcessNextBatchAsync();

        Assert.That(count, Is.EqualTo(0));
        _publisherMock.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_WhenMessagesClaimed_PublishesMarksPublishedAndCommits()
    {
        var m1 = OutboxMessage.Rehydrate(1, 10, "order.created", "code-1", "{\"id\":1}", OutboxMessageStatus.Pending, 0, DateTime.UtcNow, null);
        var m2 = OutboxMessage.Rehydrate(2, 11, "order.created", "code-2", "{\"id\":2}", OutboxMessageStatus.Pending, 0, DateTime.UtcNow, null);

        _outboxRepoMock
            .Setup(x => x.ClaimPendingBatchAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([m1, m2]);

        var count = await _sut.ProcessNextBatchAsync();

        Assert.That(count, Is.EqualTo(2));
        _publisherMock.Verify(x => x.PublishAsync("order.created", "code-1", "{\"id\":1}", It.IsAny<CancellationToken>()), Times.Once);
        _publisherMock.Verify(x => x.PublishAsync("order.created", "code-2", "{\"id\":2}", It.IsAny<CancellationToken>()), Times.Once);
        _outboxRepoMock.Verify(x => x.MarkPublishedAsync(1UL, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _outboxRepoMock.Verify(x => x.MarkPublishedAsync(2UL, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_PublishFailure_IncrementsRetryAndTransitionsToDeadLetterAtMaxRetries()
    {
        var retryMsg = OutboxMessage.Rehydrate(1, 10, "order.created", "code-retry", "{}", OutboxMessageStatus.Pending, 0, DateTime.UtcNow, null);
        var deadLetterMsg = OutboxMessage.Rehydrate(2, 11, "order.created", "code-dlq", "{}", OutboxMessageStatus.Pending, 2, DateTime.UtcNow, null);

        _outboxRepoMock
            .Setup(x => x.ClaimPendingBatchAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([retryMsg, deadLetterMsg]);

        _publisherMock
            .Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker unreachable"));

        var count = await _sut.ProcessNextBatchAsync();

        Assert.That(count, Is.EqualTo(2));
        _outboxRepoMock.Verify(
            x => x.MarkRetryOrDeadLetterAsync(1UL, 1, OutboxMessageStatus.Pending, null, It.IsAny<CancellationToken>()),
            Times.Once);
        _outboxRepoMock.Verify(
            x => x.MarkRetryOrDeadLetterAsync(2UL, 3, OutboxMessageStatus.DeadLetter, It.Is<DateTime?>(d => d.HasValue), It.IsAny<CancellationToken>()),
            Times.Once);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void ProcessNextBatchAsync_WhenCancelled_RollsBackAndPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var msg = OutboxMessage.Rehydrate(1, 10, "order.created", "code-1", "{}", OutboxMessageStatus.Pending, 0, DateTime.UtcNow, null);

        _outboxRepoMock
            .Setup(x => x.ClaimPendingBatchAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([msg]);

        _publisherMock
            .Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ProcessNextBatchAsync(cts.Token));
        _txMock.Verify(x => x.RollbackAsync(CancellationToken.None), Times.Once);
        _txMock.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
