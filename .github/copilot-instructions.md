# Copilot Instructions — HVO.RoofController

## Project Overview

HVO.RoofController is the observatory roof automation system for HVO. It
consists of a Raspberry Pi server application and an iPad companion client for
remote roof control.

### Architecture

- **RPi Server** — ASP.NET Core + Blazor Server (SSR) application that manages
  GPIO and I2C hardware for roof motor control, limit switches, and relay
  management. Deployed via Docker to a Raspberry Pi (linux-arm64).
- **iPad Client** — .NET MAUI application providing a touch-optimized interface
  for monitoring and controlling the roof from the observatory floor.
- **Common Library** — Shared models, options, and DTOs used by both the RPi
  server and iPad client.
- **WebSite.Themes** — Razor Class Library providing shared CSS themes.

### Key Safety Systems

Describe these exactly as implemented; do not call the watchdog a dead-man control.

- Maximum-run watchdog (`SafetyWatchdogTimeout`, 5-600 s) — an absolute cap measured
  from the start of a movement. Repeating Open or Close in the same direction does not
  extend it. On expiry the roof stops and the fault latches.
- Operator lease (optional, `OperatorLeaseTimeout`, 2-120 s; off when unset) — Open/Close
  start a lease that the client renews with `POST .../Lease` while the roof moves. If
  renewals stop, the roof stops with `OperatorLeaseExpired`. Renewal never starts motion.
  Losing the client stops motion only when the lease is enabled.
- Limit switches (IN1/IN2) — stop at fully open and fully closed; polarity is set by
  `UseNormallyClosedLimitSwitches`. Starting at a limit is handled as a departure phase;
  contradictory limits (both active) stop motion and refuse starts.
- Fault latch — watchdog expiry, drive fault (IN3), relay verification failure, input
  read failure, contradictory limits and a reasserted start limit latch the fault.
  Open/Close are refused (`FaultLatched`) until a successful `ClearFault`. The latch
  never blocks Stop, and Stop does not clear it.
- Relay register read-back — every relay transition is verified by reading the HAT's
  relay register back. This proves the register, not the physical contacts; never
  describe it as contact verification. An unverified stop is reported as a failure
  (`RelayStateUnverified`), never as success.
- VFD fault input (IN3) — polarity is `FaultInputActiveHigh` (default `true`: raw HIGH =
  fault). Commissioning must confirm it against the real wiring before use.
- Drive-running interlock (IN4, optional) — with `AtSpeedConfirmationTimeout` set, IN4 must
  assert within that window after a start, otherwise the roof stops with `DriveNotRunning`.
- Input read failures — `MaxConsecutiveInputReadFailures` consecutive failed reads while
  moving stop the roof with `InputReadFailure`.
- `IgnorePhysicalLimitSwitches` is for the simulator. On physical hardware it is refused
  unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local configuration
  (never through the API).
- None of this replaces an independent hardware stop path (E-stop / VFD STOP input); see
  `docs/commissioning.md`.

### HTTP API

- Routes are under `api/v4.0/RoofControl`. Commands (`Open`, `Close`, `Stop`,
  `ClearFault`, `Lease`) are `POST`; `GET Status` and `GET`/`POST Configuration` complete
  the surface. There are no GET command routes.
- Every protected request needs an `X-Api-Key` header. Roles are `RoofViewer`,
  `RoofOperator` and `RoofAdmin` (Admin includes Operator includes Viewer); Stop accepts
  any authenticated role. Failures are RFC 7807 ProblemDetails with `code` and
  `roofStatus` extensions.
- `POST Configuration` requires `ExpectedVersion`, and safety-critical changes also need
  `ConfirmSafetyCriticalChange`.
- See `docs/security.md` for keys, roles and HTTPS.

### Technology Stack

- .NET 10 (RPi server and shared libraries)
- .NET 10 MAUI (iPad client)
- ASP.NET Core + Blazor Server (SSR)
- GPIO / I2C hardware interfaces
- Docker (RPi deployment)

### Dependencies

- `HVO.Core` — shared foundation library (NuGet from HVO.SDK)
- `HVO.Core.SourceGenerators` — compile-time code generation (NuGet from HVO.SDK)
- `HVO.Iot.Devices` — IoT device abstractions (NuGet from HVO.SDK)

## Solution Structure

```text
src/
  HVO.RoofControllerV4.RPi/         # Raspberry Pi server
  HVO.RoofControllerV4.iPad/        # iPad MAUI client
  HVO.RoofControllerV4.Common/      # Shared models
  HVO.WebSite.Themes/               # CSS theme RCL
  HVO.RoofController.sln            # Solution file
tests/
  HVO.RoofControllerV4.RPi.Tests/   # Unit and API tests (MSTest)
docs/                               # Hardware, commissioning, security, deployment, CI runners
```

## Coding Standards

- Follow existing code style and patterns in the repository
- Use `Directory.Build.props` and `Directory.Packages.props` for centralized
  package management
- All public APIs must have XML documentation comments
- Keep controllers thin — business logic belongs in services
- Use dependency injection throughout
- Hardware abstractions must be mockable for testing

## Build and Test

Run `dotnet` from `src/` so `src/global.json` selects the SDK:

```bash
cd src
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
# Release treats warnings as errors; CI builds both configurations
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj -c Release
```

The full solution includes the `net10.0-ios` project and requires macOS, the MAUI workload, and a supported Xcode version. On Linux, validate the portable graph through the RPi test project as shown above.

## Deployment

The RPi server runs in Docker on the Pi (`linux-arm64`, container port 8080). Deploy only
with `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh`, which requires a
verified Stop before replacing the container and fails closed otherwise; see
`docs/deployment.md`. The production compose file is
`src/HVO.RoofControllerV4.RPi/docker-compose.yaml`.

`src/docker-compose.yml` is a local simulation compose (Development environment, no HAT
devices, port 5200). Never use it on the observatory Pi. `dotnet run` uses port 5195.

## CI/CD

- `ci.yml` — ubuntu; restores, builds (Debug) and tests the RPi test project graph from
  `src/`, checks that coverage is not empty, then builds Release with warnings as errors.
  It does not build the solution (the iOS project cannot build on Linux).
- `ios.yml` — `macos-26`, Xcode 26.6 via `DEVELOPER_DIR`, pinned workload set; builds the
  iPad app for the simulator without signing.
- `pi-image.yml` — builds the Pi image for `linux/arm64` (no push) and checks that the
  Dockerfile SDK tag matches `src/global.json`.
- `m5-ios-validation.yml` — self-hosted M5 runner; trusted triggers on `main` only. Read
  `docs/ci-runners.md` before changing it.

Pin every action to a full commit SHA with a `# vX.Y.Z` comment (the repository requires
SHA pinning) and keep `permissions: contents: read` unless a job needs more.

## Issue and PR Workflow

- Reference the issue number in branch names and PR descriptions
- Use conventional commit messages
- All PRs are squash-merged into `main`

## Dev Container

Use the provided Dev Container configuration for a consistent development
environment. Do not install additional tools or extensions into the Dev
Container without updating the configuration files.
