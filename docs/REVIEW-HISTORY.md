# Review history

Findings addressed before the initial public release, plus remaining limits.
Release revisions and qualification evidence are recorded in [CHANGELOG.md](../CHANGELOG.md)
and [QUALIFICATION.md](QUALIFICATION.md).

| Finding | Found by | Fixed in |
| --- | --- | --- |
| Persona filter was a no-op; the business view now filters step-entry events. | review | v0.1.0 |
| Event ledger was non-causal; domain events now carry predecessor links, and framework arrival order is labeled separately. | review | v0.1.0 |
| Snapshot was rewritten per event; snapshots now accompany admission, awaiting review, and terminal state. | review | v0.1.0 |
| `DecisionRequested` could dangle after restart; unresolved decisions now recover as `Failed (NeedsReconciliation)`. | review | v0.1.0 |
| No fault-injection coverage; managed process-kill qualification now covers all declared emit sites. | review | v0.1.0 |
| Repair-fails path was never exercised; qualification now exercises and preserves terminal repair failures. | qualification | v0.1.0 |
| A torn event log stops the whole host; per-run quarantine remains planned. | qualification | open, M4 |
| Failed repair still reaches the review port before the run is failed. | review | open, M4 |
| Repair is unconditional in v1; this fixed-graph limitation is documented. | review | v0.1.0 (documented) |
