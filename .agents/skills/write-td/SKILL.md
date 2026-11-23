---
name: write-td
description: Author a complete architectural Technical Design (TD) saved to doc/specs/YYYY-MM-DD-<slug>.md for multi-service, contract, database schema, or cross-environment changes in mini-kubernetes. Stops for explicit human approval before implementation.
---

# Write Technical Design (`write-td`)

Use this skill to design any non-trivial change in `ducvp72/mini-kubernetes` before writing implementation code. It produces a strictly architectural Technical Design (TD) stored locally at `doc/specs/YYYY-MM-DD-<slug>.md` (gitignored via `/doc/`).

## When to Use

Use `write-td` (rather than `write-td-lite`) whenever a change involves any of:
- Any edit to `proto/order/v1/order.proto` or `proto/webhook/v1/webhook.proto`
- Changes spanning more than one service (`services/open-api`, `services/order-service`, `services/webhook-service`)
- Any new database table, schema alteration, constraint, or index
- New or modified Kafka/Redpanda topics, consumer groups, or message envelopes
- Cross-environment infrastructure, Kubernetes manifests (`deploy/k8s/`), Argo CD (`deploy/argocd/`), Terraform (`infra/terraform/envs/{staging,production}`), or CI/CD workflows (`.github/workflows/`)

## Hard Rules

1. **NO implementation code in the TD.** Keep the document strictly architectural. Do not include C# or Go implementation bodies, controller/handler code, or full YAML/Dockerfile dumps. Allowed artifacts inside the TD:
   - Protobuf message & RPC contract shapes
   - SQL DDL / schema tables and index definitions
   - Mermaid diagrams (`sequenceDiagram`, `flowchart`, `erDiagram`)
   - Structured tables, state transitions, and test matrices
2. **Never mutate `develop`.** Perform all research read-only on `develop`. Implementation happens later in `.worktrees/<YYYY-MM-DD>-<slug>` cut from `develop`.
3. **Database Schema section is MANDATORY whenever the DB is touched.** You MUST specify exact table names, column names, SQL data types, nullability (`NOT NULL` / `NULL`), primary keys, foreign keys, `UNIQUE` / `CHECK` constraints, and every index (with explicit cursor-pagination and hot-path query rationale).
4. **Strict environment isolation.** `staging` and `production` MUST NEVER share stateful resources: MySQL instances/databases, Redpanda/Kafka clusters/topics, Redis instances, Kubernetes Secrets/Key Vaults, Terraform state files, or GitHub Actions OIDC federated credentials.
5. **Respect Azure Free Trial caps & Two-Mode Boundary.** Stay within `2 × Standard_B2s` (4 vCPU total), keep monitoring OFF by default (or capped via `daily_quota_gb`), and mark every cloud-mutating step as **Guided Cloud Mode** (requires explicit user confirmation before execution).
6. **Keep business logic minimal.** This is a Kubernetes, CI/CD, GitOps, and throughput-scaling lab — avoid over-engineering domain rules, complex auth layers, or unnecessary abstractions.

## Monorepo Map

| Path | Role |
| --- | --- |
| `proto/order/v1/order.proto` | Order gRPC contract (source of truth; changes first) |
| `proto/webhook/v1/webhook.proto` | Webhook gRPC contract (source of truth; changes first) |
| `services/open-api` | .NET 8 REST gateway calling `order-service` over gRPC (`h2c`) |
| `services/order-service` | .NET 8 gRPC service + MySQL + Kafka/Redpanda producer & background consumer + `WebhookService` gRPC client |
| `services/webhook-service` | Go gRPC server (`WebhookService.SendOrderCallback`) + outbound HTTP callback dispatcher |
| `deploy/k8s/` | Kustomize `base` + `overlays/staging` + `overlays/production` |
| `deploy/argocd/` | Argo CD Application / AppProject GitOps definitions |
| `infra/terraform/envs/staging` | Isolated Terraform root & state for `staging` |
| `infra/terraform/envs/production` | Isolated Terraform root & state for `production` |
| `.github/workflows/` | CI/CD pipelines with per-environment OIDC |

## Step-by-Step Workflow

1. **Read-Only Research on `develop`**
   - Verify current `develop` commit (`git rev-parse --short develop`) without modifying files.
   - Read existing `proto/`, affected services, `deploy/k8s/`, `infra/terraform/envs/{staging,production}`, and `.github/workflows/` to understand current contracts, schemas, and conventions.
   - Check that the planned feature branch name `feat/<phase>-<slug>` is free locally and on `origin`.
2. **Draft the TD at `doc/specs/YYYY-MM-DD-<slug>.md`**
   - Fill every required section below completely and concretely. Omit a section only if genuinely N/A and explicitly state `N/A — <reason>` (never omit **Database Schema & Indexes** if any DB read/write/schema change is involved).
3. **Self-Check Before Presenting**
   - Zero C#/Go implementation code snippets?
   - Every touched table has exact SQL types, nullability, PK/FK/constraints, and indexed hot paths?
   - `staging` vs `production` stateful isolation explicitly verified?
   - Test Plan covers happy path, validation, null-safety, transaction rollback/atomicity, concurrency, and cursor pagination (where applicable)?
4. **Stop for Human Review & Explicit Approval**
   - Present a concise summary and a link to `doc/specs/YYYY-MM-DD-<slug>.md`.
   - Highlight key architectural decisions, trade-offs, and any Guided Cloud Mode gates.
   - **STOP HERE.** Do NOT create worktrees or invoke `implement-td` until the user explicitly approves the TD.

## Required TD Template (`doc/specs/YYYY-MM-DD-<slug>.md`)

```markdown
# TD: <Title>

- **Date:** YYYY-MM-DD
- **Slug:** `<slug>`
- **Base Branch:** `develop @ <short-sha>`
- **Planned Branch / Worktree:** `feat/<phase>-<slug>` / `.worktrees/YYYY-MM-DD-<slug>`
- **Mode Boundary:** Fast App Mode (local code/tests/manifests) | Guided Cloud Mode (list any gated `az` / `terraform apply` / `kubectl apply` steps)

## 1. Overview & Minimal Scope
- **Goal:** 2–3 sentences describing the capability and why it is needed for the lab.
- **In Scope:** Bullet list of exact paths/components touched.
- **Out of Scope:** Explicit non-goals to keep business logic minimal.

## 2. Protobuf gRPC Contracts
- Affected files: `proto/order/v1/order.proto`, `proto/webhook/v1/webhook.proto` (or `N/A — no contract change`).
- Define exact RPC signatures, request/response message fields, field numbers, enums, pagination cursor fields, and error/status-code mappings.
- Note backward-compatibility rules and plaintext HTTP/2 (`h2c`) transport expectations.

## 3. Architecture & Service Flows
- Component interaction overview across `open-api`, `order-service`, `webhook-service`, MySQL, and Redpanda/Kafka.
- Include at least one Mermaid `sequenceDiagram` or `flowchart LR` showing request path, async event flow, and failure/retry paths.
- Specify Kafka/Redpanda topic names, partition keys, delivery semantics, and consumer group behavior (if applicable).

## 4. Database Schema & Indexes (MANDATORY when DB is touched)
For every new or modified table:
- **Table Name & Engine:** e.g. `orders` (InnoDB, `utf8mb4_0900_ai_ci`)
- **Columns Table:**

| Column | SQL Type | Nullable | Default | Constraints / Notes |
| --- | --- | --- | --- | --- |
| `id` | `BIGINT UNSIGNED` | `NOT NULL` | `AUTO_INCREMENT` | `PRIMARY KEY` |

- **Keys & Constraints:** Primary key, foreign keys (with `ON DELETE` / `ON UPDATE` actions), `UNIQUE` keys, `CHECK` constraints.
- **Indexes & Query Rationale:** Exact index names and column order (`INDEX idx_... (col_a, col_b)`), explaining:
  - Hot-path lookup queries served by each index
  - Bidirectional cursor pagination (`next` / `prev`) sort order and deterministic tie-breaker columns
  - Write amplification / cardinality trade-offs
- **Migration Strategy:** Forward-only EF Core migration notes and zero-downtime compatibility.

## 5. Per-Env Isolation & Cloud / K8s / HPA Impact
- **Environment Isolation Matrix:** Confirm `staging` and `production` use separate:
  - MySQL server/database & credentials
  - Redpanda/Kafka namespace/cluster & topics
  - Redis (if used) & Kubernetes Secrets
  - Terraform state (`infra/terraform/envs/staging` vs `infra/terraform/envs/production`)
  - GitHub Actions OIDC federated credentials (`staging` vs `production` environments)
- **Kubernetes & HPA Impact:** Changes in `deploy/k8s/`, `deploy/argocd/`, resource requests/limits, `appProtocol: kubernetes.io/h2c`, probes, and HPA min/max replicas.
- **Azure Free Trial Guardrails:** Confirm total cluster footprint stays within `2 × Standard_B2s` (4 vCPU cap), monitoring remains disabled by default (or capped via `daily_quota_gb`), and list any Guided Cloud Mode checkpoints.

## 6. Cross-Cutting Concerns & High-Throughput Scale (`high-throughput-scale`)
- **Capacity & Bottleneck Estimate:** Expected burst RPS, sync vs. async boundary, Kafka partition key cardinality (no hot partitions), and storage growth/retention plan for large tables.
- **Backpressure & Load Shedding:** Bounded queues/pools, edge rate limiting (`429`/`503`), end-to-end deadlines, and jittered retries/circuit breakers.
- **Atomicity & Transactions:** Exact transaction boundaries, rollback behavior, hot-row contention mitigation, and outbox/event consistency.
- **Concurrency & Idempotency:** Optimistic concurrency tokens, idempotency keys, duplicate event handling, and race-condition prevention.
- **Observability, Zero-Drop Shutdown & Error Handling:** Structured logging fields, correlation IDs, separated `/healthz` (liveness) vs. `/readyz` (readiness), graceful `SIGTERM` drain, and gRPC/HTTP status mapping.

## 7. Test Plan
Directly executable by `write-tests` (target **80%–100% coverage**):
- **Unit Tests:** `.NET 8` (`open-api`, `order-service`) and `Go` (`webhook-service`) scenarios — happy path, validation, null-safety, error mapping.
- **Integration Tests:** MySQL + Redpanda/Kafka tests — persistence, index/query behavior, gRPC `h2c` round-trip.
- **Atomicity & Rollback Tests:** Specific fault-injection cases forcing failure on the final write and asserting zero partial rows.
- **Concurrency & Race Tests:** Parallel writers/consumers, idempotent retries, `go test -race ./...`.
- **Pagination Tests (if applicable):** Bidirectional cursor (`next` & `prev`), boundaries, empty sets, invalid cursor rejection.
- **Smoke / Load Checks (optional):** `docker compose` smoke flow or `loadtest/` k6 profile.
```
