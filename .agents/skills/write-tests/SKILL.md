---
name: write-tests
description: Implement comprehensive polyglot unit, integration, and verification tests from a Technical Design's Test Plan (doc/specs/YYYY-MM-DD-<slug>.md), targeting 80%–100% coverage across .NET 8 and Go services before merge/review.
---

# Write Tests (`write-tests`)

Use this skill to implement and verify the complete test suite specified in a Technical Design (`doc/specs/YYYY-MM-DD-<slug>.md`). Work inside the active feature worktree (`.worktrees/<YYYY-MM-DD>-<slug>`) — never mutate `develop`.

## Coverage Target & Philosophy

- **Target coverage: 80%–100%** across unit and integration tests for all changed/added code.
- **Evidence before assertions:** Run every test command and report real observed pass/fail counts and measured coverage percentages. Never claim tests pass without running them.
- **Strict environment isolation:** Integration tests MUST use ephemeral local resources (Testcontainers or isolated local Docker MySQL & Redpanda). NEVER point tests at `staging` or `production` cloud databases, Kafka clusters, Redis, or secrets.

## Polyglot Test Stack

| Service / Area | Stack & Tools | Commands |
| --- | --- | --- |
| `services/open-api` (.NET 8 REST) | **NUnit + Moq** + `WebApplicationFactory` (unit tests with `Mock<T>`, plus Docker-scoped integration tests) | `dotnet test services/open-api --collect:"XPlat Code Coverage"` |
| `services/order-service` (.NET 8 gRPC + MySQL + Kafka) | **NUnit + Moq** + **Docker-scoped integration tests** (`Testcontainers.MySql` / `Testcontainers.Redpanda` or `docker compose` isolated test containers) | `dotnet test services/order-service --collect:"XPlat Code Coverage"` |
| `services/webhook-service` (Go gRPC + HTTP callback) | Go `testing`, `net/http/httptest`, in-memory `bufconn` gRPC tests + Docker-scoped integration tests | `cd services/webhook-service && go test -race -coverprofile=coverage.out ./... && go tool cover -func=coverage.out` |
| End-to-End / Container Scope | Docker Compose full stack (`mysql`, `redpanda`, `open-api`, `order-service`, `webhook-service`, `mock-webhook`) + smoke script & `k6` in `loadtest/` | `docker compose up -d --build && ./scripts/smoke-test.sh` |

### `.NET 8` NUnit + Moq Conventions
- Use `NUnit` (`[TestFixture]`, `[SetUp]`, `[TearDown]`, `[Test]`, `[TestCase]`, `Assert.That(..., Is.EqualTo(...))`) and `Moq` (`new Mock<T>(MockBehavior.Strict)` or verified setups with `mock.Verify(..., Times.Once)`).
- Tag Docker container-scoped integration tests with `[Category("Integration")]` and unit tests with `[Category("Unit")]` so unit suites run fast without Docker (`--filter TestCategory=Unit`) and container integration suites run explicitly against real MySQL + Redpanda containers (`--filter TestCategory=Integration`).
- Include `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk`, `NUnit`, `Moq`, and `coverlet.collector` in every `.NET` test `.csproj`.

## Mandatory Test Categories

Read the **Test Plan** section of `doc/specs/YYYY-MM-DD-<slug>.md` and ensure every applicable category below is explicitly covered:

1. **Happy Path**
   - End-to-end expected behavior for every new/modified REST endpoint, gRPC RPC, Kafka/Redpanda producer/consumer, and webhook HTTP callback.
2. **Validation & Error Mapping**
   - Missing required fields, out-of-range values, malformed payloads, unknown resource IDs, and exact HTTP / gRPC status code translation.
3. **Null-Safety & Boundary Conditions**
   - Nullable reference types and optional DTO/proto fields in `.NET 8`; `nil` pointers, empty slices, zero-value structs, and closed channels in `Go`; empty result sets and max page sizes.
4. **Transaction Rollback & Atomicity**
   - Whenever a flow performs multi-step database writes (or DB write + outbox/event record), write an explicit integration test that **forces a failure on the final write** (e.g., via fault injection, constraint violation, or mocked failing post-step inside the transaction scope) and asserts that **zero partial rows** remain committed in MySQL.
5. **Concurrency & Race Conditions**
   - Parallel requests/workers hitting the same resource, optimistic concurrency conflict handling, idempotent retry / duplicate Kafka message handling, and a clean `go test -race ./...` run for `webhook-service`.
6. **Bidirectional Cursor Pagination (when pagination is present)**
   - Forward (`next`) and backward (`prev`) traversal across multiple pages.
   - Deterministic ordering with tie-breaker columns (no skipped or duplicated rows).
   - Boundary cases: first page (`has_prev == false`), last page (`has_next == false`), single-item page, empty dataset.
   - Rejection of malformed, expired, or tampered cursors.

## Workflow

1. **Read the TD Test Plan**
   - Open `doc/specs/YYYY-MM-DD-<slug>.md` and list every planned test case and affected service.
2. **Audit Existing Tests & Coverage Baseline**
   - Inspect existing test projects in the active worktree to match naming, fixtures, and helper conventions.
3. **Write Unit & Integration Tests**
   - Implement clean, deterministic, self-contained tests (no flaky `sleep`-based synchronization; use channels, latches, or polling timeouts).
   - Add brief comments only above non-obvious test harnesses (e.g., fault-injection hooks for rollback tests or cursor-encoding helpers).
4. **Run Full Verification & Collect Coverage**
   - Execute `.NET 8` tests with coverage collection, `Go` tests with `-race -coverprofile`, and optional smoke/`k6` checks if scoped in the TD.
   - If any test fails, fix the root cause (in test or implementation within the worktree) and re-run until green.
5. **Report Coverage & Gaps — Then STOP**
   - Present a concise summary table:
     - Service / Package
     - Tests run (passed / failed / skipped)
     - Measured line/branch coverage %
     - Explicit list of uncovered lines or gaps (with justification if any area is below 100%, and remediation options if below 80%)
   - **STOP and ask the user** whether to close any remaining gaps or proceed to final review and merge into `develop`.
