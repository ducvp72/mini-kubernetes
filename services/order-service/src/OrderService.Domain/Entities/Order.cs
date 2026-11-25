using OrderService.Domain.Common;
using OrderService.Domain.Enums;
using OrderService.Domain.Events;
using OrderService.Domain.Exceptions;

namespace OrderService.Domain.Entities;

public sealed class Order : BaseEntity<ulong>
{
    public const int OrderCodeLength = 36;
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxCustomerIdLength = 64;
    public const int MaxSkuLength = 64;
    public const int MaxCallbackUrlLength = 512;
    public const uint MaxSimulateWorkMs = 5000;
    public const decimal MaxUnitPrice = 999_999_999_999_999.99m;

    private Order()
    {
    }

    public string OrderCode { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string CustomerId { get; private set; } = string.Empty;

    public string Sku { get; private set; } = string.Empty;

    public uint Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal TotalAmount => Quantity * UnitPrice;

    public OrderStatus Status { get; private set; } = OrderStatus.Pending;

    public string? CallbackUrl { get; private set; }

    public uint SimulateWorkMs { get; private set; }

    // Pattern: Factory — enforces Order invariants and raises OrderCreatedDomainEvent on aggregate creation.
    public static Order Create(
        string idempotencyKey,
        string customerId,
        string sku,
        uint quantity,
        decimal unitPrice,
        string? callbackUrl,
        uint simulateWorkMs,
        DateTime utcNow,
        string? orderCode = null)
    {
        var normalizedIdempotencyKey = ValidateRequiredText(idempotencyKey, nameof(idempotencyKey), MaxIdempotencyKeyLength);
        var normalizedCustomerId = ValidateRequiredText(customerId, nameof(customerId), MaxCustomerIdLength);
        var normalizedSku = ValidateRequiredText(sku, nameof(sku), MaxSkuLength);

        if (quantity == 0)
        {
            throw new DomainValidationException("Quantity must be greater than zero.");
        }

        if (unitPrice < 0 || unitPrice > MaxUnitPrice)
        {
            throw new DomainValidationException($"Unit price must be between 0 and {MaxUnitPrice}.");
        }

        if (Math.Round(unitPrice, 2, MidpointRounding.AwayFromZero) != unitPrice)
        {
            throw new DomainValidationException("Unit price must have at most 2 decimal places.");
        }

        if (simulateWorkMs > MaxSimulateWorkMs)
        {
            throw new DomainValidationException($"simulateWorkMs cannot exceed {MaxSimulateWorkMs} ms.");
        }

        var normalizedCallbackUrl = ValidateOptionalCallbackUrl(callbackUrl);
        var normalizedOrderCode = string.IsNullOrWhiteSpace(orderCode)
            ? Guid.NewGuid().ToString("D")
            : orderCode.Trim();

        if (normalizedOrderCode.Length != OrderCodeLength)
        {
            throw new DomainValidationException($"Order code must be exactly {OrderCodeLength} characters.");
        }

        var timestamp = TimestampPrecision.ToUtcMicroseconds(utcNow);

        var order = new Order
        {
            OrderCode = normalizedOrderCode,
            IdempotencyKey = normalizedIdempotencyKey,
            CustomerId = normalizedCustomerId,
            Sku = normalizedSku,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Status = OrderStatus.Pending,
            CallbackUrl = normalizedCallbackUrl,
            SimulateWorkMs = simulateWorkMs,
            RowVersion = 1,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };

        order.AddDomainEvent(new OrderCreatedDomainEvent(
            Guid.NewGuid(),
            order.OrderCode,
            order.CustomerId,
            order.Sku,
            order.Quantity,
            order.UnitPrice,
            order.CallbackUrl,
            order.SimulateWorkMs,
            timestamp));

        return order;
    }

    public void MarkConfirmed(DateTime utcNow)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new DomainValidationException($"Cannot confirm order in status '{Status}'.");
        }

        Status = OrderStatus.Confirmed;
        RowVersion++;
        UpdatedAt = TimestampPrecision.ToUtcMicroseconds(utcNow);
    }

    public void MarkFailed(DateTime utcNow)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new DomainValidationException($"Cannot fail order in status '{Status}'.");
        }

        Status = OrderStatus.Failed;
        RowVersion++;
        UpdatedAt = TimestampPrecision.ToUtcMicroseconds(utcNow);
    }

    private static string ValidateRequiredText(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException($"{fieldName} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{fieldName} cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? ValidateOptionalCallbackUrl(string? callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            return null;
        }

        var trimmed = callbackUrl.Trim();
        if (trimmed.Length > MaxCallbackUrlLength)
        {
            throw new DomainValidationException($"callbackUrl cannot exceed {MaxCallbackUrlLength} characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DomainValidationException("callbackUrl must be a valid absolute http or https URL.");
        }

        return trimmed;
    }
}
