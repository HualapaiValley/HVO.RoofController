```instructions
# Copilot Instructions for HVO.RoofControllerV4.RPi

## Project Overview
The Roof Controller V4 project automates the observatory roof using Blazor Server, ASP.NET Core APIs, and GPIO hardware integrations on the Raspberry Pi. Safety-first operation is paramount.

## Critical Reminders
- Follow the structured logging conventions spelled out in `docs/projects/roof-controller-v4-rpi/logging-reference.md`. Every GPIO action, relay change, and watchdog transition should include named parameters.
- Reuse the service and hardware patterns already present: `RoofControllerServiceV4` drives sequencing, and hardware abstractions live under `Logic/Hardware`. Model new devices after `GpioLimitSwitch` and ensure optional `ILogger<T>` injection.
- Keep REST endpoints consistent with the versioned contract documented in `docs/projects/roof-controller-v4-rpi/api-reference.md`. Breaking changes must be versioned.
- Reference `docs/projects/roof-controller-v4-rpi/hardware-overview.md` and `operator-cheat-sheet.md` when updating limit switch polarity, relay mapping, or operational flows.
- Timers that guard safety logic must use the disposable recreation pattern (no Start/Stop reuse). Maintain watchdog coverage in tests.

## Testing Expectations
- Extend `src/HVO.RoofControllerV4.RPi.Tests` with MSTest using the existing fake HAT harness. New safety logic must include positive and negative path coverage.
- When changing API behavior, update integration tests and the project README so device operators know what to expect.

## UI & Theme
- Components under `Components/` should keep logic in `.razor.cs` files and use scoped CSS. Ensure the layout applies `data-theme="hvo-dark"` and uses the shared theme tokens.

## Deployment Notes
- Docker artifacts (`Dockerfile`, `docker-compose.yaml`) target Raspberry Pi. Validate native library dependencies before introducing changes that require additional packages.
- Update the docs bundle (`RoofController_Diagrams_2025-09-26.zip`) or replace it when physical wiring or state machines change.
```
