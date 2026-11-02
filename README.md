# Steppewise

**Steppewise: deterministic rails for agentic workflows. .NET 10 · Microsoft Agent Framework · OpenTelemetry. Every step verified, every decision human, every run a receipt.**

[![CI](https://github.com/djcdevelopment/steppewise/actions/workflows/ci.yml/badge.svg)](https://github.com/djcdevelopment/steppewise/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4)](global.json)
[![Microsoft Agent Framework](https://img.shields.io/badge/Microsoft.Agents.AI.Workflows-1.27.0-0078D4)](src/Steppewise/Steppewise.csproj)

Steppewise is not a bespoke workflow engine; it wraps Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows` `1.27.0`) execution in deterministic contract rails, an append-only event-sourced ledger, and SHA-256-bound human review. Microsoft Agent Framework does for governance, observability, traceability, and agent-to-agent handoff what controller attributes did for API contracts when Swagger started generating from the build — the contract stops being a document someone maintains and becomes a byproduct of execution. Steppewise demonstrates that: the ledger, the spans, and the human-review receipt fall out of running the graph; nothing is written up afterward.

> **Execution boundary (`v0.1.0`)**: Steppewise executes deterministic synthetic fixtures (`evidenceMode: "fixture"`, `liveInference: false`) on a loopback-only host (`127.0.0.1`). It makes zero live LLM calls, performs no external network requests, and requires an explicit human review decision bound to the verified candidate's SHA-256 hash before any run can complete as `Accepted` or `Rejected`.

---

## Why Deterministic Rails on Microsoft Agent Framework

Most agentic demos treat governance and observability as post-hoc logging. Steppewise binds them directly to the Microsoft Agent Framework execution graph (`src/Steppewise/Runtime.cs`):

1. **Typed Sealed Executor Contracts (`src/Steppewise/Contracts.cs`)**: Every edge in `WorkflowBuilder` passes one of five `sealed record` types (`RunInput`, `Candidate`, `Verdict`, `ReviewDecision`, `RunOutcome`) carrying `RunId`, `Sequence`, and `CauseSequence`. Messages round-trip cleanly through `System.Text.Json` with zero runtime or session handle leaks.
2. **Draft → Verify → Repair → Verify → Human `RequestPort`**: Each workflow executes a validated five-step chain (`fixture.draft` → `verify` → `fixture.repair` → `verify` → `human.review`) through `InProcessExecution.RunStreamingAsync` and `RequestPort.Create<Verdict, ReviewDecision>`. Both the initial failed candidate (`candidate-1`) and the repaired passing candidate (`candidate-2`) are preserved in the ledger with their SHA-256 digests.
3. **Append-Only Event Ledger (`run-<id>.events.jsonl`)**: Every transition appends and flushes a `DurableEvent` JSONL record before advancing. Materialized snapshots (`run-<id>.json`) are stateless reductions. Snapshots are written 3 times per run: admission, awaiting review, terminal.
4. **Bidirectional OpenTelemetry Span Correlation (`spans.jsonl`)**: `ActivitySource("Steppewise")` and `ActivityListener` flush an activity for every domain event, matching `traceId` and `spanId` between `run-<id>.events.jsonl` and `spans.jsonl`.
5. **Fail-Closed Startup & Fault Recovery**: Interrupted in-flight runs recover as `Failed (Interrupted)`, interrupted review decisions surface as `Failed (NeedsReconciliation)` rather than silently auto-completing, and tampered files refuse startup.

![Beginner Execution Panel](docs/architecture/panels/a-execute.svg)

---

## Quick Start (Linux, macOS, and Windows)

Requires the **.NET 10 SDK** (`10.0.401` or newer `10.0.4xx` feature band; configured in `global.json`).

### Linux / macOS (POSIX)

```bash
dotnet restore src/Steppewise/Steppewise.csproj --locked-mode
dotnet build src/Steppewise/Steppewise.csproj -c Release --no-restore -warnaserror
./scripts/run.sh
```

Open **http://127.0.0.1:5188** in your browser. To rotate to a clean evidence directory at any time:

```bash
./scripts/fresh.sh
```

### Windows (PowerShell)

```powershell
dotnet restore src/Steppewise/Steppewise.csproj --locked-mode
dotnet build src/Steppewise/Steppewise.csproj -c Release --no-restore -warnaserror
$env:STEPPEWISE_ENV = "dev"
$env:STEPPEWISE_PORT = "5188"
$env:STEPPEWISE_STORAGE = Join-Path $PWD ".local\evidence"
$env:STEPPEWISE_DEFINITIONS = Join-Path $PWD "examples"
dotnet run --project src/Steppewise/Steppewise.csproj -c Release --no-build
```

---

## Walkthrough: Draft, Verify, Repair, and SHA-Bound Human Review

If you have forty minutes and want to see it before reading it: [walkthrough panel](docs/architecture/panels/a-execute.svg) (the execution panel is the only panel currently in that directory).

1. **Select a workflow**:
   - **Service intake · change request review** (`service-intake`, `examples/service-intake.json`) — generates a synthetic change-request packet (`ticket: "CR-1042"`), catches a missing `rollbackPlan` on `candidate-1`, repairs the packet in `candidate-2`, and pauses at `AwaitingReview`.
   - **Local AI work · bounded maintenance note** (`local-ai-work`, `examples/local-ai-work.json`) — drafts a maintenance summary, catches an unverified `statusClaim` on `candidate-1`, repairs it to `pending-verification` in `candidate-2`, and pauses at `AwaitingReview`.
2. **Switch views (`Operator`, `Business owner`, `Engineer`)**:
   - **Operator view** shows synchronous domain progression and preserved candidates.
   - **Business owner view** filters out `ActionStarted` step-entry noise to highlight outcomes and review decisions.
   - **Engineer view** exposes per-step status badges, candidate SHA-256 digests, and the raw `Diagnostics` split between synchronous `domainEvents` and asynchronous `frameworkArrivalEvents` (`arrival-order-not-causal`).
3. **Record a human decision**:
   - Optionally enter a reviewer identifier (`actorSource: "self-reported"`, or omit to record `local-process-user`) and click **Accept repaired result** or **Reject run**.
   - `POST /api/runs/{id}/review` verifies `expectedHash` against the latest verified candidate's SHA-256 hash. Stale hashes, duplicate decisions, or reviews against failed runs return `HTTP 409`.

---

## Architecture as Built

All architecture diagrams in `docs/architecture/` are native Archify `2.16.0` SVG exports paired with their declarative JSON sources (`docs/architecture/*.json`).

### 1. User Journey & Human Decision (`docs/architecture/journey.svg`)

![User Journey and Human Decision](docs/architecture/journey.svg)

### 2. Component Responsibilities & Trust Boundaries (`docs/architecture/responsibilities.svg`)

![Component Responsibilities and Trust Boundaries](docs/architecture/responsibilities.svg)

### 3. Request Lifetime & Event-to-Span Correlation (`docs/architecture/lifetime.svg`)

![Request Lifetime and Event-to-Span Correlation](docs/architecture/lifetime.svg)

### 4. Restart Reconstruction & Explicit Decision Receipt (`docs/architecture/review.svg`)

![Restart Reconstruction and Explicit Decision Receipt](docs/architecture/review.svg)

---

## Qualification Evidence & Fault-Injection Results

Steppewise ships a zero-dependency Python qualification harness (`scripts/qualify.py`) with two verification modes:

### 1. Fast HTTP Qualification (`27 / 27` checks, `< 0.1 s`)

```bash
python3 scripts/qualify.py --url http://127.0.0.1:5188
```

Verifies admission, failed original candidate preservation (`candidate-1`), repaired candidate verification (`candidate-2`), SHA-256 digest integrity, causal `CauseSequence` domain chaining, executor entry ordering, stale-hash `HTTP 409` rejection, explicit accept/reject receipts, duplicate-review `HTTP 409` rejection, and unknown-workflow refusal across both `service-intake` and `local-ai-work` (`docs/qualification-linux.json`).

### Crash testing: 358 forced process kills, 3,435 / 3,435 checks

```bash
dotnet publish src/Steppewise/Steppewise.csproj -c Release -warnaserror -p:RestoreLockedMode=true -p:SourceRevisionId=$(git rev-parse HEAD) -o artifacts/portable
python3 scripts/qualify.py --managed --milestone A --dotnet dotnet --dll artifacts/portable/Steppewise.dll --output ./qualification.json
```

- **358 post-`Emit` process-kill fault trials** (`179` ordinals per workflow) across all **`13 / 13` declared `EmitSites`** (`start.admitted`, `action.started`, `candidate.produced`, `candidate.checked`, `maf.received`, `start.waiting`, `start.failed`, `review.requested`, `review.recorded`, `review.failed`, `review.restored`, `recovery.interrupted`, `recovery.reconcile`).
- **Zero dangling reviews (`362 / 362`)**: Every interrupted mid-execution run transitions deterministically to `Failed (Interrupted)` and every run interrupted after `DecisionRequested` transitions to `Failed (NeedsReconciliation)`.
- **Double-restart idempotency (`4 / 4`)**: Restarting recovered evidence a second time appends zero duplicate events.

See [docs/QUALIFICATION.md](docs/QUALIFICATION.md) and [CHANGELOG.md](CHANGELOG.md) for the full verification protocol and release metrics.

What review found and what changed: [docs/REVIEW-HISTORY.md](docs/REVIEW-HISTORY.md)

---

## Capability Boundaries & What Is Not Claimed

Steppewise documents its exact verified scope in [docs/STATUS.md](docs/STATUS.md) and [CONTRACTS-v2.md](CONTRACTS-v2.md):

- A truncated event log stops the whole host, not just that run. Per-run quarantine is planned for M4. See [`Runtime.cs` — `LoadEvidence`, lines 244–261](src/Steppewise/Runtime.cs#L244-L261).
- **Synthetic fixtures only (`v0.1.0`)**: No live LLM calls, external APIs, or prompt-injection benchmarks are executed.
- **Single-host `.owner.lock` storage**: Concurrent multi-host writers, network filesystems (NFS/SMB), and distributed PostgreSQL leases are not implemented in `v0.1.0`.
- **Graph reconstruction vs. `CheckpointManager`**: `RestoreReview` rebuilds a fresh three-node MAF review graph from the validated event log rather than calling `InProcessExecution.ResumeStreamingAsync` with a `CheckpointManager`.
- **Local `spans.jsonl` vs. OTLP export**: OpenTelemetry spans are written to local `spans.jsonl` via `ActivityListener`; network OTLP export is deferred to `M4`.
- **Loopback `dev`/`qa` host only**: `STEPPEWISE_ENV=prod` is refused at startup; `actor` attribution is self-reported or OS-process-user metadata, not authentication.

---

## Repository Map

```text
.
├── CONTRACTS-v2.md                        # Active v2 runtime, HTTP, storage, and traceability contracts
├── Directory.Build.props                  # Deterministic build, nullable, and warnings-as-errors settings
├── global.json                            # .NET 10 SDK configuration (10.0.401+, rollForward: latestFeature)
├── LICENSE                                # MIT License
├── SECURITY.md                            # Loopback boundary and vulnerability reporting policy
├── CONTRIBUTING.md                        # Locked restore, build, and qualification instructions
├── CHANGELOG.md                           # v0.1.0 release notes and breaking storage notice
├── examples/
│   ├── service-intake.json                # Five-step synthetic service change-request workflow (CR-1042)
│   └── local-ai-work.json                 # Five-step synthetic bounded maintenance-note workflow
├── scripts/
│   ├── run.sh                             # POSIX loopback launcher (dev profile, port 5188)
│   ├── fresh.sh                           # Rotates existing .local/evidence storage before starting fresh
│   └── qualify.py                         # HTTP (27 checks) and managed fault-injection qualification harness
├── docs/
│   ├── QUALIFICATION.md                   # Vertical-slice, fault-injection, and replay qualification protocol
│   ├── STATUS.md                          # Verified capabilities vs. pending M4 roadmap items
│   ├── qualification-linux.json           # Fresh-storage Linux HTTP qualification receipt
│   └── architecture/                      # Archify 2.16.0 SVG diagrams, declarative JSON sources, and manifest
└── src/Steppewise/
    ├── Steppewise.csproj                  # .NET 10 web host referencing Microsoft.Agents.AI.Workflows 1.27.0
    ├── packages.lock.json                 # Deterministic NuGet dependency lockfile
    ├── Program.cs                         # Loopback Kestrel setup, origin/host guards, and HTTP endpoints
    ├── Runtime.cs                         # MAF graph compilation, append-only event log, and review recovery
    ├── Contracts.cs                       # Sealed domain/executor records (RunSnapshot, Candidate, Verdict, etc.)
    ├── ControlGate.cs                     # Bounded control-proposal validation gate
    └── wwwroot/                           # Zero-framework browser UI (Operator, Business owner, Engineer views)
```

---

## Background

Chronological public writing by the author on this architecture and stack:

- `2026-04-16` — [Field notes from building with Microsoft's Agent Framework](https://www.linkedin.com/posts/derekciula_field-notes-from-building-with-microsofts-ugcPost-7450507875573174272--WlM/)
- `2026-05-06` — [Claude, MAF and OTeL - When and how to use.](https://www.linkedin.com/posts/derekciula_claude-maf-and-otel-when-and-how-to-use-ugcPost-7457884685101084672-wQu4/)
- `2026-06-17` — [Time is a flat circle.](https://www.linkedin.com/posts/derekciula_aiagents-datamodeling-softwarearchitecture-share-7472951086434213888-zw1Y/)
- `2026-07-08` — [technical overview of a local agentic fleet build out](https://www.linkedin.com/posts/derekciula_technical-overview-of-a-local-agentic-fleet-ugcPost-7480716731154628608-WqKv/)
- `2026-07-16` — [One JSONL File: A simple manifest and stop paying frontier prices for local tools and file access.](https://www.linkedin.com/posts/derekciula_aiengineering-agenticai-llmops-ugcPost-7483472420796854273-HpiD/)

---

## License & Independence

See [LICENSE](LICENSE), [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and [CONTRACTS-v2.md](CONTRACTS-v2.md).

Steppewise — stepwise execution, from Steppe Integrations.

Steppewise is an independent open-source project by Derek Ciula / Steppe Integrations, released under the MIT License. It is not affiliated with, derived from, or endorsed by any employer or client, and contains no proprietary code, data, or prompts.
