# HVO Roof Controller V4

A .NET 10 Blazor Server + ASP.NET Core application that automates the Hualapai Valley Observatory roof. It drives a Lenze SMVector VFD through the Sequent Microsystems 4-Relay/4-Input HAT, exposing a safety-focused REST API, background watchdog service, and modern control UI.

## Highlights
- Safety-first motion sequencing: STOP-first relay logic with relay register read-back, an
  absolute maximum-run watchdog, an optional renewable operator lease, limit switch
  handling (including departure from the start limit) and a fault latch that only
  `ClearFault` resets. See the root [README](../../README.md#safety-behavior) for the exact
  behavior and its limits.
- Blazor Server UI with real-time status pill, notifications, and watchdog visuals
- Authenticated REST API with versioned routing (`/api/v4.0/RoofControl`): `X-Api-Key`
  header, role-based access, `POST` for every command
- Structured logging and health checks for observability and rapid diagnostics
- Configurable hardware abstractions; the limit-switch bypass is for the in-memory register
  simulation and is refused on physical hardware and against the HAT emulator unless
  explicitly allowed in local configuration
- HAT emulator mode (`HatEmulator:Enabled`): the whole controller runs against an emulated
  roof, drive, limit switches and HAT, with no hardware ([docs/emulator.md](../../docs/emulator.md))

## Getting Started
1. Install the .NET 10 SDK pinned by `src/global.json`, and run `dotnet` from `src/` so the
   pin applies.
2. Restore and build:
   ```bash
   cd src
   dotnet build HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj
   ```
3. Provide at least one API key (see [docs/security.md](../../docs/security.md)); without
   keys every protected endpoint returns 401.
4. Start the HAT emulator, which Development uses in place of the HAT, then run the site in a
   second terminal (HTTP on port 5195 with the `Debug` launch profile; `Debug-Secure` adds
   `https://localhost:7296`; the container listens on 8080):
   ```bash
   dotnet run --project HVO.RoofControllerV4.Emulator
   dotnet run --project HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj
   ```
5. Browse to `http://localhost:5195` and sign in to the Blazor console, or call the API
   under `/api/v4.0/RoofControl` with an `X-Api-Key` header.

Deploy to the Pi with `deploy-roofcontroller-rpi.sh` (or a `docker-compose.yaml` profile); see
[docs/deployment.md](../../docs/deployment.md). Both first run the image with
`--validate-deployment`, which checks the configuration, keys and HTTPS certificate without
starting the host or touching the HAT.

## API testing with REST Client
- Install the VS Code REST Client extension (`humao.rest-client`).
- Launch the app locally with `dotnet run` (or the `watch` task when this folder is open in VS Code).
- Open `HVO.RoofControllerV4.RPi.http` in this project and use the **Send Request** action on any request snippet to exercise status, configuration, motion, and fault-clear endpoints. Commands are `POST` and need an `X-Api-Key` header.

## Documentation
- [Hardware Overview](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring, relay/limit mappings, safety philosophy
- [Wiring diagrams](../../docs/projects/roof-controller-v4-rpi/diagrams/) – source Graphviz DOT files, SVG diagrams, and PNG renderings for the hardware overview
- [Commissioning](../../docs/commissioning.md) – checks C1-C15 as automated scenarios against the emulated plant, with the installation assumptions each depends on
- [Security](../../docs/security.md) – API keys, roles, HTTPS, console sign-in
- [Deployment](../../docs/deployment.md) – deployment script, compose profiles, deployment check, verified stop and rollback
- [HAT emulator](../../docs/emulator.md) – running without hardware: the emulator, `HatEmulator` settings, fault injection, containers
- [Logging and telemetry](../../docs/telemetry.md) – log levels, OTLP export, roof metrics and the motion timing histograms

## Configuration Notes
- Operational settings live in `appsettings*.json` under `RoofControllerOptionsV4` and `RoofControllerHostOptionsV4`.
- Safety options in `RoofControllerOptionsV4` (bounds are enforced at startup and on every configuration change):
  - `SafetyWatchdogTimeout` (5–600 s): absolute cap on one movement; repeated same-direction commands do not extend it.
  - `OperatorLeaseTimeout` (2–120 s, unset = off): clients renew with `POST /api/v4.0/RoofControl/Lease` while moving; expiry stops the roof with `OperatorLeaseExpired`.
  - `FaultInputActiveHigh` (code default `true`, production `false`): IN3 polarity. The documented wiring feeds IN3 through the drive's fault relay (`P140 = 3`), which is closed while the drive is healthy, so production reads LOW as a fault. Keep it in step with the wiring.
  - `AtSpeedConfirmationTimeout` (0.5–30 s, unset = off, production 3 s): the drive run interlock on IN4 (`TB-14`, `P142 = 1`). A start is refused with `InterlockActive` while IN4 already reports running; IN4 must then rise within this window, and IN4 low for 250 ms after it confirmed, without the destination limit, stops the roof. Both latch `DriveNotRunning`.
  - `DriveStopConfirmationTimeout` (0.5–60 s, unset = `AtSpeedConfirmationTimeout`): IN4 still reporting running this long after a stop is logged as Critical once per stop. Set it longer than the drive's deceleration (`P105`) when a ramp stop (`P111` = 2 or 3) is used, plus the DC brake time (`P175`) with a DC brake (`P111` = 1 or 3), since the Run output may stay on while braking. Local configuration only; the configuration API reports it but never changes it.
  - `DepartureReleaseTimeout` (0.5–60 s, unset = off): a start limit that has not released, and stayed released for `LimitSwitchDebounce`, within this window stops the roof and latches `DepartureLimitNotReleased` (a jammed roof, or swapped motor leads driving it into the stop behind the limit). It must be shorter than `SafetyWatchdogTimeout` and at least `LimitSwitchDebounce` plus 0.5 s. Set it from the release time with the installed acceleration (`P104`); the emulated plant releases about 1.0 s after the command at `P104` = 2 s and about 2.8 s at 20 s. Local configuration only.
  - `MaxConsecutiveInputReadFailures` (1–10, default 3): consecutive failed input reads while moving before the roof stops with `InputReadFailure`.
  - Relay register reads are supervised as well (not configurable): one failed or stale read sets `relayRegisterReadsHealthy = false` and fails `/health/ready`; two consecutive failures stop motion and latch `RelayVerificationFailed` (when idle, the all-off sequence is re-run first).
  - `UseNormallyClosedLimitSwitches` (production `false`): IN1/IN2 polarity. IN1/IN2 are on the ME-8108 normally open pair (3-4), HIGH at the limit.
- On shutdown the roof is stopped and the relay register verified. If it cannot be verified, the all-off sequence is retried every 500 ms until it verifies, the controller is disposed or 15 s pass (disposal usually ends it after about 10 s). The host bounds each wait (5 s plus 1 s grace, up to three calls) and logs Critical if a HAT call is stuck; disposal then stops waiting for the controller lock after 2 s rather than block, and runs its all-off stop if the stuck call returns.
- `IgnorePhysicalLimitSwitches` is for the in-memory register simulation, which has no limit switches. On physical hardware, and against the HAT emulator, the controller refuses it unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is `true` in local configuration; that setting cannot be changed through the API. `appsettings.Development.json` runs against the HAT emulator with the limit switches in force and the production wiring, and `.devcontainer/devcontainer.rpi.json` uses the physical HAT (`HatEmulator__Enabled=false`) and forces `IgnorePhysicalLimitSwitches=false`.
- `HatEmulator` (`Enabled`, `Host`, `Port`, `ConnectTimeout`, `RequestTimeout`, `AllowOutsideDevelopment`): see [docs/emulator.md](../../docs/emulator.md). Outside Development, emulator mode is refused unless `AllowOutsideDevelopment` is `true`; a controller in emulator mode shows an `EMULATED HAT` banner and reports Degraded health, or worse, with every health description naming the emulator.
- `POST /api/v4.0/RoofControl/Configuration` needs the `RoofAdmin` role and the `ExpectedVersion` returned by `GET Configuration`. Every field except `ConfirmSafetyCriticalChange` must be sent; `OperatorLeaseTimeoutSeconds` and `AtSpeedConfirmationTimeoutSeconds` may be null, which turns them off. Safety-critical changes, including turning the lease or the IN4 interlock off, also need `ConfirmSafetyCriticalChange: true`.
- The latched fault is cleared only by `POST /api/v4.0/RoofControl/ClearFault` (optional `pulseMs`, 50–2000, default 250) with healthy inputs; Stop does not clear it.
- `HardwareDetection` section in `appsettings*.json` can provide default values for `ForceRaspberryPi`, `ContainerRpiHint`, or `UseRealGpio`; these populate the matching environment variables when not already set.
- When running inside Docker on the Raspberry Pi, hardware detection now respects the environment variable overrides `HVO_FORCE_RASPBERRY_PI=true` (force hardware) and `HVO_CONTAINER_RPI_HINT="raspberrypi-5"` (optional hint when GPIO devices are not mounted by default).
- Health check tags: `roof` and `hardware`. `/health/live` and `/health/ready` are anonymous with minimal bodies; `/health` needs the `RoofViewer` role and returns 503 with details when unhealthy.
- OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the production compose file supplies a default off-Pi collector. It never determines roof-control readiness. The [logging and telemetry reference](../../docs/telemetry.md) lists the settings, the log levels, and every roof metric, including the motion timing histograms (travel time by direction, the drive's IN4 start and stop delays, and the start limit's release) that set the timing options.

## Testing
Run the dedicated test project to validate relay sequencing, watchdog behaviour, and idempotent command handling:
```bash
cd src
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
```
Most tests use a scripted HAT (`TestSupport/FakeRoofHat`). The emulated-plant tests (`Plant/`) run this controller, through the real `FourRelayFourInputHat` library, against `HVO.RoofControllerV4.Simulation`: an SMVector drive, two ME-8108 limit switches, the SM-I-010 HAT and the documented wiring, modelled from the vendor documentation, with the production `appsettings.json`. What software cannot prove (wiring, contact movement, the hardwired stop path) is a documented assumption; see the [tests README](../../tests/HVO.RoofControllerV4.RPi.Tests/README.md) and [docs/commissioning.md](../../docs/commissioning.md).
