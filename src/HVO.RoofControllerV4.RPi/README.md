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
- Configurable hardware abstractions; the limit-switch bypass is for simulation and is
  refused on physical hardware unless explicitly allowed in local configuration

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
4. Run the site (HTTP on port 5195 with the `Debug` launch profile; `Debug-Secure` adds
   `https://localhost:7296`; the container listens on 8080):
   ```bash
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
- [Commissioning checklist](../../docs/commissioning.md) – bench and HAT checks before connecting the roof
- [Security](../../docs/security.md) – API keys, roles, HTTPS, console sign-in
- [Deployment](../../docs/deployment.md) – deployment script, compose profiles, deployment check, verified stop and rollback

## Configuration Notes
- Operational settings live in `appsettings*.json` under `RoofControllerOptionsV4` and `RoofControllerHostOptionsV4`.
- Safety options in `RoofControllerOptionsV4` (bounds are enforced at startup and on every configuration change):
  - `SafetyWatchdogTimeout` (5–600 s): absolute cap on one movement; repeated same-direction commands do not extend it.
  - `OperatorLeaseTimeout` (2–120 s, unset = off): clients renew with `POST /api/v4.0/RoofControl/Lease` while moving; expiry stops the roof with `OperatorLeaseExpired`.
  - `FaultInputActiveHigh` (default `true`): IN3 polarity. Confirm against the real VFD wiring during commissioning; do not change it without re-running that check.
  - `AtSpeedConfirmationTimeout` (0.5–30 s, unset = off): IN4 must assert within this window after a start, otherwise the roof stops with `DriveNotRunning`.
  - `MaxConsecutiveInputReadFailures` (1–10, default 3): consecutive failed input reads while moving before the roof stops with `InputReadFailure`.
  - Relay register reads are supervised as well (not configurable): one failed or stale read sets `relayRegisterReadsHealthy = false` and fails `/health/ready`; two consecutive failures stop motion and latch `RelayVerificationFailed` (when idle, the all-off sequence is re-run first).
- On shutdown the roof is stopped and the relay register verified. If it cannot be verified, the all-off sequence is retried every 500 ms for up to 15 s; the host bounds its wait (`ShutdownTimeout`) and logs Critical if a HAT call is stuck.
  - `UseNormallyClosedLimitSwitches`: IN1/IN2 polarity.
- `IgnorePhysicalLimitSwitches` is for local simulation. On physical hardware the controller refuses it unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is `true` in local configuration; that setting cannot be changed through the API. `appsettings.Development.json` is for simulation, and `.devcontainer/devcontainer.rpi.json` forces `IgnorePhysicalLimitSwitches=false`.
- `POST /api/v4.0/RoofControl/Configuration` needs the `RoofAdmin` role and the `ExpectedVersion` returned by `GET Configuration`; safety-critical changes also need `ConfirmSafetyCriticalChange: true`.
- The latched fault is cleared only by `POST /api/v4.0/RoofControl/ClearFault` (optional `pulseMs`, 50–2000, default 250) with healthy inputs; Stop does not clear it.
- `HardwareDetection` section in `appsettings*.json` can provide default values for `ForceRaspberryPi`, `ContainerRpiHint`, or `UseRealGpio`; these populate the matching environment variables when not already set.
- When running inside Docker on the Raspberry Pi, hardware detection now respects the environment variable overrides `HVO_FORCE_RASPBERRY_PI=true` (force hardware) and `HVO_CONTAINER_RPI_HINT="raspberrypi-5"` (optional hint when GPIO devices are not mounted by default).
- Health check tags: `roof` and `hardware`. `/health/live` and `/health/ready` are anonymous with minimal bodies; `/health` needs the `RoofViewer` role and returns 503 with details when unhealthy.
- OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the production compose file supplies a default off-Pi collector. Use `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`; `OTEL_SERVICE_NAME` defaults to `hvo-roof-controller`, `OTEL_SERVICE_INSTANCE_ID` defaults to `roof-controller-rpi`, and `OTEL_METRIC_EXPORT_INTERVAL` defaults to `10000` milliseconds. The exporter captures ASP.NET Core and outbound HTTP telemetry, standard runtime metrics, roof command outcomes/durations, and safety-stop reasons. It never determines roof-control readiness.
- The shared collector prefixes roof metric names with `hvo_`. Safety stops are available through `hvo_roof_controller_safety_stops_total`, labelled with `roof_stop_reason` and `roof_stop_source`. Sources are `open-limit`, `closed-limit`, `fault`, `watchdog`, or `unknown`.
- Limit transitions use `hvo_roof_controller_limit_switch_events_total` with `roof_limit_switch` (`open` or `closed`) and `roof_limit_state` (`reached` or `cleared`). Current switch state is exported by `hvo_roof_controller_limit_switch_state` with `roof_limit_switch`; a value of `1` means the named switch is reached.
- Current fault, watchdog, drive, and controller state are exported as `hvo_roof_controller_fault_active`, `hvo_roof_controller_watchdog_active`, `hvo_roof_controller_watchdog_remaining_seconds`, `hvo_roof_controller_drive_at_speed`, and `hvo_roof_controller_status`. The status metric has the bounded `roof_status` label.

## Testing
Run the dedicated test project to validate relay sequencing, watchdog behaviour, and idempotent command handling:
```bash
cd src
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
```
The tests use a simulated HAT (`TestSupport/FakeRoofHat`). Behavior that depends on the real HAT, VFD and wiring is covered by [docs/commissioning.md](../../docs/commissioning.md).
