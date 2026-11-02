# Contributing to Steppewise

Thank you for your interest in contributing to Steppewise.

## Prerequisites

- **.NET 10 SDK** (`10.0.401` or newer `10.0.4xx` feature band; configured in `global.json`)
- **Python 3.10+** (for the zero-dependency `scripts/qualify.py` qualification harness)

## Build and Verification Workflow

1. **Restore with locked dependencies and build with warnings as errors**:
   ```bash
   dotnet restore src/Steppewise/Steppewise.csproj --locked-mode
   dotnet build src/Steppewise/Steppewise.csproj -c Release --no-restore -warnaserror
   ```
2. **Start a fresh-storage local host and run the HTTP qualification suite (`27` checks)**:
   ```bash
   ./scripts/fresh.sh
   ./scripts/run.sh &
   python3 scripts/qualify.py --url http://127.0.0.1:5180
   ```
3. **Run managed fault-injection qualification before submitting runtime changes**:
   ```bash
   dotnet publish src/Steppewise/Steppewise.csproj -c Release -warnaserror -p:RestoreLockedMode=true -p:SourceRevisionId=$(git rev-parse HEAD) -o artifacts/portable
   python3 scripts/qualify.py --managed --milestone A --dotnet dotnet --dll artifacts/portable/Steppewise.dll --output ./qualification.json
   ```

## Pull Request Guidelines

- Keep executor message records (`RunInput`, `Candidate`, `Verdict`, `ReviewDecision`, `RunOutcome`) sealed and round-trippable through `System.Text.Json`.
- Never claim a durability, recovery, or telemetry property in documentation unless it is backed by a named check in `scripts/qualify.py`.
- Ensure `git status --porcelain` is clean and no machine-specific paths or credentials are included.
