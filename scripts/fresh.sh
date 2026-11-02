#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Fresh evidence directory preserves all previous runs; this does not delete evidence.
export STEPPEWISE_STORAGE="$PWD/.local/evidence-$(date -u +%Y%m%dT%H%M%SZ)-$$"
exec scripts/run.sh
