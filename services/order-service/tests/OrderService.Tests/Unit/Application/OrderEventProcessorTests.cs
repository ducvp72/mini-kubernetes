using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Events;
using OrderService.Application.Services;
using OrderService.Domain.Repositories;

namespace OrderService.Tests.Unit.Application;

[TestFixture]
[Category("Unit")]
public sealed class OrderEventProcessorTests
{
    private Mock<IOrderRepository> _orderRepoMock = null!;
    private Mock<IWebhookDispatcher> _webhookMock = null!;
    private OrderEventProcessor _sut = null!;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [SetUp]
    public void SetUp()
    {
        _orderRepoMock = new Mock<IOrderRepository>();
        _webhookMock = new Mock<IWebhookDispatcher>();
        _sut = new OrderEventProcessor(
            _orderRepoMock.Object,
            _webhookMock.Object,
            TimeProvider.System,
            NullLogger<OrderEventProcessor>.Instance);
    }

    [Test]
    public async Task ProcessMessageAsync_PendingOrderWithCallback_ConfirmsAndDispatchesWebhook()
    {
        var evt = new OrderCreatedIntegrationEvent(
            Guid.NewGuid(),
            42UL,
            Guid.NewGuid().ToString("D"),
            "cust-1",
            "SKU-1",
            2,
            15.00m,
            "http://mock-webhook:8090/callback",
            1,
            1UL,
            DateTime.UtcNow);

        _orderRepoMock
            .Setup(x => x.TryConfirmOrderAsync(42UL, 1UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _webhookMock
            .Setup(x => x.SendOrderCallbackAsync(
                It.Is<WebhookCallbackRequest>(r =>
                    r.OrderCode == evt.OrderCode &&
                    r.Status == "CONFIRMED" &&
                    r.CallbackUrl == evt.CallbackUrl &&
                    r.CorrelationId == evt.EventId.ToString("D")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookDispatchResult(true, 200, 1, string.Empty));

        var processed = await _sut.ProcessMessageAsync(evt.OrderCode, JsonSerializer.Serialize(evt, JsonOptions));

        Assert.That(processed, Is.True);
        _webhookMock.Verify(x => x.SendOrderCallbackAsync(It.IsAny<WebhookCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessMessageAsync_DuplicateAlreadyConfirmedOrder_SkipsWebhook()
    {
        var evt = new OrderCreatedIntegrationEvent(
            Guid.NewGuid(),
            42UL,
            Guid.NewGuid().ToString("D"),
            "cust-1",
            "SKU-1",
            1,
            15.00m,
            "http://mock-webhook:8090/callback",
            0,
            1UL,
            DateTime.UtcNow);

        _orderRepoMock
            .Setup(x => x.TryConfirmOrderAsync(42UL, 1UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var processed = await _sut.ProcessMessageAsync(evt.OrderCode, JsonSerializer.Serialize(evt, JsonOptions));

        Assert.That(processed, Is.False);
        _webhookMock.Verify(x => x.SendOrderCallbackAsync(It.IsAny<WebhookCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessMessageAsync_EmptyCallbackUrl_ConfirmsAndSkipsWebhook()
    {
        var evt = new OrderCreatedIntegrationEvent(
            Guid.NewGuid(),
            55UL,
            Guid.NewGuid().ToString("D"),
            "cust-1",
            "SKU-1",
            1,
            10.00m,
            null,
            0,
            0UL, // defaults expectedRowVersion to 1
            DateTime.UtcNow);

        _orderRepoMock
            .Setup(x => x.TryConfirmOrderAsync(55UL, 1UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var processed = await _sut.ProcessMessageAsync(evt.OrderCode, JsonSerializer.Serialize(evt, JsonOptions));

        Assert.That(processed, Is.True);
        _webhookMock.Verify(x => x.SendOrderCallbackAsync(It.IsAny<WebhookCallbackRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessMessageAsync_WebhookDeliveryFailed_LogsWithoutThrowing()
    {
        var evt = new OrderCreatedIntegrationEvent(
            Guid.NewGuid(),
            99UL,
            Guid.NewGuid().ToString("D"),
            "cust-1",
            "SKU-1",
            1,
            10.00m,
            "http://mock-webhook:8090/callback",
            0,
            1UL,
            DateTime.UtcNow);

        _orderRepoMock
            .Setup(x => x.TryConfirmOrderAsync(99UL, 1UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _webhookMock
            .Setup(x => x.SendOrderCallbackAsync(It.IsAny<WebhookCallbackRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookDispatchResult(false, 503, 3, "upstream 503"));

        var processed = await _sut.ProcessMessageAsync(evt.OrderCode, JsonSerializer.Serialize(evt, JsonOptions));

        Assert.That(processed, Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{invalid-json")]
    [TestCase("""{"orderId":0,"orderCode":""}""")]
    public async Task ProcessMessageAsync_MalformedPayloads_ReturnsFalseSafely(string payload)
    {
        var processed = await _sut.ProcessMessageAsync("key-1", payload);

        Assert.That(processed, Is.False);
        _orderRepoMock.Verify(x => x.TryConfirmOrderAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
