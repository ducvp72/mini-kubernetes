---
name: write-td-lite
description: Author a short architectural note (no implementation code) at doc/specs/YYYY-MM-DD-<slug>.md for single-service tweaks, bug fixes, or single-manifest/HPA adjustments in mini-kubernetes. Escalates to write-td when scope crosses contracts, schemas, services, or environments.
---

# Write Technical Design — Lite (`write-td-lite`)

Use this skill for small, localized changes in `ducvp72/mini-kubernetes` that still deserve a crisp written design checkpoint before coding. The note is saved to `doc/specs/YYYY-MM-DD-<slug>.md` (gitignored via `/doc/`).

## Hard Rules

1. **NO implementation code.** Keep the note strictly architectural/design-level (no C#, Go, or full YAML code dumps).
2. **Never mutate `develop`.** Research read-only on `develop`; implementation runs later in `.worktrees/<YYYY-MM-DD>-<slug>` cut from `develop`.
3. **Strict environment isolation.** Never introduce shared stateful resources between `staging` and `production` (MySQL, Redpanda/Kafka, Redis, Secrets, Terraform state, OIDC credentials).
4. **Escalate immediately if scope grows.** Check the Escalation Table below before writing; if ANY escalation condition is true, stop and switch to `write-td`.

## Escalation Table (`write-td-lite` vs `write-td`)

| Change Characteristic | Stay in `write-td-lite` | Escalate to `write-td` |
| --- | --- | --- |
| Protobuf contracts (`proto/order/v1/order.proto`, `proto/webhook/v1/webhook.proto`) | Untouched | Any message, field, enum, or RPC change |
| Services touched (`open-api`, `order-service`, `webhook-service`) | Exactly 1 service | More than 1 service |
| Database schema (`MySQL` / EF Core) | No schema or index change | Any new table, column/type change, constraint, or index |
| Messaging (`Redpanda/Kafka`) | No topic or schema change | New topic, partition key change, or consumer group redesign |
| Infrastructure & GitOps (`deploy/k8s/`, `deploy/argocd/`, `infra/terraform/envs/{staging,production}`, `.github/workflows/`) | Single manifest tweak (e.g., HPA threshold, probe timeout, resource limit in one overlay) | Cross-env infra, Terraform module/state changes, Argo CD structure, or OIDC/CI pipeline changes |
| Stateful isolation or Free Trial cap risk | No impact on isolation or 4 vCPU cap | Any impact on `staging`/`production` isolation, 4 vCPU cap, or monitoring cost |

## Workflow

1. **Read-Only Research on `develop`**
   - Inspect the target file(s) on `develop` and confirm the escalation table passes.
   - Verify the planned branch `feat/<phase>-<slug>` is unused locally and on `origin`.
2. **Write the Lite Note at `doc/specs/YYYY-MM-DD-<slug>.md`**
   - Keep it under ~80 lines using the compact template below.
3. **Stop for Quick User Ack**
   - Share a 3–5 bullet summary and link to `doc/specs/YYYY-MM-DD-<slug>.md`.
   - Wait for a quick user acknowledgment (`ok` / `proceed`) before invoking `implement-td`.

## Compact Template (`doc/specs/YYYY-MM-DD-<slug>.md`)

```markdown
# TD-Lite: <Title>

- **Date:** YYYY-MM-DD
- **Slug:** `<slug>`
- **Base Branch:** `develop @ <short-sha>`
- **Planned Branch / Worktree:** `feat/<phase>-<slug>` / `.worktrees/YYYY-MM-DD-<slug>`
- **Mode Boundary:** Fast App Mode | Guided Cloud Mode (if touching live AKS/Azure)
- **Escalation Check:** Confirmed single-service / single-manifest; no `proto/`, DB schema/index, or cross-env stateful changes.

## 1. Problem & Minimal Scope
- What is broken or being tuned, and the exact path(s) affected.

## 2. Design Change (No Implementation Code)
- Concise description of the behavioral, configuration, or HPA/manifest adjustment.
- Edge cases, null-safety, or concurrency implications (if any).

## 3. Isolation & Cap Check
- Confirm `staging` and `production` remain fully isolated (no shared DB, Kafka, Redis, Secrets, Terraform state, or OIDC).
- Confirm Azure Free Trial `2 × Standard_B2s` (4 vCPU) and monitoring caps are respected.

## 4. Verification & Test Plan
- Exact unit/integration tests to add or update (`dotnet test`, `go test -race ./...`, `kubectl kustomize`, or `terraform validate`).
```
