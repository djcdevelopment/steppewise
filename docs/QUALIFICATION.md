# Qualification protocol

Keep evidence categories separate: Linux build; synthetic fixture execution; Windows execution; actual local inference; offline execution; independent review. Timestamp each run and record revision, SDK/package lock hashes, environment profile, launch command, outcomes and limitations.

## Vertical-slice gate

1. Clean locked restore and Release build.
2. Run `scripts/qualify.py` against a fresh local evidence directory; retain its JSON output.
3. Manually inspect both browser workflows, including rejection and acceptance.
4. Stop and restart at human review. Record actual behavior; do not infer checkpoint recovery from files existing.
5. Independently rehearse the exact candidate on Windows and Linux. No reviewer self-awards an independent verdict.

## Remaining runtime gates

Before claiming multi-host durability, exercise 100 bounded fault/duplicate scenarios including stale ownership, database loss, review races, cancellation, and crashes after external effects but before checkpoint persistence. Ambiguous effects must require reconciliation. Healthy-storage recovery target is 30 seconds with a 15-second lease and 5-second heartbeat. This slice does not claim those targets passed.

A2A qualification requires separate processes exercising discovery, lifecycle, streaming, reconnect, cancellation, authentication and trace propagation. Local MCP integration is a separate interface.

## Offline controller gate

Use the existing small-controller checkpoint as a baseline before qualifying larger hardware. No model or GPU selection is justified by VRAM alone. Verify current allocation and obtain the authorized isolated test window before live qualification; never interrupt other work or substitute cloud inference.

Begin with N=1 and a 20-case screen. Finalists receive 100 independently authored, held-out episodes balanced across service intake and AI work, repeated three times. Keep tuning examples separate. Score arguments and complete outcomes separately from valid JSON. Include missing evidence, refusal, stale state, adversarial source content, queue contention and recovery.

Targets: 99% correct commands/arguments, 95% complete episodes per application, 95% correct abstention/evidence requests, zero unauthorized committed actions, 30% lower operator interaction time, and p95 decision latency at most 10 seconds at the declared load. Report sample sizes and uncertainty. Engine rejection contains a bad model proposal but does not make that proposal correct.

WAN-disconnected proof requires preinstalled models, dependencies, local authentication and a functioning local network. Service intake must visibly wait when internet access is unavailable. Manual controls and deterministic transitions must remain usable when the model is unavailable.

## Managed Fault and Replay Qualification

Publish the exact committed candidate with `dotnet publish src/Steppewise -c Release -p:RestoreLockedMode=true -p:SourceRevisionId=$(git rev-parse HEAD) -o artifacts/portable`, then run:

```bash
python3 scripts/qualify.py --managed --milestone A --dotnet dotnet --dll artifacts/portable/Steppewise.dll --output ./qualification.json
```

Use `--milestone B` (verification tier B) for revision/hash, domain-span, and reviewer identity checks. Managed mode copies the published package to its fresh isolated storage root before running. The report gives the storage root, DLL hash, all assertions, request timings, and trial/run latencies. Each fault case has isolated evidence. On restart the prior fault marker and site trace are retained as `.attempt-N` files. A baseline trace determines every dynamic fault ordinal; a separately declared `EmitSites` inventory determines call-site coverage.

Fault injection is only for QA hosts launched by the harness: it waits for the durable barrier, kills that exact child PID, and restarts its evidence without the fault setting. External `--url` mode never kills a host. Repair-failure and review-executor-failure fixtures are explicit QA scenarios. They perform no inference or external side effects.

The collector self-check records a deliberately false local probe followed by a true sentinel. `--inject-check-failure` additionally inserts an intentional failing assertion into the actual report; later independent scenarios still run and the process exits nonzero after writing JSON. A scenario whose input cannot be parsed is reported as an exception instead of hiding subsequent independent scenarios.

These are process-kill-at-durable-barrier checks. They do not prove power-loss durability, multi-process leases, OTLP delivery, or Windows execution.

For verification tier C add `--milestone C --golden-root <preserved-baseline-storage-root>`. This also checks measured persistence writes, log-only replay, tampering refusal, corrupt-log preservation, archived legacy reads, typed payload/projection agreement, five-step compatibility and rejection of a sixth step. Missing golden evidence is a failed check, not permission to regenerate a new baseline from the candidate under test.

Verification tiers B and C additionally require `--browser-evidence <path>` containing the actual browser probe's `gitRevision`, `dllSha256`, and `actorsAllPersonas:true`; the recorded identity must match this candidate. Tier C creates and runs an isolated .NET serialization probe against the copied DLL, checking sealed message records, default JSON roundtrips, preserved causal coordinates, and absence of runtime handles. The probe requires the same installed SDK; its source and output are retained under the qualification root.
