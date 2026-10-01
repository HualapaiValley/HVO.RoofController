## Description

Brief description of the changes in this PR.

## Related Issue

Fixes #

## Changes Made

- ...

## Checklist

Run the commands from `src/` so `src/global.json` selects the SDK.

- [ ] `dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests -c Release` completes with zero errors and zero warnings (Release treats warnings as errors)
- [ ] `dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests` — all tests pass
- [ ] No new compiler or analyzer warnings introduced
- [ ] Safety-relevant changes (relays, inputs, limits, watchdog, lease, fault latch, Stop path) have tests, and `docs/commissioning.md` names the emulated scenario for each check they affect and any installation assumption it cannot prove
- [ ] Documentation updated (if applicable), screenshots included
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]` for a change a user or operator would notice (or the description says why none is needed)
- [ ] `build/secret-scan.py` prints `PASS`
- [ ] Issue linked in PR description (`Fixes #`)
