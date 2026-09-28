## Description

Brief description of the changes in this PR.

## Related Issue

Closes #

## Changes Made

- ...

## Checklist

Run the commands from `src/` so `src/global.json` selects the SDK.

- [ ] `dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests -c Release` completes with zero errors and zero warnings (Release treats warnings as errors)
- [ ] `dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests` — all tests pass
- [ ] No new compiler or analyzer warnings introduced
- [ ] Safety-relevant changes (relays, inputs, limits, watchdog, lease, fault latch, Stop path) have tests, and `docs/commissioning.md` names the emulated scenario for each check they affect and any installation assumption it cannot prove
- [ ] Documentation updated (if applicable)
- [ ] Issue linked in PR description
