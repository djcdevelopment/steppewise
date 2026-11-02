# Changelog

All notable changes to this project are documented in this file.

## [0.1.0] - 2026-11-02

### Steppewise v0.1.0 — Deterministic Rails for Microsoft Agent Framework (.NET 10)

`Steppewise` `v0.1.0` is the initial public release of the `.NET 10` reference implementation for deterministic contract rails, append-only event-sourced receipts, OpenTelemetry span correlation, and SHA-256-bound human review on Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows` `1.27.0`).

### What Is Shipped and Verified in `v0.1.0`

- **Real Microsoft Agent Framework `1.27.0` Graph Execution**:
  - Executes five-step sequential workflows (`fixture.draft` → `verify` → `fixture.repair` → `verify` → `human.review`) through `WorkflowBuilder`, `InProcessExecution.RunStreamingAsync`, `StreamingRun.WatchStreamAsync(false, ct)`, and `RequestPort.Create<Verdict, ReviewDecision>`.
  - Enforces step-order validation in `ValidateDefinition`, rejecting unsupported step counts or out-of-order definitions at startup (`definition.sixth-step-rejected`, `definition.no-ordinal-indexing`).
- **Typed Sealed Executor Contracts & Stateless Projection**:
  - All five executor message types (`RunInput`, `Candidate`, `Verdict`, `ReviewDecision`, `RunOutcome`) are `sealed record` contracts carrying `RunId`, `Sequence`, and `CauseSequence` (`22/22` reflection and `System.Text.Json` round-trip checks passed, with zero runtime/session handle leaks).
  - `RunSnapshot` is a stateless reduction (`Reduce`) over `DurableEvent` rows (`10/10` `messages.projection-equivalence.*` and `10/10` `projection.baseline-equivalence.*` checks passed).
- **Append-Only Event Ledger & Startup Integrity (`-94.55%` Snapshot Rewrites)**:
  - Each run appends `DurableEvent` JSONL records to `run-<id>.events.jsonl` (`44` appends per accepted/rejected run, `32` on failed repair, `34` on failed review) and writes at most `3` materialized snapshots (admission, waiting review, terminal) instead of rewriting the full snapshot on every event (`3` vs `55` full-file rewrites, `-94.55%`).
  - Deleting `run-<id>.json` and restarting reconstructs identical state from `run-<id>.events.jsonl` alone (`4/4` `storage.log-only.*` checks).
  - Tampered snapshots (`2/2` `storage.tamper-rejected.*`) and corrupt/unterminated event logs (`4/4` `storage.corrupt-log-*`) fail closed at startup while preserving corrupt files byte-for-byte.
  - Legacy snapshot-only runs (`run-<id>.json` without `.events.jsonl`) load as read-only archives (`Archived = true`), remain inspectable via `GET /api/runs`, refuse review (`HTTP 409`), and are never mutated (`6/6` `storage.legacy-*` checks).
- **Post-`Emit` Process-Kill Fault Qualification (`8,182 / 8,182` Checks Passed)**:
  - `scripts/qualify.py --managed --milestone C` executed `358` post-`Emit` process-kill fault-injection trials (`179` per workflow) across all `13/13` declared emit sites (`maf.received`: `260`, `action.started`: `30`, `candidate.produced`: `16`, `candidate.checked`: `14`, `review.requested`: `8`, `start.admitted`: `8`, `review.recorded`: `6`, `start.waiting`: `6`, `recovery.interrupted`: `2`, `recovery.reconcile`: `2`, `review.failed`: `2`, `review.restored`: `2`, `start.failed`: `2`).
  - Across `362` managed restarts (`358` fault trials + `4` recovery seeds), `250` mid-execution interrupted runs transitioned deterministically to `Failed (Interrupted)`, `53` runs interrupted after `DecisionRequested` transitioned to `Failed (NeedsReconciliation)` (`49` fault trials + `4` review-fault/seed runs), `28` runs in `AwaitingReview` prior to `DecisionRequested` restored cleanly, `25` terminal decisions (`17` `Accepted`, `8` `Rejected`) and `2` terminal repair failures were preserved, and **`0` runs were left in an ambiguous dangling-review state** (`362/362` `fault.no-dangling-review.*` and `724/724` `fault.restart-state.*` checks passed).
  - Double-restart idempotency verified (`4/4` `recovery.idempotent.*` checks append zero duplicate events).
- **SHA-256-Bound Human Review & Reviewer Attribution**:
  - `POST /api/runs/{id}/review` requires `expectedHash` matching the latest verified candidate's SHA-256 digest; stale hashes, duplicate reviews, and terminal-run reviews are rejected with `HTTP 409` (`290` managed `fault.review-refused.*` checks + `4` HTTP checks).
  - Review events record `actor`, `actorSource` (`self-reported` or `local-process-user`), and `decisionId` across `DecisionRequested` and terminal `Accepted`/`Rejected` events (`119` `review.actor.*` and `31` `review.actor-continuity.*` checks).
- **Bidirectional Span-to-Ledger Correlation & Self-Verifying Build Identity**:
  - Every non-MAF domain event carries `traceId` and `spanId` matching a flushed `spans.jsonl` span tagged with `workflow.run.id` and `event.sequence` (`3,054/3,054` `trace.domain-event.*` checks).
  - `GET /api/info` exposes `gitRevision` and `dllSha256` verified on every managed host startup (`732/732` `identity.revision` and `732/732` `identity.dll-hash` checks).
- **Request Latency & Zero-CVE Locked Dependency Graph**:
  - Across `1,142` recorded HTTP requests during managed qualification: overall `median = 6.681 ms` and `p95 = 79.389 ms` (`max = 88.114 ms`), with `GET /api/runs/{id}` median `0.870 ms` (`p95 1.692 ms`), `GET /api/runs` median `3.409 ms` (`p95 4.741 ms`), `POST /api/runs/{id}/review` median `6.904 ms` (`p95 69.359 ms`), and full 4-step workflow start (`POST /api/runs/{workflow}`) median `66.241–68.513 ms` (`p95 84.696–85.239 ms`).
  - Deterministic NuGet lockfile (`src/Steppewise/packages.lock.json`) audited on `2026-11-02` against NuGet vulnerability indices and the GitHub Advisory Database (`0` vulnerable packages across all `12` direct and transitive dependencies).

### Not Yet Proven / Breaking in `v0.1.0`

- **Breaking Storage Compatibility (Pre-Rename Evidence Directories Will Not Load)**: Evidence directories created before the `Steppewise` / `service-intake` rename will not load. `ValidateSnapshot` (`src/Steppewise/Runtime.cs`) checks `!definitions.ContainsKey(s.Workflow)` at startup and refuses runs recorded under the retired pre-rename workflow ID. Rotate or clear `STEPPEWISE_STORAGE` (`./scripts/fresh.sh`) before starting `v0.1.0`.
- **Independent Windows Runtime Observation**: PowerShell launch commands and cross-platform `.NET 10` paths are included, and `.github/workflows/ci.yml` targets `windows-latest`, but all pre-tag qualification receipts (`8,182` managed checks and headless Chromium probes) were executed on Linux x86-64.
- **Live Model Inference & External Providers**: `v0.1.0` executes deterministic synthetic fixtures (`evidenceMode: "fixture"`, `liveInference: false`). No live LLM calls, prompt-injection evaluations, or model-latency benchmarks are part of this release.
- **MAF `CheckpointManager` Resume**: Restarting a run in `AwaitingReview` (`RestoreReview`) constructs a fresh three-node MAF review graph (`RunInput` → `Verdict` → `RequestPort` → `finish`) from the durable event log and verifies `DefinitionHash`. It does not call `InProcessExecution.ResumeStreamingAsync` with a `CheckpointManager`.
- **Multi-Process / Distributed Lease Coordination**: Single-writer storage ownership is enforced via an OS file lock (`.owner.lock` with `FileShare.None`) and a single in-process `SemaphoreSlim(1, 1)` gate across runs. Network filesystems (NFS/SMB), multi-host leases (PostgreSQL), and concurrent multi-run execution are not yet implemented.
- **Power-Loss / Hardware `fsync` Durability**: Fault qualification kills the host process immediately after `Emit` (`Environment.FailFast` / `SIGKILL` after durable file write). Kernel page-cache loss under physical power failure and automatic per-run quarantine of torn trailing JSONL lines (which currently refuses host startup fail-closed) are not yet implemented.
- **General Graph Branching, Parallelism, and Conditional Repair**: The `v0.1.0` interpreter supports the fixed five-step sequential chain (`fixture.draft` → `verify` → `fixture.repair` → `verify` → `human.review`). `fixture.repair` executes unconditionally, and a failed repair verdict passes through `RequestPort` before `StartAsync` fails the run after `Drain`.
- **OTLP Network Export & Root-Parented Event Spans**: Telemetry activities are written to local `spans.jsonl` via `ActivityListener`. No OTLP network exporter is configured, and `workflow.event.*` spans currently chain their `ParentSpanId` to the prior domain event span rather than `workflow.run`.
- **Authenticated Reviewer Identity**: `actor` records either a caller-supplied string (`actorSource: "self-reported"`) or `Environment.UserName` (`actorSource: "local-process-user"`). `v0.1.0` is a loopback-only `dev`/`qa` host (`prod` is refused at startup) with no authentication middleware.
- **Self-Hosted A2A Background Tasks & Service Bus Integration**: Upstream Microsoft Agent Framework `.NET` A2A self-hosting defaults to `InMemoryTaskStore` and `AgentRunMode.DisallowBackground`. Neither A2A endpoints nor Azure Service Bus transport adapters are wired into the `v0.1.0` runtime.
