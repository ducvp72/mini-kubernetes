---
name: high-throughput-scale
description: Use when designing, implementing, load-testing, or reviewing high-RPS and big-data workloads (10k–200k RPS promotion bursts, millions to billions of rows) across .NET 8, Go, MySQL, Kafka/Redpanda, and Kubernetes HPA in mini-kubernetes. Enforces capacity math, async ingestion, backpressure, load shedding, hot-key/partitioning safety, cache-stampede protection, and stateless zero-drop horizontal scaling.
---

# High-Throughput & Big-Data Scale Mindset (`high-throughput-scale`)

Apply this mindset during **both architecture (`write-td`) and implementation (`implement-td`)** whenever building endpoints, consumers, schemas, or Kubernetes manifests that must survive sudden traffic bursts (e.g., 1M concurrent users / up to 200,000 req/s) and large, continuously growing datasets without cascading failure.

> **Lab Pragmatism Guardrail:** Always *design* schemas, APIs, bounds, idempotency, and async boundaries with big-data / high-RPS discipline, while keeping lab business logic minimal and staying inside Azure Free Trial caps (`AGENTS.md` Hard Rules #5–#6). For techniques requiring extra cloud hardware (read replicas, multi-shard clusters, KEDA), document the scaling path in the TD without over-provisioning extra cloud servers in the lab.

---

## 1. Capacity Math & Bottleneck Thinking First

Before writing code or choosing a sync vs. async boundary, estimate the numbers:

- **Little's Law:** `In-Flight Concurrency = RPS × Latency(sec)`.
  - At `200,000 RPS`, a `50 ms` synchronous downstream call requires **10,000 concurrent in-flight connections/tasks**.
  - Cutting synchronous latency from `50 ms` → `5 ms` (or moving slow work off the request path) reduces concurrency by **10×**.
- **Know every ceiling:**
  1. **Database writes/sec & connection pool ceiling** (single MySQL instance saturates far earlier than stateless API pods).
  2. **Network serialization & payload bytes/sec** (`200k RPS × 2 KB = 400 MB/s` = `3.2 Gbps`).
  3. **Allocation rate & GC pause pressure** (Gen2/LOH allocations collapse P99 latency under burst).
  4. **Storage growth rate** (`1M orders/day × 500 B ≈ 500 MB/day ≈ 180 GB/year` + indexes).

---

## 2. Decouple the Write Path & Absorb Bursts (Shock Absorbers)

Never tie client request latency to slow downstream I/O during a traffic spike:

- **Fast Async Ingestion (`202 Accepted`):**
  - Validate input → persist minimal state / enqueue to Kafka/Redpanda → return immediately.
  - Push downstream work (order confirmation, outbound webhooks to unreliable external HTTP endpoints) to asynchronous workers (`order-service` consumer → gRPC `webhook-service`).
- **Kafka / Redpanda Partitioning Discipline:**
  - **Partition count sets max parallel consumers** within a consumer group (`max_parallel_consumers = partition_count`).
  - **Partition key hygiene:** Partition by high-cardinality keys (`order_id` or `user_id`) so traffic spreads evenly. **NEVER** partition by a low-cardinality or hot promotion key (`promo_id`, `hot_sku`) that creates a **hot partition** bottlenecking a single broker/consumer.
- **Batching & Micro-Batching:**
  - Single-row round-trips collapse under 50k–200k RPS.
  - **Producers:** Enable batching (`LingerMs`, `BatchSize`) and fast compression (`lz4` or `zstd`).
  - **Consumers & DB Writers:** Process and commit offsets in bounded micro-batches; use multi-row `INSERT` / `FOR UPDATE SKIP LOCKED` batch claims when high write volume requires it.

---

## 3. Backpressure, Load Shedding & Resilience

Under overload, a healthy system **sheds excess traffic fast** rather than letting queues grow until every pod OOMs:

- **Bound Every Buffer & Pool:**
  - Every in-memory queue (`Channel.CreateBounded<T>` in .NET, buffered channels/semaphores in Go), worker pool (`SemaphoreSlim`), HTTP/gRPC connection pool, and DB connection pool MUST have an explicit upper bound and overflow policy (`DropWrite` / `Wait` with deadline).
  - Never spawn unbounded `Task.Run(...)` or unbounded `go func()` per incoming request.
- **Fail Fast at the Edge (`open-api`):**
  - Apply rate limiting (Token Bucket / Sliding Window) and concurrency limiters (`Microsoft.AspNetCore.RateLimiting`) at the ingress/API layer.
  - Reject overload quickly with `429 Too Many Requests` or `503 Service Unavailable` + `Retry-After` rather than queuing indefinitely.
- **Strict End-to-End Deadlines:**
  - Every incoming request carries a bounded deadline (`CancellationToken` in .NET, `context.WithTimeout` in Go) propagated across gRPC (`Deadline`), MySQL (`CommandTimeout`), Kafka, and outbound HTTP webhooks.
- **Stop Retry Storms:**
  - Retries without jitter multiply spike traffic (`2×–5×` amplification). Always use **bounded retries + exponential backoff with full jitter + circuit breakers** (Polly / `Microsoft.Extensions.Http.Resilience` in .NET; bounded retry/breaker in Go).
- **Idempotency Everywhere:**
  - At high RPS, network timeouts and at-least-once Kafka delivery guarantee duplicate requests/events. Require an `idempotency_key` (enforced via `UNIQUE` constraint or conditional upsert) so retries are safe and side-effect-free.

---

## 4. Big-Data Storage & Hot-Row Survival (Millions → Billions of Rows)

Design tables assuming they will hold **100M+ rows** and face extreme contention on popular items:

- **Prevent Hot-Row Lock Contention:**
  - In a flash sale, 100,000 requests updating `UPDATE inventory SET stock = stock - 1 WHERE sku = 'HOT_SKU'` serialize on one InnoDB row lock and cause lock-wait timeouts/deadlocks.
  - **Patterns to solve hot rows:**
    1. **Sharded/Striped Inventory Buckets:** Split 1 hot SKU row into `N` bucket rows (`sku + bucket_id`) and update a random/hashed bucket atomically (`WHERE stock >= @qty`).
    2. **Atomic Cache Pre-Decrement + Async DB Reconciliation** (when Redis is present: atomic Lua/`DECRBY` in Redis + async flush to MySQL).
    3. **Single-Writer Partitioned Queue:** Route mutations for the same entity to a single Kafka partition processed sequentially by one consumer (zero row lock contention).
- **Append-Only Primary Keys:**
  - Use monotonically increasing keys (`BIGINT AUTO_INCREMENT`, Snowflake ID, or UUIDv7) for InnoDB clustered PKs to avoid random B-tree page splits and buffer-pool fragmentation at high insert rates.
- **Partitioning, Retention & Read/Write Split:**
  - For unbounded tables (`orders`, `outbox_messages`, `webhook_deliveries`), plan time-based partitioning (`PARTITION BY RANGE`), TTL archival/pruning of processed outbox/log rows, and read-replica / CQRS separation so heavy read/reporting traffic never stalls OLTP writes.
- **Zero Full-Table Scans & Zero Deep Offsets:**
  - Always stream or chunk large scans (`IAsyncEnumerable<T>` / keyset batches), use bidirectional `(created_at, id)` cursor pagination (`database-performance`), and avoid `SELECT COUNT(*)` on hot endpoints.

---

## 5. Read-Path Scaling & Serialization Efficiency

- **Cache-Aside with Stampede Protection (when caching is used):**
  - **Cache Stampede (Thundering Herd):** When a hot key expires under 200k RPS, thousands of requests hit MySQL simultaneously. Mitigate with single-flight / request coalescing, TTL jitter (`baseTTL + rand(0..15%)`), or stale-while-revalidate.
  - **Cache Penetration:** Briefly cache negative results (`not_found` sentinel with short TTL) or validate input format before querying DB for non-existent IDs.
- **Compact Wire Formats & Zero-Copy/Pooling:**
  - Use binary **Protobuf over gRPC HTTP/2 multiplexing (`h2c`)** between internal services (`open-api` → `order-service` → `webhook-service`) — reuse a single long-lived gRPC `GrpcChannel` / `grpc.ClientConn` (HTTP/2 multiplexes thousands of concurrent RPCs over one TCP connection).
  - Pool buffers on hot paths (`ArrayPool<byte>` / `RecyclableMemoryStream` in .NET, `sync.Pool` in Go) and avoid large JSON/string allocations.

---

## 6. Kubernetes Horizontal Scaling & Zero-Drop Mindset

Design every microservice so Kubernetes can scale pods aggressively (`1 → N → 1`) under sudden load without dropping a single request:

- **100% Stateless Pods:** Store zero local-disk or in-memory session state in `open-api`, `order-service`, or `webhook-service`.
- **Zero-Drop Graceful Shutdown:**
  - On `SIGTERM`: flip `/readyz` to unhealthy, wait briefly (`preStop` hook, e.g., 5s) so kube-proxy/Endpoints removes the pod from Service rotation, finish in-flight HTTP/gRPC requests and commit active Kafka offsets, then exit cleanly within `terminationGracePeriodSeconds`.
- **Separate Liveness vs. Readiness Probes:**
  - `/healthz` (liveness): lightweight process-alive check ONLY — never query MySQL/Kafka in liveness (a transient DB blip would cause Kubernetes to kill-loop every pod at once!).
  - `/readyz` (readiness): checks whether the pod can accept traffic (startup complete, not shutting down).
- **Right Scaling Signals & HPA Math:**
  - Set accurate CPU/memory `requests` (HPA calculates utilization as `actual / request`).
  - Know the difference between **CPU-bound scaling** (HPA on CPU/Memory for `open-api`) and **queue-lag scaling** (Kafka consumer lag via KEDA/custom metrics for `order-service` consumers in production).
  - Always validate scaling behavior with `k6` spike profiles (`loadtest/`) and inspect **P50 / P95 / P99 latency, RPS, error rate, HPA replica timeline, and Kafka consumer lag** — never optimize blind.

---

## Scale Review Checklist (Before Approving TD or PR)

- [ ] **Sync path is thin:** Slow/unreliable work (webhooks, heavy processing) is offloaded asynchronously via Kafka/Redpanda.
- [ ] **Bounded concurrency:** Every queue, worker pool, HTTP/gRPC client, and DB pool has explicit upper bounds and timeouts/deadlines.
- [ ] **No hot-row / hot-partition traps:** Partition keys have high cardinality; high-contention writes avoid serialized single-row locks.
- [ ] **Idempotent & retry-safe:** Duplicate requests/Kafka messages produce identical state; retries use bounded jittered backoff + circuit breakers.
- [ ] **Big-data query safety:** No `OFFSET`, no unbounded `SELECT *`, no `COUNT(*)` on large tables, append-only PKs, and archival/retention noted for append-only logs/outbox tables.
- [ ] **K8s zero-drop ready:** Stateless pods, separated `/healthz` vs. `/readyz`, graceful `SIGTERM` drain, single shared HTTP/2 gRPC channel, and realistic `requests`/`limits` for HPA.
