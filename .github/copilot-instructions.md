# Copilot Instructions — HVO.RoofController

## Project Overview

HVO.RoofController is the observatory roof automation system for HVO. It
consists of a Raspberry Pi server application with a browser-based operator console
and an authenticated HTTP API.

### Architecture

- **RPi Server** — ASP.NET Core + Blazor Server (SSR) application that manages
  GPIO and I2C hardware for roof motor control, limit switches, and relay
  management. Deployed via Docker to a Raspberry Pi (linux-arm64).
- **Web Console** — responsive Blazor interface for desktop, tablet and phone.
- **Common Library** — Shared server and HTTP API models, options and DTOs.
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
  `UseNormallyClosedLimitSwitches` (production `false`: the ME-8108 normally open pair).
  Starting at a limit is handled as a departure phase; contradictory limits (both active)
  stop motion and refuse starts. The optional `DepartureReleaseTimeout` stops a start limit
  that has not released in time with `DepartureLimitNotReleased`.
- Fault latch — watchdog expiry, drive fault (IN3), relay verification failure, input
  read failure, contradictory limits, a reasserted start limit, the drive-running check
  (`DriveNotRunning`) and an unreleased start limit latch the fault.
  Open/Close are refused (`FaultLatched`) until a successful `ClearFault`. The latch
  never blocks Stop, and Stop does not clear it.
- Relay register read-back — every relay transition is verified by reading the HAT's
  relay register back. This proves the register, not the physical contacts; never
  describe it as contact verification. An unverified stop is reported as a failure
  (`RelayStateUnverified`), never as success.
- VFD fault input (IN3) — polarity is `FaultInputActiveHigh` (code default `true`: raw HIGH
  = fault; production `false` for the `P140 = 3` fault relay, closed while healthy).
- Drive-running interlock (IN4) — with `AtSpeedConfirmationTimeout` set (production 3 s), a
  start is refused with `InterlockActive` while IN4 reports running, IN4 must assert within
  the window after a start, and IN4 low for 250 ms while moving without the destination
  limit stops the roof; the last two latch `DriveNotRunning`. IN4 still high
  `DriveStopConfirmationTimeout` after a stop is logged as Critical.
- Input read failures — `MaxConsecutiveInputReadFailures` consecutive failed reads while
  moving stop the roof with `InputReadFailure`.
- Relay register reads — supervision re-reads the register. One failed or stale read
  marks relay reads unhealthy (readiness fails; the last verified state is kept); two
  consecutive failures stop motion and latch `RelayVerificationFailed`.
- Repeated commands — a repeated same-direction Open/Close and `RenewLease` enforce the
  watchdog, lease and at-speed deadlines before renewing; a repeat never revives an
  expired lease.
- Shutdown — the host requests a verified stop. An unverified one is retried (all-off
  every 500 ms until verified, disposal or 15 s); the host's wait is bounded even if HAT
  I/O blocks, and disposal stops waiting for a lock held by blocked HAT I/O after 2 s,
  deferring its all-off stop until that call returns.
- `IgnorePhysicalLimitSwitches` is for local simulation. On physical hardware it is refused
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
  HVO.RoofControllerV4.Common/      # Shared models
  HVO.RoofControllerV4.Client/      # Client library: REST API, status hub, credentials, Stop, shared wording
  HVO.RoofControllerV4.Simulation/  # Emulated drive, limit switches, HAT and wiring (tests and the emulator)
  HVO.RoofControllerV4.Emulator/    # HAT emulator: the emulated plant's HAT registers over TCP, control API
  HVO.WebSite.Themes/               # CSS theme RCL
  HVO.RoofController.sln            # Solution file
tests/
  HVO.RoofControllerV4.RPi.Tests/   # Unit, API, simulation and emulated-plant tests (MSTest)
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
- No test may rely on physical hardware. Model emulated devices on the vendor
  documentation, and record what software cannot prove as a documented assumption

## Build and Test

Run `dotnet` from `src/` so `src/global.json` selects the SDK:

```bash
cd src
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
# Release treats warnings as errors; CI builds both configurations
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj -c Release
```

The full solution builds on Linux. Use the RPi test project for focused safety checks.

## Deployment

The RPi server runs in Docker on the Pi (`linux-arm64`). With HTTPS only port 8443 is
published and plain HTTP (8080) listens on loopback inside the container; plain HTTP on the
LAN is an explicit opt-out (`ALLOW_INSECURE_HTTP=true`, compose profile `pi-lan-http`).
Deploy with `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh`: it runs the image's
`--validate-deployment` check first, requires a verified Stop before replacing the
container, verifies the new one from the deploying machine and restores
`roof-controller-previous` on any failure, signal or lost terminal after the stop begins
(removing only the container it created); see `docs/deployment.md`. The production compose file
is `src/HVO.RoofControllerV4.RPi/docker-compose.yaml` (Pi profiles `pi` and `pi-lan-http`,
which run the deployment check first, and the test-rig profile `emulator`, which runs the
production settings against the HAT emulator container with no devices, on loopback).
Keep the script, its tests (`tests/deploy/deploy-script-tests.sh`) and the compose file in
step. HAT emulator mode (`HatEmulator:*`) is described in `docs/emulator.md`; the deploy
script sets it only through `HAT_EMULATOR_ENDPOINT` with `ALLOW_EMULATED_HAT=true`.

`src/docker-compose.yml` is the local development compose: the controller in the
Development environment against the HAT emulator container, no HAT devices, port 5200.
Never use it on the observatory Pi. `dotnet run` uses port 5195 and expects the emulator on
port 5291 (`dotnet run --project src/HVO.RoofControllerV4.Emulator`).

## CI/CD

- `ci.yml` — ubuntu; from `src/`, checks the dev container SDK against `global.json`,
  restores and builds `HVO.RoofController.sln` (Debug), runs the RPi tests, checks that
  coverage is not empty, then builds the solution in Release with warnings as errors. A
  second job (`deploy-script`) runs `bash -n` and ShellCheck on the deploy script, its tests
  against fake `docker`/`curl`, and `docker compose config` for the Pi compose file's
  profiles (and that `pi` with `pi-lan-http` is rejected) and for `src/docker-compose.yml`.
- `pi-image.yml` — builds the Pi image for `linux/arm64` (no push) and checks that the
  Dockerfile SDK tag matches `src/global.json`.
- `emulator-image.yml` — builds the HAT emulator image for `linux/amd64` and `linux/arm64`
  (no push), checks its SDK tag against `src/global.json` and its runtime tag against the Pi
  image, and runs `tests/emulator/compose-smoke-test.sh` (the compose `emulator` profile
  opens and closes the emulated roof).

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
