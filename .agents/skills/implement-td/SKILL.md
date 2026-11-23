---
name: implement-td
description: Implement an approved Technical Design (doc/specs/YYYY-MM-DD-<slug>.md) into complete, production-ready polyglot code across isolated Git worktrees cut from develop. Enforces contract-first, project engineering skills, builds & tests per worktree, and stops at the User Review Gate before merging into develop.
---

# Implement Technical Design (`implement-td`)

Use this skill ONLY after a Technical Design (`doc/specs/YYYY-MM-DD-<slug>.md`) has been reviewed and approved by the user via `write-td` or `write-td-lite`.

## Core Quality Bar

1. **Full, concrete, production-ready implementation — zero placeholders.**
   - NEVER leave `// TODO`, `// FIXME`, `throw new NotImplementedException()`, stubbed methods, empty catch blocks, or hand-waved logic.
   - Write every file completely as a diligent junior engineer preparing a PR for a senior engineer to review line by line.
2. **Brief, crisp comments above complex logic and design patterns.**
   - Above any non-trivial algorithm, concurrency control, transaction boundary, or architectural pattern (e.g., Keyset Cursor Pagination, Outbox Dispatch, Optimistic Concurrency, Idempotency Guard, Exponential Backoff, Repository / Unit of Work), add a short 1–3 line comment stating **what pattern** is used and **why** the block works that way.
   - Do NOT comment trivial getters, obvious assignments, or self-explanatory one-liners.
3. **Never mutate `develop`.**
   - All edits, builds, and tests run inside isolated Git worktrees under `.worktrees/<YYYY-MM-DD>-<slug>` (or per-service worktrees `.worktrees/<YYYY-MM-DD>-<service>`) cut from `develop`.
4. **Strict environment isolation & Guided Cloud Mode.**
   - `staging` and `production` NEVER share MySQL, Redpanda/Kafka, Redis, Secrets, Terraform state (`infra/terraform/envs/staging` vs `infra/terraform/envs/production`), or OIDC credentials.
   - Never run cloud-mutating commands (`az ... create/delete/update`, `terraform apply/destroy`, `kubectl apply/delete/patch` against AKS, or GitHub environment/OIDC mutations) without first showing the exact command, explaining its effect, and getting explicit user confirmation.

## Mandatory Engineering Standards

Before writing service code, load and strictly enforce the applicable standards:

### `.NET 8` Services (`services/open-api`, `services/order-service`)
Explicitly invoke and follow all **7 project engineering skills**:
1. `dotnet-backend-api` — REST/gRPC endpoint design, DTO validation, problem details, `h2c` Kestrel/client configuration, cancellation tokens.
2. `ef-core-skill` — DbContext lifetime, `AsNoTracking()` for reads, explicit transactions, compiled/parameterized queries, migration hygiene.
3. `resource-gc-management` — `IDisposable`/`IAsyncDisposable`, `HttpClientFactory` / gRPC channel pooling, bounded buffers, avoiding allocations/leaks on hot paths.
4. `clean-architecture-ddd` — strict dependency direction (Domain -> Application -> Infrastructure -> Presentation/API), thin controllers/gRPC services, explicit domain invariants.
5. `database-performance` — covering/composite indexes matching the TD, keyset/cursor pagination (no deep `OFFSET`), avoiding N+1 queries, batching, connection pooling.
6. `coding-quality` — null-safety (`<Nullable>enable</Nullable>`), guard clauses, small cohesive functions, deterministic error handling, zero compiler warnings.
7. `high-throughput-scale` — high-RPS & big-data mindset: async ingestion, bounded queues/workers, backpressure/load shedding, hot-row & hot-partition avoidance, idempotency, single shared HTTP/2 gRPC channel, separated `/healthz` vs. `/readyz`, and zero-drop `SIGTERM` drain.

### `Go` Service (`services/webhook-service`)
Enforce `high-throughput-scale` plus idiomatic Go conventions:
- Propagate `context.Context` as the first parameter through all I/O, Kafka, gRPC, and HTTP calls.
- Wrap errors with context using `fmt.Errorf("...: %w", err)`; never swallow errors.
- Configure gRPC cleartext HTTP/2 (`h2c`) with `grpc.WithTransportCredentials(insecure.NewCredentials())`.
- Close response bodies/channels cleanly, bound worker concurrency, handle graceful shutdown on `SIGINT`/`SIGTERM`, and keep `go vet ./...` and `go test -race ./...` completely clean.

## Strict Execution Order

### Step 1: Pre-Flight & TD Verification
- Read `doc/specs/YYYY-MM-DD-<slug>.md` in full.
- Verify `develop` is clean and synced (`git fetch origin && git status --short`). Never write implementation files on `develop`.

### Step 2: Contract First (`proto/`)
If the TD modifies `proto/order/v1/order.proto` or `proto/webhook/v1/webhook.proto`:
1. Create the contract worktree from `develop`:
   ```bash
   git worktree add -b feat/<phase>-<slug>-proto .worktrees/<YYYY-MM-DD>-proto develop
   ```
2. Edit only the `.proto` file(s), regenerate C# (`Grpc.Tools` / `dotnet build`) and Go (`protoc` / `make proto`) stubs, and verify both compile cleanly. **Never hand-edit generated stubs.**
3. Commit the contract changes on the contract branch so downstream service worktrees branch from the settled contract.

### Step 3: Per-Subagent Git Worktrees (Parallel Service Implementation)
Follow `parallel-worktree-agents` whenever spanning 2+ independent services/areas:
- For multi-service changes, cut one isolated Git worktree per independent service (from `develop`, or from the settled contract branch if `proto/` changed):
  - `.worktrees/<YYYY-MM-DD>-open-api` (`feat/<phase>-<slug>-open-api`) — owns `services/open-api/**`
  - `.worktrees/<YYYY-MM-DD>-order-service` (`feat/<phase>-<slug>-order-service`) — owns `services/order-service/**`
  - `.worktrees/<YYYY-MM-DD>-webhook-service` (`feat/<phase>-<slug>-webhook-service`) — owns `services/webhook-service/**`
  - `.worktrees/<YYYY-MM-DD>-infra` (`feat/<phase>-<slug>-infra`) — owns `deploy/k8s/**`, `deploy/argocd/**`, `infra/terraform/envs/{staging,production}/**`, `.github/workflows/**`
- For single-service changes, a single worktree `.worktrees/<YYYY-MM-DD>-<slug>` on `feat/<phase>-<slug>` is sufficient.
- **Disjoint file ownership:** Service subagents MUST NOT modify `proto/` or another service's directory. If a contract mismatch is discovered, stop and report back immediately.

### Step 4: Build & Verify in Every Worktree
Run all applicable checks inside each worktree before reporting:
- `.NET 8`: `dotnet build --no-restore` (or `dotnet build`) and `dotnet test`
- `Go`: `go vet ./...` and `go test -race ./...`
- `K8s`: `kubectl kustomize deploy/k8s/overlays/staging >/dev/null` and `kubectl kustomize deploy/k8s/overlays/production >/dev/null`
- `Terraform`: `terraform -chdir=infra/terraform/envs/staging fmt -check && terraform -chdir=infra/terraform/envs/staging validate` (and same for `production`)
- Confirm `git status --short` contains zero secrets, `.env`, kubeconfigs, or `*.tfstate*`.

### Step 5: User Review Gate (MANDATORY STOP)
**STOP before merging anything into `develop`.** Present to the user:
1. Summary of changes per worktree with clickable file links.
2. Key design patterns applied (and where brief explanatory comments were placed).
3. Exact build/test/validation command results (evidence before assertions).
4. Confirmation that `staging` and `production` isolation and Azure Free Trial caps are preserved.
5. Explicit prompt asking whether to:
   - Run `write-tests` to expand coverage to 80%–100%,
   - Request modifications, or
   - Merge into `develop`.

### Step 6: Handling Review Feedback & Merging to `develop`
- **If the user requests modifications:** Apply every requested change inside the existing worktree(s), re-run build & test verification, and present the updated diff at the Review Gate.
- **When the user explicitly approves merging into `develop`:**
  1. Fetch latest `develop` and rebase/merge each worktree branch against `develop` **inside the worktree**.
  2. If any merge conflict occurs, resolve it carefully inside the worktree, re-run full builds and tests, and verify zero regressions.
  3. Merge the verified branch(es) into `develop`, verify clean status, and clean up merged worktrees (`git worktree remove <path>` — never force-remove a dirty worktree).
