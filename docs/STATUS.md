# Capability boundary

This document describes the `v0.1.0` slice, not the completed Steppewise roadmap. Update statuses only against exact revision evidence.

| Capability | `v0.1.0` scope |
|---|---|
| Two examples (`service-intake` and `local-ai-work`) and shared browser projections | Implemented; 27 HTTP checks passed in Linux qualification (`docs/qualification-linux.json`) |
| Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows` `1.27.0`) | Actual executor graph (`WorkflowBuilder`, `InProcessExecution.RunStreamingAsync`) and `RequestPort<Verdict, ReviewDecision>` review gate |
| Synthetic original / check / repair / review | Implemented; deterministic fixture execution (`liveInference: false`) |
| Declarative authoring | Restricted five-step sequential registered-action format (`ValidateDefinition`) |
| Persistence | Append-only event log (`run-<id>.events.jsonl`) with up to three phase snapshots (`run-<id>.json`) per normal run (`-94.55%` snapshot rewrites); log-only replay, tamper refusal, corrupt-log preservation, and interrupted-decision reconciliation verified across 358 process-kill fault trials |
| Concurrent hosts and PostgreSQL fencing | Pending (`M4`); single-host `.owner.lock` enforced; do not run two hosts against one evidence directory |
| General conditions, parallelism, arbitrary adapters | Pending (`M4`); `fixture.repair` runs unconditionally in the five-step chain |
| A2A interoperable hosting/client | Pending (`M4`); no A2A claim from MAF use alone |
| OpenTelemetry collection/export and trace viewer | Local `spans.jsonl` export and domain-event correlation implemented; OTLP network export and root-parented event spans (`M4`) pending |
| Local-model bounded controller | Pending qualification; no model calls in fixture execution |
| Production authorization | Pending; loopback development (`dev`) and `qa` only (`prod` refused at startup) |
| WAN-disconnected cold-start proof | Pending |
| Windows runtime | Cross-platform `.NET 10` paths and Windows CI workflow included; pre-tag qualification receipts executed on Linux x86-64 |

Private source prompts, customer material, credentials and infrastructure identifiers must not be included here.

## A2A feasibility finding

The official .NET self-hosted A2A guide currently documents in-memory default stores (`InMemoryTaskStore`) and says background responses (`AgentRunMode.DisallowBackground`) are not yet supported. Durable background-task lifecycle is therefore blocked at the compatibility gate; it must not be represented as implemented or replaced by a bespoke protocol under the A2A label. Source checked: https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/a2a/dotnet . Recheck against the exact selected package before extending this claim.

## Runtime and projection boundaries

Domain events carry `CauseSequence` pointing to the preceding domain event in this sequential fixture graph. Operator and engineer views exclude asynchronously delivered MAF stream events (`e.action !== 'maf'`); the `Business owner` view in `v0.1.0` is an event-row filter that hides action-entry detail (`e.status !== 'ActionStarted'`). Raw framework events remain in `Diagnostics`, explicitly labeled arrival order (`arrival-order-not-causal`), not causal order. Legacy events without `CauseSequence` remain readable; no causal evidence is retroactively invented.

Start remains tied to HTTP cancellation: a disconnect during execution can mark the run `Failed`. Admitted review completes independently of HTTP disconnect. Pending review restoration (`RestoreReview`) uses the definition's review step ID in a new three-node MAF review graph (`RunInput` → `Verdict` → `RequestPort` → `finish`), not `CheckpointManager` resume. Each event appends and flushes one log record; snapshots occur at admission, waiting review and terminal state. Snapshot-only legacy runs are read-only archives (`Archived = true`). Process-kill-at-`Emit` checks do not establish power-loss or multi-host durability.

Five-step definition validation (`ValidateDefinition`) rejects unsupported step counts or out-of-order definitions at startup (`definition.sixth-step-rejected`, `definition.no-ordinal-indexing`). Executor messages (`RunInput`, `Candidate`, `Verdict`, `ReviewDecision`, `RunOutcome`) are sealed, plain-JSON-serializable records with causal coordinates; this does not implement checkpoint resume or an external message bus.
