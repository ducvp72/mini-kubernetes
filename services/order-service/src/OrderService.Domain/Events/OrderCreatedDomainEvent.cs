namespace OrderService.Domain.Events;

// Pattern: Domain Event — captures immutable Order creation facts for decoupled Transactional Outbox persistence.
public sealed record OrderCreatedDomainEvent(
    Guid EventId,
    string OrderCode,
    string CustomerId,
    string Sku,
    uint Quantity,
    decimal UnitPrice,
    string? CallbackUrl,
    uint SimulateWorkMs,
    DateTime OccurredAtUtc) : IDomainEvent;
