#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export STEPPEWISE_ENV="${STEPPEWISE_ENV:-dev}"
export STEPPEWISE_PORT="${STEPPEWISE_PORT:-5188}"
export STEPPEWISE_STORAGE="${STEPPEWISE_STORAGE:-$PWD/.local/evidence}"
export STEPPEWISE_DEFINITIONS="${STEPPEWISE_DEFINITIONS:-$PWD/examples}"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"
if ! command -v "$DOTNET_BIN" >/dev/null 2>&1; then
  echo 'Set DOTNET_BIN to a .NET 10 SDK dotnet executable.' >&2
  exit 1
fi
exec "$DOTNET_BIN" run --project src/Steppewise/Steppewise.csproj --no-launch-profile
