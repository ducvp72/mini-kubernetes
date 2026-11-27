---
name: parallel-worktree-agents
description: Orchestrate parallel subagents across isolated Git worktrees cut from develop in mini-kubernetes. Enforces contract-first merging, disjoint per-worktree file ownership, mandatory User Review Gate, cross-worktree conflict resolution against develop, full post-merge verification, and safe worktree cleanup.
---

# Parallel Git Worktree Subagents (`parallel-worktree-agents`)

Use this skill whenever work spans **2+ independent components** in `ducvp72/mini-kubernetes` (e.g., `open-api`, `order-service`, `webhook-service`, and/or `infra`/`deploy`) so parallel subagents never collide on files, `develop` stays clean as the single source of truth, and every change passes human review before merging.

> **Authority:** Follows `AGENTS.md` Hard Rules #1, #2, #3, #7, and #8. Where generic worktree skills assume a single branch/worktree, this project skill governs multi-worktree parallel subagent execution in `mini-kubernetes`.

---

## Core Invariants

1. **`develop` is the clean source of truth.** Never write feature/service code in the main checkout on `develop` (or `main`). Allowed in the main checkout: reading, `git fetch`/`pull`, creating/removing worktrees, user-approved sequential merges into `develop`, and pushing `develop`.
2. **Contract first.** If `proto/order/v1/order.proto` or `proto/webhook/v1/webhook.proto` changes, complete and merge the contract worktree (`feat/<phase>-proto`) into `develop` **before** cutting downstream service worktrees.
3. **1 Subagent = 1 Worktree = 1 Branch = Disjoint Ownership.** Every parallel subagent works strictly inside its own `.worktrees/<YYYY-MM-DD>-<slug>` directory and branch, and MUST NEVER touch files outside its assigned ownership or alter `proto/`.
4. **No Auto-Commit, Merge, or Push / Mandatory User Review Gate.** Neither the coordinator nor any subagent may run `git commit`, `git merge`, or `git push` until the user explicitly allows it. Leave all changes **uncommitted** inside each worktree so the user can review via file changes in their editor/IDE, request fixes, and approve committing, merging into `develop`, and pushing to `origin`.
5. **Conflict Resolution Against `develop`.** When merging multiple worktrees back into `develop`, resolve any conflict inside the incoming worktree against the updated `develop` branch and re-verify before completing the merge.

---

## Step-by-Step Orchestration Playbook

### Step 1: Sync `develop` & Check Free Branch Names
Run in the main checkout (foreground):

```bash
git fetch origin && git checkout develop && git pull --ff-only origin develop
git status --short   # must be clean
```

Verify every planned branch name is free locally and on `origin`:

```bash
for B in feat/<phase>-open-api feat/<phase>-order-service feat/<phase>-webhook-service; do
  git show-ref --verify -q "refs/heads/$B" && echo "TAKEN local: $B"
  git ls-remote --exit-code --heads origin "$B" >/dev/null && echo "TAKEN remote: $B"
done
```

### Step 2: Contract-First Worktree (Only When `proto/` Changes)
1. Cut the contract worktree from `develop`:
   ```bash
   DATE=$(date +%F)
   git worktree add -b "feat/<phase>-proto" ".worktrees/${DATE}-proto" develop
   ```
2. Edit only `proto/`, run `make proto` / stub generation, verify compilation, commit on `feat/<phase>-proto`.
3. Get user approval (if not already approved via TD), merge `feat/<phase>-proto` into `develop`, and remove `.worktrees/${DATE}-proto` before Step 3 so every service worktree starts from the settled contract.

### Step 3: Split `develop` into Parallel Git Worktrees
Cut one isolated worktree + branch per independent service/area directly from updated `develop`:

```bash
DATE=$(date +%F)
git worktree add -b "feat/<phase>-open-api"        ".worktrees/${DATE}-open-api"        develop
git worktree add -b "feat/<phase>-order-service"   ".worktrees/${DATE}-order-service"   develop
git worktree add -b "feat/<phase>-webhook-service" ".worktrees/${DATE}-webhook-service" develop
# Optional 4th worktree when infra/manifests/CI are in scope:
# git worktree add -b "feat/<phase>-infra"         ".worktrees/${DATE}-infra"           develop
```

Verify all worktrees exist cleanly:
```bash
git worktree list
```

### Step 4: Spawn Parallel Subagents (`invoke_subagent`)
Launch all independent subagents **in parallel in a single turn** (`TypeName: "self"`, `Workspace: "inherit"` — each subagent operates inside its assigned absolute `.worktrees/<YYYY-MM-DD>-<service>` path).

Every subagent prompt **MUST** specify:
1. **Worktree absolute path & branch name** (explicitly instructing: *"Execute ALL file reads/edits and commands inside `<ABS_WORKTREE_PATH>`. Never edit files in the main repo root."*).
2. **Disjoint file ownership table:**
   | Worktree / Subagent | Allowed Paths | Forbidden Paths |
   | :--- | :--- | :--- |
   | `open-api` | `services/open-api/**` | `proto/**`, other `services/**`, root `Makefile`/`docker-compose.yml` |
   | `order-service` | `services/order-service/**` | `proto/**`, other `services/**`, root `Makefile`/`docker-compose.yml` |
   | `webhook-service` | `services/webhook-service/**` | `proto/**`, other `services/**`, root `Makefile`/`docker-compose.yml` |
   | `infra` *(optional)* | `deploy/**`, `infra/**`, `.github/**`, `loadtest/**`, `docker-compose.yml` | `proto/**`, `services/**` |
3. **Spec path:** `doc/specs/YYYY-MM-DD-<slug>.md` (read-only reference).
4. **Required engineering skills:**
   - `.NET 8` subagents: `implement-td`, `dotnet-backend-api`, `ef-core-skill`, `resource-gc-management`, `clean-architecture-ddd`, `database-performance`, `coding-quality`, `high-throughput-scale`, `write-tests`.
   - `Go` subagent: `implement-td`, `high-throughput-scale`, `coding-quality`, `write-tests`.
5. **Escalation rule:** If a subagent discovers a `proto/` mismatch or needs a shared root file changed, it MUST stop and report back rather than editing outside its ownership.
6. **Per-worktree verification — DO NOT auto-commit or merge:** Each subagent runs its own build & unit tests inside its worktree (serialize any integration tests that share fixed local ports or local Docker MySQL/Redpanda!), verifies `git status --short` has no secrets/junk, and **MUST NOT run `git commit`, `git merge`, or `git push`**. Leave all edits uncommitted in the worktree so the user can review via file changes.

> **Reactive Wakeup:** Do NOT poll subagents in a loop. End the turn and wait for completion notifications from all subagents.

### Step 5: Mandatory User Review Gate
Once all parallel subagents report back green, collect a consolidated file-change & evidence summary across all worktrees (leaving all worktrees **uncommitted**):

```bash
for wt in .worktrees/${DATE}-*; do
  echo "=== $wt ($(git -C "$wt" branch --show-current)) ==="
  git -C "$wt" status --short
  git -C "$wt" diff --stat
done
```

Present to the user:
- Summary of changes per worktree (with clickable links to key files).
- Exact build, test, and coverage results from each worktree.
- Any open questions or shared-file wiring needed.
- **STOP and ask for approval:**
  - **If the user requests modifications:** Apply changes inside the relevant worktree(s) (re-using that subagent via `send_message` if still active, or dispatching focused edits in that worktree), re-run verification, and return to this Review Gate. **`develop` remains untouched.**
  - **If the user approves:** Proceed to Step 6.

### Step 6: Sequential Merge & Cross-Worktree Conflict Resolution Against `develop`
Merge approved worktree branches sequentially in dependency order (`order-service` → `open-api` → `webhook-service` → `infra`):

For **each** approved worktree `$WT` on branch `$BRANCH`:
1. Ensure clean commit on `$BRANCH` (verify no `.env`, kubeconfigs, `*.tfstate*`, `terraform.tfvars`):
   ```bash
   git -C "$WT" status --short
   ```
2. **Compare & sync `$BRANCH` against latest `develop` inside `$WT`** (catches any conflict with earlier merged worktrees BEFORE touching `develop`):
   ```bash
   git -C "$WT" merge --no-edit develop
   ```
   - **If conflicts occur:**
     - Inspect conflicted files (`git -C "$WT" diff --name-only --diff-filter=U`).
     - Resolve every conflict carefully against `develop` (never hand-edit generated stubs — re-run `make proto` if stubs conflict; keep both services' disjoint changes intact).
     - Complete the merge commit inside `$WT`, then **re-run build & test inside `$WT`** to confirm zero regressions.
3. **Merge verified `$BRANCH` into `develop`** from the main checkout:
   ```bash
   git checkout develop
   git merge --no-ff "$BRANCH" -m "feat(<phase>): merge <scope> into develop"
   ```
4. **Full Integrated Verification on `develop`** (after ALL worktree branches are merged):
   ```bash
   make build && make test
   # Plus docker compose smoke test / kustomize / terraform validate when applicable
   ```
5. Only after integrated checks on `develop` are 100% green **and the user explicitly approves pushing**, run:
   ```bash
   git push origin develop
   ```

### Step 7: Safe Worktree & Branch Cleanup
Remove merged worktrees and delete merged local branches (never force-remove a dirty worktree):

```bash
for wt in .worktrees/${DATE}-*; do
  BRANCH=$(git -C "$wt" branch --show-current)
  git worktree remove "$wt" || { echo "KEPT $wt: worktree not clean"; continue; }
  git branch -d "$BRANCH" || echo "KEPT branch $BRANCH: not fully merged"
done
git worktree prune
git worktree list
```

---

## Anti-Patterns to Avoid
- Never let two parallel subagents edit the same worktree or the same file (e.g., `docker-compose.yml` or root `Makefile` — assign shared root files to ONE designated owner or apply them after service merges).
- Never merge into `develop` before the User Review Gate passes.
- Never run `git worktree remove --force` on a worktree with uncommitted changes.
- Never run `git merge` into `main` from this skill — promoting `develop` → `main` requires `staging` verification + explicit user approval (`AGENTS.md` Hard Rule #8).
