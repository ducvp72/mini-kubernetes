# mini-kubernetes — Polyglot Microservices & AKS Lab

**This file is the single source of truth for Antigravity agents working in `ducvp72/mini-kubernetes`.**

**Hard Rules** are always followed, whatever a plan, skill, or step says. The
**Workflow** is the default way to get a change from spec to `develop` (and on to `main`):
use it when asked what to do next. Deviate when there is a reason, and say so.

## Project Facts

| Area                   | Details                                                                                                                                                                                                 |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Repo                   | Single polyglot monorepo `ducvp72/mini-kubernetes` deploying 3 independent microservices                                                                                                                |
| Branches               | `develop` (clean integration source of truth -> `staging`), `main` (release -> `production`, promoted ONLY after staging verification + user approval), feature branches `feat/<phase>-<slug>`          |
| Worktrees              | Per-subagent isolated worktrees under `.worktrees/<YYYY-MM-DD>-<slug>` (gitignored): contract-first (`feat/<phase>-proto`), then 3 parallel service worktrees (`open-api`, `order-service`, `webhook-service`) |
| Specs & Plans          | `doc/specs/` and `doc/plans/` (local-only, gitignored via `/doc/`)                                                                                                                                      |
| Project Skills         | `.agents/skills/` — 12 project-scoped skills covering technical design, parallel git-worktree orchestration, implementation, testing (80%–100% coverage), .NET/Go/DB engineering standards, and high-throughput / big-data scale mindset |
| Contract paths         | `proto/order/v1/order.proto` AND `proto/webhook/v1/webhook.proto` — always change first on `feat/<phase>-proto`; generate C# (`Grpc.Tools` / MSBuild) and Go (`protoc`) stubs                           |
| Polyglot gRPC Flow     | `Client / k6` -> REST `POST /orders` -> `open-api` (.NET 8) -> gRPC `OrderService.CreateOrder` -> `order-service` (.NET 8) -> MySQL (`PENDING`) + Kafka/Redpanda (`order.created`) -> fast `202 Accepted`; `order-service` background consumer processes `order.created`, updates MySQL (`CONFIRMED`) -> gRPC `WebhookService.SendOrderCallback` -> `webhook-service` (Go gRPC server) -> HTTP POST callback to client |
| Services               | `services/open-api` (.NET 8 REST), `services/order-service` (.NET 8 gRPC server + MySQL + Kafka producer & background consumer + `WebhookService` gRPC client), `services/webhook-service` (Go gRPC server `WebhookService.SendOrderCallback` + HTTP POST callback) |
| Data Layer (Minimal)   | Phase 1 uses **MySQL + Kafka (Redpanda)** only (matching `architect-td.md` & `azure-lab-hand-on-practice.xlsx`). Redis is not added unless requested in a later phase, where it must also be isolated per environment |
| Infra & Deploy         | `deploy/k8s/` (Kustomize `base` + `staging`/`production` overlays), `deploy/argocd/`, `infra/terraform/` (`modules/` + `envs/staging/` + `envs/production/`), `.github/workflows/`, `loadtest/`         |
| Local build & test     | `make proto`, `make build`, `make test`, `dotnet test`, `go test ./...`, `docker compose up -d`                                                                                                         |
| Azure Free Trial Caps  | 4 vCPU regional cap: `infra/terraform/modules/aks` parameterizes `node_count` (`1 × Standard_B2s` per cluster when running both `staging` & `production` simultaneously = 4 vCPU total; `2 × Standard_B2s` when running one environment at a time for multi-node scheduling & HPA stress testing); strict environment isolation (`rg-mini-k8s-staging`, `rg-mini-k8s-prod`, separate Azure MySQL Flexible Servers, separate Redpanda brokers, separate Secrets); Terraform monitoring OFF by default (daily cap when enabled in Phase 5); GitHub Actions OIDC (`id-token: write`, separate federated credentials per environment, verify immutable `sub` claim format for post-2026-07-15 repos) |

## Hard Rules

1. **Never implement directly on `main`, and keep `develop` clean.** `develop` is the clean source of truth. Cut a feature branch (`feat/<phase>-<slug>`) and isolated git worktree (`.worktrees/<YYYY-MM-DD>-<slug>`) from `develop`. Allowed on the main checkout: reading, syncing `develop`, managing worktrees, final user-approved merge/push, and cleanup.
2. **Contract first.** Edit `proto/order/v1/order.proto` and `proto/webhook/v1/webhook.proto` in a dedicated contract worktree (`feat/<phase>-proto`), regenerate C# and Go stubs, verify compilation, and merge into `develop` **before** cutting service worktrees or touching any service code. Never hand-edit generated stubs.
3. **Two-Mode Boundary:**
   - **Fast App Mode** (autonomous): Agents may scaffold, edit, build, and test local application code (`proto/`, `services/`, `Dockerfile`s, `docker-compose.yml`, `loadtest/`, local Kustomize/Terraform validation) autonomously, using parallel subagents in independent git worktrees across independent services.
   - **Guided Cloud Mode** (explicit confirmation required): Agents NEVER execute cloud-mutating commands (`az ... create/delete/update`, `terraform apply/destroy`, `kubectl apply/delete/patch` against AKS, or GitHub environment/OIDC mutations) without first showing the exact command, explaining what it does and teaches, and getting explicit user confirmation.
4. **Never commit secrets or state.** Never stage or commit `.env`, kubeconfigs, certificates, API keys, `terraform.tfvars`, or `*.tfstate*`.
5. **Never violate Azure Free Trial limits.** Stay within the 4 vCPU cap (`1 × Standard_B2s` per cluster when running both `staging` and `production` simultaneously, or `2 × Standard_B2s` when running a single environment for multi-node/HPA testing), never enable uncapped Log Analytics/Container Insights, and always remind/run teardown at the end of a cloud session.
6. **Keep business logic minimal.** This is a Kubernetes, CI/CD, GitOps, and throughput-scaling lab — avoid over-engineering domain features, auth layers, complex schemas, or unrequested data stores (stick to MySQL + Kafka/Redpanda in Phase 1).
7. **Workflow authority vs. skills.** `AGENTS.md` decides workflow steps, branch names, save locations (`doc/specs/`, `doc/plans/`), and guardrails. Project-scoped skills in `.agents/skills/` and superpowers skills (`brainstorming`, `writing-plans`, `test-driven-development`, `verification-before-completion`) supply step content and engineering standards. Where a skill says otherwise, follow this file and note what was adapted.
8. **Strict Environment Isolation & Staging-Verified Promotion Gate.**
   - `staging` (`develop`) and `production` (`main`) **NEVER** share stateful resources: use separate Resource Groups (`rg-mini-k8s-staging`, `rg-mini-k8s-prod`), separate Azure MySQL Flexible Servers, separate Redpanda/Kafka brokers (and separate Redis if added later), separate Secrets, separate Terraform stacks (`infra/terraform/envs/staging/` & `infra/terraform/envs/production/` sharing `infra/terraform/modules/`), separate GitHub Environments & Entra ID OIDC federated credentials, and separate Argo CD Applications.
   - **Strict Promotion Gate:** Merging `develop` -> `main` (promoting to `production`) is decided **ONLY** after `develop` is deployed to `staging`, verified healthy (smoke + load checks), and explicitly approved by the user.

---

## Project-Scoped Skill Suite (`.agents/skills/`)

Invoke these project-scoped skills whenever working on design, implementation, testing, or code review:

| Category | Skill | When to Invoke |
| :--- | :--- | :--- |
| **Workflow / Design / Test** | `write-td` | Authoring a full technical design spec (architecture-only, **no code**, full DB schema, primary/foreign keys, and indexes) |
| **Workflow / Design / Test** | `write-td-lite` | Authoring a concise technical design for smaller, well-scoped features or phase increments |
| **Workflow / Design / Test** | `implement-td` | Implementing a technical design with complete, non-vague code and brief pattern/complexity comments |
| **Workflow / Design / Test** | `parallel-worktree-agents` | Splitting `develop` into isolated per-subagent Git worktrees, enforcing disjoint ownership, User Review Gate, cross-worktree conflict resolution against `develop`, and safe cleanup |
| **Workflow / Design / Test** | `write-tests` | Writing unit, integration, and contract tests targeting **80%–100% polyglot coverage** (.NET & Go) |
| **Engineering Standards** | `dotnet-backend-api` | Building or reviewing `services/open-api` or `services/order-service` (.NET 8 REST/gRPC API structure, middleware, DI, health checks) |
| **Engineering Standards** | `ef-core-skill` | Writing or optimizing Entity Framework Core models, DbContexts, migrations, and MySQL queries in `order-service` |
| **Engineering Standards** | `resource-gc-management` | Managing allocations, pooling, `IDisposable`/`IAsyncDisposable`, `CancellationToken`, and GC pressure under promotion-burst load |
| **Engineering Standards** | `clean-architecture-ddd` | Keeping service boundaries, domain models, and infrastructure adapters cleanly separated without over-engineering |
| **Engineering Standards** | `database-performance` | Designing MySQL schemas, keys, indexes, connection pooling, and query plans for high-throughput order ingestion |
| **Engineering Standards** | `coding-quality` | Enforcing clean code, readability, error handling, observability hygiene, and review readiness across C# and Go |
| **Engineering Standards** | `high-throughput-scale` | Applying high-RPS (10k–200k RPS) & big-data scale mindset: capacity math, async shock absorption, backpressure/load shedding, hot-row/partition safety, cache-stampede protection, and K8s zero-drop HPA scaling |

---

## Workflow

### 0. Sync (`develop`)

```bash
git fetch origin && git checkout develop && git pull --ff-only origin develop
```

Resolve any sync error before starting new work.

### 1. Scope & Research — read-only

Read the relevant paths on `develop` without modifying files. Confirm:
- Phase / goal and exact touched paths (`proto/`, `services/*`, `deploy/`, `infra/`, `.github/`)
- Mode boundary: Fast App Mode vs. Guided Cloud Mode steps
- Free branch name(s) `feat/<phase>-<slug>`:

```bash
BRANCH=feat/<phase>-<slug>
git show-ref --verify -q "refs/heads/$BRANCH" && echo "TAKEN local"
git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null && echo "TAKEN remote"
```

**Show one concise summary and wait for confirmation** before creating a branch, worktree, or cloud resource.

### 2. Setup Per-Subagent Worktrees & Baseline Build

Never edit directly on `develop` or `main`. Create isolated worktrees cut from `develop`:

- **Single-track or Contract-first worktree** (e.g., `feat/<phase>-proto`):
  ```bash
  WS=".worktrees/$(date +%F)-proto"
  git worktree add -b "feat/<phase>-proto" "$WS" develop
  ```
- **Parallel service worktrees** (cut from `develop` **after** `feat/<phase>-proto` merges into `develop`):
  ```bash
  DATE=$(date +%F)
  git worktree add -b "feat/<phase>-open-api"        ".worktrees/${DATE}-open-api"        develop
  git worktree add -b "feat/<phase>-order-service"   ".worktrees/${DATE}-order-service"   develop
  git worktree add -b "feat/<phase>-webhook-service" ".worktrees/${DATE}-webhook-service" develop
  ```

Run a baseline build/test so any pre-existing breakage is caught before planning:

```bash
cd "$WS" && make build && make test
```

### 3. Spec & Plan

Use `write-td` (or `write-td-lite`) for technical designs. Save specs under `doc/specs/<YYYY-MM-DD>-<slug>.md` and plans under `doc/plans/<YYYY-MM-DD>-<slug>.md` (both are gitignored via `/doc/`). Start each plan with:

```markdown
## Workspace
- Branch(es): `feat/<phase>-proto`, `feat/<phase>-open-api`, `feat/<phase>-order-service`, `feat/<phase>-webhook-service`
- Worktree(s): `.worktrees/<YYYY-MM-DD>-<slug>`
- Base: `develop @ <git rev-parse --short HEAD>`
- Mode: Fast App Mode / Guided Cloud Mode (list any gated cloud commands)
```

Keep plans concise and concrete: target paths, contract changes, verification commands, applicable `.agents/skills/`, and explicit Guided Cloud Mode checkpoints.

### 4. Implement

Work inside isolated worktrees in strict dependency order:

1. **Contract first (`feat/<phase>-proto`):** Edit `proto/order/v1/order.proto` and `proto/webhook/v1/webhook.proto`, regenerate C# and Go stubs (`make proto`), verify compilation, commit, and merge `feat/<phase>-proto` into `develop` before launching service subagents.
2. **Services in parallel (3 independent git worktrees):** Cut `.worktrees/<YYYY-MM-DD>-open-api`, `.worktrees/<YYYY-MM-DD>-order-service`, and `.worktrees/<YYYY-MM-DD>-webhook-service` from updated `develop`. Fan out 3 parallel subagents — one per worktree — following `implement-td`, `write-tests`, and the relevant engineering skills. Service agents must not alter `proto/` — if a contract is wrong, stop and report.
3. **Manifests, IaC & CI:** Update `deploy/k8s/`, `deploy/argocd/`, `infra/terraform/modules/` & `envs/{staging,production}/`, `.github/workflows/`, or `loadtest/` as scoped.
4. **Cloud mutations (Guided Cloud Mode):** Present exact `az`, `terraform apply`, or `kubectl apply` commands with a short explanation and wait for user approval before running.

### 5. Verify & Mandatory User Review Gate

Run all applicable checks inside each worktree before presenting for review — evidence before assertions:

```bash
make test                                              # dotnet test + go test ./... (target 80%-100% coverage)
docker compose up -d --build && ./scripts/smoke-test.sh # local end-to-end smoke test (when app/compose changed)
kubectl kustomize deploy/k8s/overlays/staging >/dev/null
kubectl kustomize deploy/k8s/overlays/production >/dev/null
kubectl apply --dry-run=client -k deploy/k8s/overlays/staging   # when cluster context is available
terraform -chdir=infra/terraform/envs/staging fmt -check && terraform -chdir=infra/terraform/envs/staging validate
terraform -chdir=infra/terraform/envs/production fmt -check && terraform -chdir=infra/terraform/envs/production validate
```

**Mandatory User Review Gate:**
1. When all worktrees finish and pass their verification checks, present a clear summary and diffs from each worktree for human review.
2. If the user requests changes, apply edits inside the relevant worktree(s) and re-run verification.
3. **Only after explicit user approval**, proceed to Step 6 to merge sequentially into `develop`.

### 6. Merge, Commit, Push & Staging-Verified Promotion

Use conventional commits (`feat(phase-N): ...`, `fix(...): ...`, `chore(...): ...`):

```bash
git status --short                                     # verify no secrets, .env, terraform.tfvars, or *.tfstate*
git add -A && git commit -m "feat(<scope>): <summary>"
```

- **Sequential Merge into `develop` (after User Review Gate approval):**
  Merge each approved worktree branch (`feat/<phase>-open-api`, `feat/<phase>-order-service`, `feat/<phase>-webhook-service`) sequentially into `develop`. Resolve any merge conflicts against `develop`, re-run the full build and test suite (`make build && make test`), and then push `develop` (`git push origin develop`).
- **Staging (`develop` -> `staging` environment):** Pushing `develop` triggers GitHub Actions OIDC (`staging` environment) to build/push images and update the staging overlay; Argo CD syncs `staging`.
- **Production (`develop` -> `main` -> `production` environment — Strict Promotion Gate):**
  Merging `develop` -> `main` is decided **ONLY** after `develop` is deployed to `staging`, verified healthy (smoke + load checks), and explicitly approved by the user. After review and merge of the `develop` -> `main` PR, GitHub Actions promotes via OIDC (`production` environment) and Argo CD syncs `production`.

### 7. Clean Up & Azure Teardown

Remove merged worktrees and branches (never force-remove a dirty worktree):

```bash
git worktree remove "$WS"
git branch -d "$BRANCH"
git push origin --delete "$BRANCH"   # if branch was pushed
```

**End-of-session Azure teardown checklist (Guided Cloud Mode — confirm before running):**
- Terraform-managed lab (destroy each active stack):
  - `terraform -chdir=infra/terraform/envs/staging destroy`
  - `terraform -chdir=infra/terraform/envs/production destroy`
- CLI-managed lab:
  - `az group delete --name rg-mini-k8s-staging --yes --no-wait`
  - `az group delete --name rg-mini-k8s-prod --yes --no-wait`
- Verify no stray billable resources remain: `az resource list -o table`

---

## Execution Method

**Foreground** = normal blocking command call. **Background** = async command (system notifies on exit — never poll). **Parallel Subagents** = `invoke_subagent` fan-out for independent tasks that need judgment, never for a single shell command.

Anything that creates/mutates cloud resources, commits, merges, pushes, or deletes stays **Foreground** (and **Guided Cloud Mode** requires confirmation first).

| Step | Work                                                              | Method                                                                                                  |
| ---- | ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| 0    | `git fetch` / `pull --ff-only` on `develop`                       | Foreground                                                                                              |
| 1    | Branch-name check, targeted file reads                            | Foreground (or `research` subagent for broad codebase surveys)                                          |
| 2    | `git worktree add` (contract worktree, then 3 service worktrees)  | Foreground                                                                                              |
| 2    | Baseline `make build && make test`                                | Background while drafting Step 3 spec/plan; check green before finalizing plan                          |
| 3    | Spec & plan (`doc/specs/`, `doc/plans/`)                          | Foreground                                                                                              |
| 4    | `proto/` edits (`order.proto` & `webhook.proto`), stub gen & merge | Foreground (contract worktree lands on `develop` first)                                                 |
| 4    | Independent services (`open-api`, `order-service`, `webhook-service`) | **Parallel Subagents** — 1 subagent per dedicated git worktree with disjoint file ownership; serialize tests sharing ports/DB |
| 4    | Cloud mutations (`az`, `terraform apply`, `kubectl apply` on AKS) | Foreground + **Guided Cloud Mode** confirmation                                                         |
| 5    | `make test`, `kubectl kustomize`, `terraform validate` + Review Gate | Foreground (chained) or Background for long Docker/load-test runs; pause for Mandatory User Review Gate |
| 6    | Sequential merge into `develop`, re-test, push, staging-verified PR to `main` | Foreground (requires explicit user approval at Review Gate and Promotion Gate)                          |
| 7    | Worktree/branch cleanup & Azure teardown                          | Foreground (teardown requires Guided Cloud Mode confirmation)                                           |

---

## Troubleshooting Playbook

**4 vCPU quota error on AKS (`Operation could not be completed as it results in exceeding approved standardBSFamily Cores quota`)**
- Azure Free Trial caps regions at 4 vCPUs total. Parameterize `node_count` in `infra/terraform/modules/aks`: use `1 × Standard_B2s` per cluster when running both `staging` and `production` clusters simultaneously (2 × 2 = 4 vCPU total), or `2 × Standard_B2s` when running a single environment at a time for multi-node scheduling & HPA stress testing. Never exceed 4 vCPUs total. Check existing VMs/clusters (`az vm list -o table`, `az aks list -o table`) and delete leftovers or switch region.

**GitHub Actions OIDC `sub` claim mismatch (post-2026-07-15 repos)**
- Repositories created on/after 2026-07-15 may emit immutable repository/owner IDs or environment-scoped `sub` claims. Inspect the exact `sub` claim in the failing Entra ID / OIDC error log, ensure `permissions: { id-token: write, contents: read }`, and configure **separate** Azure Federated Credentials per GitHub Environment (`staging` vs `production`) matching the exact `sub` string.

**Terraform Log Analytics / Container Insights cost trap**
- Uncapped Azure Monitor / Log Analytics workspaces can burn Free Trial credits quickly. Keep `enable_monitoring = false` in `infra/terraform/modules/aks` by default; when enabling in Phase 5, always set `daily_quota_gb` (e.g., `0.5`) and `retention_in_days = 30`, and disable/destroy after testing.

**Kafka / Redpanda `ADVERTISED_LISTENERS` mismatch (Docker Compose vs. Kubernetes)**
- Clients connect to the bootstrap address, then reconnect to the advertised broker address. In Docker Compose, expose two listeners (`INTERNAL://redpanda:9092` for containers, `EXTERNAL://localhost:19092` for host tools). In Kubernetes, advertise the in-cluster headless/ClusterIP DNS name (`redpanda.<namespace>.svc.cluster.local:9092`), never `localhost`.

**gRPC HTTP/2 cleartext (`h2c`) failures in Kubernetes**
- Plaintext gRPC between `open-api` -> `order-service` (`OrderService`) and `order-service` -> `webhook-service` (`WebhookService`) requires HTTP/2 without TLS (`h2c`). In .NET 8 (`order-service`), configure Kestrel explicitly with `HttpProtocols.Http2` on the gRPC port; in .NET gRPC clients (`open-api` and `order-service`), enable unencrypted HTTP/2 (`AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)` if needed); in Go (`webhook-service`), serve plaintext HTTP/2 via `grpc.NewServer()` over TCP. In K8s `Service` manifests, set `appProtocol: kubernetes.io/h2c` (or `h2c`) on the gRPC port so proxies/probes handle HTTP/2 properly.
