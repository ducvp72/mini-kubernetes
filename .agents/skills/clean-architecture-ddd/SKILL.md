---
name: clean-architecture-ddd
description: Use when designing module boundaries, domain models, design patterns, or domain events in mini-kubernetes .NET 8 services. Balances Clean Architecture and DDD with minimal lab business logic.
---

# Clean Architecture & Pragmatic DDD

Keep `mini-kubernetes` services cleanly layered and interview-ready without over-engineering minimal lab domain logic.

## 1. Pragmatic Clean Architecture Layers

Organize `order-service` (and `open-api` proportionally) with strict inward dependency direction:

```text
Presentation (Controllers / gRPC Services)
      │
      ▼
Application (Use-Case Services, DTOs, Interfaces, Event Handlers)
      │
      ▼
Domain (Entities, Value Objects, Domain Events, Invariants)
      ▲
      │
Infrastructure (EF Core DbContext, Repositories, Kafka/Redpanda Producer, Outbox)
```

- **Domain**: Zero dependency on EF Core, ASP.NET Core, gRPC, or Kafka. Holds entities (`Order`), state transitions (`MarkConfirmed()`), and domain event records.
- **Application**: Defines repository/messaging ports (`IOrderRepository`, `IEventPublisher`) and orchestrates use cases.
- **Infrastructure**: Implements ports using EF Core + MySQL and Confluent.Kafka/Redpanda.
- **Presentation**: Thin REST controllers (`open-api`) or gRPC services (`order-service`) that validate input, invoke Application services, and map responses.
- **Pragmatism rule**: Keep folders/projects lean — do not introduce unnecessary MediatR pipelines, CQRS read-store splits, or deep inheritance hierarchies when a direct Service + Repository is clearer.

## 2. DRY & Design Patterns (With 1-Line Pattern Comments)

Apply design patterns **only** when they eliminate real duplication or isolate change:
- **Repository & Unit of Work**: Abstract persistence and coordinate atomic commits.
- **Outbox Pattern**: Guarantee reliable Kafka/Redpanda event publishing alongside MySQL writes.
- **Strategy**: Swap algorithms cleanly (e.g., webhook retry/backoff or routing policies).
- **Factory**: Encapsulate valid aggregate construction with invariants (`Order.Create(...)`).

**Mandatory Convention**: Place a concise **1-line comment** directly above any pattern class or method naming the exact pattern and its purpose so reviewers and interviewers can spot it immediately:

```csharp
// Pattern: Outbox — persists domain events atomically with aggregate writes for reliable Kafka dispatch.
public sealed class OutboxProcessor : BackgroundService { ... }

// Pattern: Factory — enforces Order invariants and raises OrderCreatedDomainEvent on creation.
public static Order Create(Guid customerId, decimal amount) { ... }
```

## 3. Domain-Driven Events (Scaled to Project Capacity)

Model significant state changes as lightweight immutable records in `Domain/Events/`:

```csharp
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

public sealed record OrderCreatedDomainEvent(
    Guid EventId,
    Guid OrderId,
    decimal Amount,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

### Dispatching Rules
1. Entities collect raised events in an internal list (`AddDomainEvent(...)`) during domain mutations.
2. **Never publish directly to Kafka before the DB transaction commits** (causes ghost events on rollback).
3. **Preferred (Transactional Outbox)**: Inside `SaveChangesAsync`, serialize domain events into an `outbox_messages` table in the same MySQL transaction; a lightweight background worker publishes pending rows to Redpanda/Kafka and marks them processed.
4. **Lightweight fallback** (when Outbox is out of scope for a phase): Dispatch domain events strictly *after* `await tx.CommitAsync(ct)` with clear idempotency keys (`EventId`).

## Checklist
- [ ] `Domain` has zero references to EF Core, ASP.NET, gRPC, or Kafka.
- [ ] Every design pattern class/method includes a 1-line `// Pattern: <Name> — <purpose>` comment.
- [ ] Domain events are raised on state transitions and never published before DB commit.
- [ ] Business logic remains minimal and focused on core order/webhook flow.
