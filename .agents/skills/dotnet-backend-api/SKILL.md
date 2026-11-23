---
name: dotnet-backend-api
description: Use when scaffolding, configuring, or refactoring .NET 8 backend servers (open-api REST or order-service gRPC) in mini-kubernetes. Covers modular Program.cs setup, EF Core Code-First migrations, 3-layer architecture, and explicit DI lifetimes.
---

# .NET 8 Backend API Architecture

Guide for starting and structuring `.NET 8` services (`services/open-api` REST and `services/order-service` gRPC) for high-throughput workloads in `mini-kubernetes`.

## 1. Modular `Program.cs` & Startup Organization

Keep `Program.cs` minimal and declarative. Delegate DI registrations and middleware pipelines to focused extension methods under `Extensions/`.

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddGlobalServices(builder.Configuration)      // Cross-cutting singletons, options, telemetry, resilience
    .AddPersistence(builder.Configuration)         // DbContextPool, repositories
    .AddApplicationServices()                      // Domain/application services
    .AddPresentationServices();                    // Controllers or gRPC + health checks

var app = builder.Build();

app.UseApiMiddleware();                            // Exception handling, correlation ID, routing
app.MapApiEndpoints();                             // Controllers / gRPC services + /healthz, /readyz
app.Run();
```

### Kestrel & HTTP/2 Cleartext (`h2c`) for gRPC
In `order-service`, configure Kestrel explicitly so internal plaintext gRPC works reliably in Docker Compose and Kubernetes:
- Bind gRPC port with `HttpProtocols.Http2`.
- Keep HTTP/1.1 on a separate port if exposing REST/health probes alongside gRPC.

## 2. Database Style: Always Code-First (EF Core)

- **Always use EF Core Code-First** with versioned migrations (`dotnet ef migrations add <Name>`).
- Never hand-modify the database schema outside migrations.
- Configure entities via `IEntityTypeConfiguration<T>` classes in `Infrastructure/Persistence/Configurations/` rather than cluttering `DbContext.OnModelCreating`.
- For MySQL (`Pomelo.EntityFrameworkCore.MySql`), specify explicit `ServerVersion` and column types/lengths so migrations stay deterministic.
- Run migrations deliberately (init container / migration job or controlled startup flag), never blindly on every replica concurrently during a rolling deploy without locking/idempotency.

## 3. Explicit 3-Layer Architecture

Enforce strict unidirectional flow for both REST and gRPC entrypoints:

```text
Controller / gRPC Service  -->  Service (Application/Domain)  -->  Repository (Data Access)
```

| Layer | Responsibility | Forbidden |
| --- | --- | --- |
| **Controller / gRPC Service** | Request validation, DTO/Proto mapping, HTTP/gRPC status codes, passing `CancellationToken` | Direct `DbContext` access, business rules, SQL |
| **Service** | Use-case orchestration, transaction boundaries, domain event dispatching, business invariants | `HttpContext`, gRPC `ServerCallContext`, raw SQL strings |
| **Repository** | Persistence queries/commands, EF Core / parameterized SQL, mapping DB primitives | HTTP/gRPC concerns, cross-aggregate orchestration |

## 4. Dependency Injection & Lifetimes

**Always use DI** — never `new` up services, repositories, `HttpClient`, or `DbContext` manually.

### Configuration via `IOptions<T>`
Bind connection strings and settings into strongly typed options validated on startup:

```csharp
builder.Services.AddOptions<DatabaseOptions>()
    .BindConfiguration(DatabaseOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

### Explicit Service Lifetimes
| Lifetime | Use For | Pitfall to Avoid |
| --- | --- | --- |
| `Scoped` | `DbContext` (pooled), Repositories, Application Services, Unit of Work | Never inject `Scoped` into `Singleton` (causes captive dependency / concurrency corruption) |
| `Singleton` | Stateless helpers, `IOptions<T>`, Kafka producers, gRPC channels, `TimeProvider`, caches | Must be strictly thread-safe; never hold request state |
| `Transient` | Lightweight stateless validators or factories with no shared state | Avoid for `IDisposable` objects resolved from root container |

### Global DI Registration Module
Centralize cross-cutting/global registrations in `Extensions/GlobalServiceCollectionExtensions.cs`:

```csharp
public static class GlobalServiceCollectionExtensions
{
    public static IServiceCollection AddGlobalServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks();
        // Register global Kafka producer, gRPC clients, resilience handlers, and validated IOptions<T>
        return services;
    }
}
```

Enable DI scope validation in Development (`ValidateScopes = true`, `ValidateOnBuild = true`) to catch captive dependencies immediately.

## Checklist
- [ ] `Program.cs` delegates to modular `Add*` / `Use*` extension methods.
- [ ] All schema changes live in EF Core Code-First migrations.
- [ ] Controllers and gRPC services never reference `DbContext` directly.
- [ ] Connection strings and config are injected via validated `IOptions<T>` / `IConfiguration`.
- [ ] Cross-cutting singletons live in the Global DI module with zero captive `Scoped` dependencies.
