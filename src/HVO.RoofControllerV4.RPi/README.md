# HVO Roof Controller V4

A .NET 9 Blazor Server + ASP.NET Core application that automates the Hualapai Valley Observatory roof. It drives a Lenze SMVector VFD through the Sequent Microsystems 4-Relay/4-Input HAT, exposing a safety-focused REST API, background watchdog service, and modern control UI.

## Highlights
- Safety-first motion sequencing (STOP-first relay logic, watchdog timers, limit switch handling)
- Blazor Server UI with real-time status pill, notifications, and watchdog visuals
- REST API with versioned routing (`/api/v4.0/RoofControl`) for open/close/stop/fault-clear operations
- Structured logging and health checks for observability and rapid diagnostics
- Configurable hardware abstractions, including development bypass for disconnected limit wiring

## Getting Started
1. Install the .NET 9 SDK (see `global.json` for the pinned version).
2. Restore and build:
   ```bash
   dotnet build src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj
   ```
3. Run the site (HTTP on :5136 by default):
   ```bash
   dotnet run --project src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj
   ```
4. Browse to `http://localhost:5136` for the Blazor UI or call the API endpoints under `/api/v4.0/RoofControl`.

## API testing with REST Client
- Install the VS Code REST Client extension (`humao.rest-client`).
- Launch the app locally via the `prepare:roofv4:debug` task or `dotnet run`.
- Open `HVO.RoofControllerV4.RPi.http` in this project and use the **Send Request** action on any request snippet to exercise status, configuration, motion, and fault-clear endpoints.

## Documentation
- [Hardware Overview](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring, relay/limit mappings, safety philosophy
- [API Reference](../../docs/projects/roof-controller-v4-rpi/api-reference.md) – REST endpoints, payloads, and health data
- [Operator Cheat Sheet](../../docs/projects/roof-controller-v4-rpi/operator-cheat-sheet.md) – quick reference for field operations
- [Troubleshooting Guide](../../docs/projects/roof-controller-v4-rpi/troubleshooting-guide.md) – symptom → diagnosis mapping
- [Logging Reference](../../docs/projects/roof-controller-v4-rpi/logging-reference.md) – structured logging templates and conventions
- Roof diagrams bundle: `../../docs/projects/roof-controller-v4-rpi/RoofController_Diagrams_2025-09-26.zip`

## Configuration Notes
- Operational settings live in `appsettings*.json` under `RoofControllerOptionsV4` and `RoofControllerHostOptionsV4`.
- `IgnorePhysicalLimitSwitches` is enabled in `appsettings.Development.json` to bypass missing limit wiring during local bench testing. Disable it for real hardware.
- `HardwareDetection` section in `appsettings*.json` can provide default values for `ForceRaspberryPi`, `ContainerRpiHint`, or `UseRealGpio`; these populate the matching environment variables when not already set.
- When running inside Docker on the Raspberry Pi, hardware detection now respects the environment variable overrides `HVO_FORCE_RASPBERRY_PI=true` (force hardware) and `HVO_CONTAINER_RPI_HINT="raspberrypi-5"` (optional hint when GPIO devices are not mounted by default).
- Health check tags: `roof` and `hardware`; see `/health`, `/health/ready`, and `/health/live` for monitoring.

## Testing
Run the dedicated test project to validate relay sequencing, watchdog behaviour, and idempotent command handling:
```bash
dotnet test src/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
```
