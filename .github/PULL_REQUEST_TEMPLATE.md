## Summary

- **Milestone / Scope**:
- **Contracts touched (`CONTRACTS-v2.md`)**:

## Verification Checklist

- [ ] `dotnet restore src/Steppewise/Steppewise.csproj --locked-mode` succeeds with zero lockfile drift
- [ ] `dotnet build src/Steppewise/Steppewise.csproj -c Release --no-restore -warnaserror` succeeds (`0` warnings, `0` errors)
- [ ] Fresh-storage `python3 scripts/qualify.py` passes (`0` failures)
- [ ] Every new runtime claim is backed by an assertion in `scripts/qualify.py`
- [ ] Zero machine-specific paths, credentials, or PNG rasters added to the repository
