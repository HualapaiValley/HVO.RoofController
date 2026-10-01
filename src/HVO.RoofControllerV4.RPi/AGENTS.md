# Agent instructions: HVO.RoofControllerV4.RPi

## Project Overview
The Roof Controller V4 project automates the observatory roof using ASP.NET Core APIs, a SignalR status hub, and a Sequent Microsystems relay/input HAT on the Raspberry Pi (I2C, via `HVO.Iot.Devices`). It has no UI of its own: the web UI (`HVO.RoofControllerV4.Web`), the terminal UI and CLI (`HVO.RoofControllerV4.Cli`), the kiosk (`HVO.RoofControllerV4.Kiosk`) and the Mac app (`HVO.RoofControllerV4.Mac`) are separate clients of its API and hub. Safety-first operation is paramount. The repository-wide rules, including the safety systems and the API, are in the root [AGENTS.md](../../AGENTS.md); this file adds project-specific points.

## Critical Reminders
- Use structured logging with named parameters (message templates, not interpolation) for every relay change, input transition, watchdog/lease event and fault latch/clear. Follow the patterns already in `Logic/RoofControllerServiceV4.cs`.
- `Logic/RoofControllerServiceV4.cs` drives sequencing behind `Logic/IRoofControllerServiceV4.cs`; failures that callers must map to API codes use `Logic/RoofControllerException.cs` (`RoofControllerErrorCode`). The HAT is reached through `HVO.Iot.Devices`; keep hardware access behind interfaces that tests can fake, and take `ILogger<T>` as an optional constructor dependency.
- Describe safety behavior exactly as implemented: the watchdog is an absolute cap on a movement (not a dead-man control), the operator lease is optional (`OperatorLeaseTimeout`), the fault latch is cleared only by `ClearFault`, and relay read-back verifies the HAT register, not the physical contacts.
- Keep REST endpoints under the versioned route (`api/v4.0/RoofControl`). Commands are `POST` and need an `X-Api-Key` or a person's session (`Authorization: Bearer`) with the right role; failures are ProblemDetails carrying `code` and `roofStatus` (`RoofControllerApiContract` in the Common project). Breaking changes must be versioned and listed in `CHANGELOG.md`. Security details: [docs/security.md](../../docs/security.md).
- Reference [hardware-overview.md](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) and [commissioning.md](../../docs/commissioning.md) when changing limit switch or fault-input polarity, relay mapping, or operational flows. Anything that only the real HAT, VFD or wiring can confirm is an installation assumption on the commissioning checklist, tied to its setting; its logic is still tested against the emulated plant.
- Timers that guard safety logic must use the disposable recreation pattern (no Start/Stop reuse). Maintain watchdog coverage in tests.

## Testing Expectations
- Extend `tests/HVO.RoofControllerV4.RPi.Tests` with MSTest, FluentAssertions and Moq, using the shared fake HAT (`TestSupport/FakeRoofHat.cs`) and `TestSupport/RoofControllerTestFactory.cs`. New safety logic must include positive and negative path coverage.
- When changing API behavior, update the controller tests (`Controllers/`) and the project [README](README.md) so device operators know what to expect.
- Run tests from `src/` so `src/global.json` applies: `cd src && dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj`. Release builds treat warnings as errors.

## UI
- The controller serves no pages. A UI change belongs in a client (`HVO.RoofControllerV4.Web`, `HVO.RoofControllerV4.Cli`, or `HVO.RoofControllerV4.Screens` for the kiosk and the Mac app), which reaches the controller only through `HVO.RoofControllerV4.Client` (REST and the status hub).

## Deployment Notes
- Docker artifacts (`Dockerfile`, `docker-compose.yaml`, `deploy-roofcontroller-rpi.sh`, `container/`) build the image for the Raspberry Pi (`linux/arm64`) and for test rigs (`linux/amd64`); the build context is the repository root and `/.dockerignore` applies. The Dockerfile's SDK tag must match `src/global.json`. Validate native library dependencies before introducing changes that require additional packages. Deployment procedure: [docs/deployment.md](../../docs/deployment.md).
- The wiring source of truth is [hardware-overview.md](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) plus `docs/projects/roof-controller-v4-rpi/diagrams/`. Update those when physical wiring or state machines change. `docs/RoofController_Wiring_Package.zip` is an archived snapshot and is not authoritative.
