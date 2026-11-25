namespace OrderService.Domain.Events;

// Pattern: Domain Event — defines the minimal immutable contract for domain state transitions.
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTime OccurredAtUtc { get; }
}
