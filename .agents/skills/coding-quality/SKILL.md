---
name: coding-quality
description: Use when writing, reviewing, or testing .NET 8 services in mini-kubernetes. Enforces >=80% test coverage, async/concurrency safety, strict null safety, CancellationToken propagation, low-allocation logging, resilience, and clean DTO boundaries.
---

# Coding Quality & High-Throughput Reliability (.NET 8)

Engineering standards for production-grade, stress-test-ready `.NET 8` code in `mini-kubernetes`.

## 1. Test Coverage (Target ≥ 80%, Aim for 100% on Core Paths)

- Target **≥ 80% line/branch coverage** overall, aiming for **100% on critical domain logic, state transitions, repositories, and concurrency/idempotency paths**.
- Combine:
  - **Unit tests (`NUnit` + `Moq`)**: Fast, isolated tests (`[Category("Unit")]`) for domain invariants, services, mappers, cursor encoding, and edge-case validation.
  - **Integration tests (`NUnit` + Docker container scope)**: Real EF Core / MySQL + Redpanda containers (`[Category("Integration")]`) verifying migrations, SQL views/stored procedures, queries, transactions/rollbacks, and HTTP/gRPC endpoints.
- Collect coverage with `dotnet test --collect:"XPlat Code Coverage"`.

## 2. Concurrency, Deadlock & Race Safety

- **Ban sync-over-async**: Never call `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on tasks — causes thread-pool starvation and deadlocks under load. Go `async`/`await` all the way down.
- **No `await` inside `lock`**: Use `SemaphoreSlim` for async critical sections, and always bound waits with a timeout + `CancellationToken`:
  ```csharp
  if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
      throw new TimeoutException("Timed out waiting for resource lock.");
  try { /* critical section */ }
  finally { _gate.Release(); }
  ```
- **Consistent lock ordering**: Acquire multiple locks or DB row locks in a strict canonical order to prevent AB-BA deadlocks.
- **Idempotent handlers**: Design Kafka consumers, webhook processors, and retryable endpoints to handle duplicate deliveries safely (idempotency key / unique constraint).

## 3. Strict Null Safety

- Enable `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (or nullable warnings as errors) in every `.csproj`.
- Guard all reference parameters and nullable values explicitly before dereferencing:
  ```csharp
  ArgumentNullException.ThrowIfNull(request);
  if (order is null) return NotFound();
  ```
- Never silence compiler warnings with null-forgiving `!` unless provably guaranteed by framework contracts (e.g., EF Core navigation initialized by runtime).

## 4. High-Throughput Quality Essentials

### End-to-End `CancellationToken` Propagation
Pass `CancellationToken ct` from every Controller / gRPC Service -> Application Service -> Repository -> EF Core / `HttpClient` / gRPC client / Kafka producer. When a client disconnects under load, aborting in-flight work immediately frees DB connections and threads.

### Structured, Low-Allocation Logging
- Use source-generated `[LoggerMessage]` methods on hot paths (zero boxing, zero string interpolation when log level is disabled):
  ```csharp
  [LoggerMessage(Level = LogLevel.Information, Message = "Created order {OrderId} for customer {CustomerId}")]
  public static partial void LogOrderCreated(this ILogger logger, Guid orderId, Guid customerId);
  ```
- Always use structured message templates (`{OrderId}`), never `$"..."` interpolation in `ILogger` calls.
- **Never log secrets, tokens, connection strings, or PII.**

### Bounded Resilience (Prevent Retry Storms)
Configure outbound HTTP/gRPC clients with `Microsoft.Extensions.Http.Resilience` / Polly:
- **Per-attempt & total timeouts** (bounded).
- **Exponential backoff with jitter** and low retry count (e.g., max 2–3 retries, only on transient errors).
- **Circuit breaker** to fail fast when a downstream dependency is saturated.

### Immutable DTOs, Edge Validation & Error Hygiene
- Use `sealed record` for request/response DTOs and domain events.
- Validate all external inputs strictly at the API/gRPC edge (length, range, non-empty IDs, cursor format) and return standardized problem details / gRPC status codes.
- **Zero swallowed exceptions**: Never write empty `catch { }`. Catch only specific recoverable exceptions, log with context, or let the global exception handler map unhandled faults cleanly.
