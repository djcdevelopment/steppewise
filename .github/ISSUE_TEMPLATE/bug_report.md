---
name: Bug Report
about: Report a reproducible defect in Steppewise runtime, contracts, or qualification
title: "[BUG] "
labels: bug
---

## Summary

Describe the unexpected behavior and which workflow (`service-intake` or `local-ai-work`) or endpoint is affected.

## Reproduction Steps

1. Commit / tag (`GET /api/info` `gitRevision` and `dllSha256`):
2. OS and `.NET` SDK version (`dotnet --version`):
3. Commands executed:
4. Observed vs. expected outcome:

## Qualification Output

Attach or paste the relevant `scripts/qualify.py` assertion failure or `run-<id>.events.jsonl` excerpt.
