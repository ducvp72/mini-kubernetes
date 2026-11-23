---
name: resource-gc-management
description: Use when writing or reviewing .NET 8 code handling files, streams, allocations, lambdas, or long-lived objects. Prevents handle leaks, Gen2/LOH GC pressure, and closure capture traps under high throughput.
---

# Resource & GC Management (.NET 8 High-Throughput)

Under 200k RPS load, small allocation leaks and un-disposed handles rapidly trigger Gen2 GC pauses, thread-pool starvation, or OS file-descriptor exhaustion.

## 1. File Manager Rule (Always Close & Dispose)

Every file handle, stream, reader, or writer **must** be deterministically closed with `await using` (preferred for async I/O) or `using`:

```csharp
await using var stream = new FileStream(
    path, FileMode.Open, FileAccess.Read, FileShare.Read,
    bufferSize: 4096, useAsync: true);
using var reader = new StreamReader(stream);
var content = await reader.ReadToEndAsync(ct);
```

- Never return a `Stream` without transferring explicit ownership to the caller (who must dispose it).
- Never rely on finalizers (`~Class()`) to release OS handles (`FileStream`, sockets, DB connections).
- Prefer `ArrayPool<byte>.Shared` or `RecyclableMemoryStream` over allocating large `byte[]` / `MemoryStream` buffers repeatedly.

## 2. Variable, Object Hygiene & Memory Leak Prevention

- **Zero unused variables/objects**: Never declare unused locals, fields, or temporary collections. Treat compiler warnings (`CS0168`, `CS0219`, `IDE0059`) as defects.
- **Short-lived allocations**: Keep request-scoped objects small and short-lived so they are collected cheaply in Gen0.
- **Avoid LOH (Large Object Heap) churn**: Objects ≥ 85,000 bytes go straight to LOH (collected only in Gen2). Stream payloads instead of buffering large JSON/strings in memory.
- **Common .NET leak sources to eliminate**:
  - **Unsubscribed events**: If a short-lived object subscribes to a `Singleton` event, always unsubscribe in `Dispose()`.
  - **Unbounded static caches**: Never use `static ConcurrentDictionary<K, V>` without size limits or eviction (`IMemoryCache` with `SizeLimit` + TTL).
  - **Un-disposed primitives**: Always dispose `CancellationTokenSource` (especially `CreateLinkedTokenSource`), `PeriodicTimer`, `Timer`, and `HttpResponseMessage`.
  - **Socket exhaustion**: Never `new HttpClient()` per request — always use `IHttpClientFactory` or typed resilient clients.

## 3. Closure Variable Traps

Closures allocate hidden display-class objects on the heap and can silently capture the wrong variable or extend object lifetimes dangerously.

### Trap A: Capturing Loop Variables
```csharp
// BAD: All tasks may observe the final value of i
for (var i = 0; i < items.Count; i++)
    tasks.Add(Task.Run(() => ProcessAsync(items[i], ct), ct));

// GOOD: Copy into loop-local variable or pass state explicitly
foreach (var item in items)
{
    var current = item;
    tasks.Add(ProcessAsync(current, ct));
}
```

### Trap B: Hidden Closure Allocations on Hot Paths
Use `static` lambdas (`static x => ...`) on hot paths so the compiler enforces zero variable capture:

```csharp
// Compiler-verified zero closure allocation
var ids = orders.Select(static o => o.Id);
```

### Trap C: Capturing Scoped Services (`DbContext`) in Background Tasks
Never capture a request-scoped `DbContext` or repository inside `Task.Run(...)` or a fire-and-forget delegate — the HTTP/gRPC request will finish and dispose the scope while the background task is still running.

```csharp
// BAD: _db is disposed when request ends -> ObjectDisposedException / corruption
_ = Task.Run(() => _db.Orders.AddAsync(order));

// GOOD: Create a new scope inside a managed BackgroundService via IServiceScopeFactory
await using var scope = _scopeFactory.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
```

## Checklist
- [ ] Every `FileStream`, `StreamReader`, `StreamWriter`, `CancellationTokenSource`, and `Timer` uses `using` / `await using`.
- [ ] No unused variables, unbounded static dictionaries, or per-request `new HttpClient()`.
- [ ] Hot-path lambdas use `static` where no capture is needed.
- [ ] No request-scoped services (`DbContext`, repositories) are captured in background delegates.
