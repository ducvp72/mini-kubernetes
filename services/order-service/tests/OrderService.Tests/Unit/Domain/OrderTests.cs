using NUnit.Framework;
using OrderService.Domain.Common;
using OrderService.Domain.Entities;
using OrderService.Domain.Enums;
using OrderService.Domain.Events;
using OrderService.Domain.Exceptions;

namespace OrderService.Tests.Unit.Domain;

[TestFixture]
[Category("Unit")]
public sealed class OrderTests
{
    private static readonly DateTime FixedUtc = new(2026, 11, 23, 12, 30, 45, 123, DateTimeKind.Utc);

    [Test]
    public void Create_WithValidInputs_InitializesPendingOrderAndRaisesDomainEvent()
    {
        var rawTime = FixedUtc.AddTicks(47); // includes sub-microsecond remainder (7 ticks)

        var order = Order.Create(
            "  idem-001  ",
            " cust-42 ",
            " SKU-FLASH-01 ",
            3,
            19.99m,
            " https://example.com/hook ",
            250,
            rawTime);

        Assert.Multiple(() =>
        {
            Assert.That(order.OrderCode, Has.Length.EqualTo(Order.OrderCodeLength));
            Assert.That(order.IdempotencyKey, Is.EqualTo("idem-001"));
            Assert.That(order.CustomerId, Is.EqualTo("cust-42"));
            Assert.That(order.Sku, Is.EqualTo("SKU-FLASH-01"));
            Assert.That(order.Quantity, Is.EqualTo(3u));
            Assert.That(order.UnitPrice, Is.EqualTo(19.99m));
            Assert.That(order.TotalAmount, Is.EqualTo(59.97m));
            Assert.That(order.Status, Is.EqualTo(OrderStatus.Pending));
            Assert.That(order.CallbackUrl, Is.EqualTo("https://example.com/hook"));
            Assert.That(order.SimulateWorkMs, Is.EqualTo(250u));
            Assert.That(order.RowVersion, Is.EqualTo(1UL));
            Assert.That(order.CreatedAt.Ticks % 10, Is.EqualTo(0));
            Assert.That(order.UpdatedAt, Is.EqualTo(order.CreatedAt));
            Assert.That(order.DomainEvents, Has.Count.EqualTo(1));
        });

        var evt = order.DomainEvents.Single() as OrderCreatedDomainEvent;
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.OrderCode, Is.EqualTo(order.OrderCode));
        Assert.That(evt.CustomerId, Is.EqualTo("cust-42"));
        Assert.That(evt.Quantity, Is.EqualTo(3u));

        order.ClearDomainEvents();
        Assert.That(order.DomainEvents, Is.Empty);
    }

    [Test]
    public void Create_WithEmptyCallbackUrl_NormalizesToNull()
    {
        var order = Order.Create("idem-002", "cust-1", "SKU-1", 1, 0m, "   ", 0, FixedUtc);

        Assert.That(order.CallbackUrl, Is.Null);
        Assert.That(order.TotalAmount, Is.EqualTo(0m));
    }

    [TestCase("", "cust-1", "SKU-1")]
    [TestCase("   ", "cust-1", "SKU-1")]
    [TestCase("idem-1", "", "SKU-1")]
    [TestCase("idem-1", "cust-1", "")]
    public void Create_WithMissingRequiredStrings_ThrowsDomainValidationException(string idem, string customer, string sku)
    {
        Assert.Throws<DomainValidationException>(() =>
            Order.Create(idem, customer, sku, 1, 10m, null, 0, FixedUtc));
    }

    [Test]
    public void Create_WithExcessiveFieldLengths_ThrowsDomainValidationException()
    {
        Assert.Throws<DomainValidationException>(() =>
            Order.Create(new string('a', 129), "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", new string('c', 65), "SKU-1", 1, 10m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", new string('s', 65), 1, 10m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10m, "https://example.com/" + new string('x', 500), 0, FixedUtc));
    }

    [Test]
    public void Create_WithInvalidQuantityPriceSimulateWorkOrCallback_ThrowsDomainValidationException()
    {
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 0, 10m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, -0.01m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10.123m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, Order.MaxUnitPrice + 1m, null, 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10m, null, Order.MaxSimulateWorkMs + 1, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10m, "ftp://example.com/hook", 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10m, "not-a-url", 0, FixedUtc));
        Assert.Throws<DomainValidationException>(() =>
            Order.Create("idem-1", "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc, "too-short"));
    }

    [Test]
    public void MarkConfirmedAndMarkFailed_EnforcePendingPrecondition()
    {
        var order1 = Order.Create("idem-c1", "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc);
        var confirmTime = FixedUtc.AddSeconds(5);

        order1.MarkConfirmed(confirmTime);
        Assert.Multiple(() =>
        {
            Assert.That(order1.Status, Is.EqualTo(OrderStatus.Confirmed));
            Assert.That(order1.RowVersion, Is.EqualTo(2UL));
            Assert.That(order1.UpdatedAt, Is.EqualTo(TimestampPrecision.ToUtcMicroseconds(confirmTime)));
        });
        Assert.Throws<DomainValidationException>(() => order1.MarkConfirmed(confirmTime));
        Assert.Throws<DomainValidationException>(() => order1.MarkFailed(confirmTime));

        var order2 = Order.Create("idem-c2", "cust-1", "SKU-1", 1, 10m, null, 0, FixedUtc);
        order2.MarkFailed(confirmTime);
        Assert.That(order2.Status, Is.EqualTo(OrderStatus.Failed));
        Assert.That(order2.RowVersion, Is.EqualTo(2UL));
        Assert.Throws<DomainValidationException>(() => order2.MarkConfirmed(confirmTime));
    }

    [Test]
    public void OutboxMessage_LifecycleAndValidation_WorkDeterministically()
    {
        var msg = OutboxMessage.Create(10, "order.created", "code-123", "{\"ok\":true}", FixedUtc);

        Assert.Multiple(() =>
        {
            Assert.That(msg.OrderId, Is.EqualTo(10UL));
            Assert.That(msg.Status, Is.EqualTo(OutboxMessageStatus.Pending));
            Assert.That(msg.RetryCount, Is.EqualTo(0));
            Assert.That(msg.ProcessedAt, Is.Null);
        });

        msg.RecordFailure(2, FixedUtc.AddSeconds(1));
        Assert.That(msg.RetryCount, Is.EqualTo(1));
        Assert.That(msg.Status, Is.EqualTo(OutboxMessageStatus.Pending));
        Assert.That(msg.ProcessedAt, Is.Null);

        msg.RecordFailure(2, FixedUtc.AddSeconds(2));
        Assert.That(msg.RetryCount, Is.EqualTo(2));
        Assert.That(msg.Status, Is.EqualTo(OutboxMessageStatus.DeadLetter));
        Assert.That(msg.ProcessedAt, Is.Not.Null);

        var msg2 = OutboxMessage.Create(11, "order.created", "code-456", "{\"ok\":true}", FixedUtc);
        msg2.MarkPublished(FixedUtc.AddSeconds(3));
        Assert.That(msg2.Status, Is.EqualTo(OutboxMessageStatus.Published));
        Assert.That(msg2.ProcessedAt, Is.Not.Null);

        Assert.Throws<DomainValidationException>(() => OutboxMessage.Create(0, "order.created", "k", "{}", FixedUtc));
        Assert.Throws<DomainValidationException>(() => OutboxMessage.Create(1, "", "k", "{}", FixedUtc));
        Assert.Throws<DomainValidationException>(() => OutboxMessage.Create(1, "order.created", "", "{}", FixedUtc));
        Assert.Throws<DomainValidationException>(() => OutboxMessage.Create(1, "order.created", "k", "", FixedUtc));
        Assert.Throws<DomainValidationException>(() => msg2.RecordFailure(0, FixedUtc));
    }
}
