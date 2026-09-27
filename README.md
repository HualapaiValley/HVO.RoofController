# HVO.RoofController

[![CI](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/ci.yml/badge.svg)](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/ci.yml)
[![Pi image](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/pi-image.yml/badge.svg)](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/pi-image.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-Proprietary-red)

Roof Controller V4 for the HVO observatory: a Raspberry Pi controller with an
authenticated web console and HTTP API.

## Features

- **Roof Automation** — open/close/stop control of the observatory roll-off roof through a
  VFD driven by a Sequent Microsystems 4-relay/4-input HAT
- **Safety Systems** — see [Safety behavior](#safety-behavior); these are software
  controls and do not replace an independent hardware stop path
- **GPIO / I2C** — direct hardware interface on Raspberry Pi for motor and sensor management
- **Authenticated API** — every protected request needs an `X-Api-Key`; commands are `POST`
  only ([docs/security.md](docs/security.md))
- **Web Console** — browser-based roof control for desktop, tablet and phone;
  mobile usability and physical Stop behavior still require validation (issues #20 and #26;
  physical commissioning in #24)
- **Docker Deployment** — containerized deployment to Raspberry Pi (linux-arm64) through a
  fail-closed deployment script ([docs/deployment.md](docs/deployment.md))

## Safety behavior

- **Maximum-run watchdog** (`SafetyWatchdogTimeout`, 5–600 s): an absolute cap on each
  movement, measured from its start. Repeating Open or Close in the same direction does
  not extend it. Expiry stops the roof and latches a fault. This is not a dead-man control.
- **Operator lease** (optional, `OperatorLeaseTimeout`, 2–120 s, off when unset): Open and
  Close start a lease that the client renews (`POST .../Lease`) while the roof moves. If the
  client stops renewing, the roof stops with `OperatorLeaseExpired`. The web console renews
  only while its browser connection is up: a closed tab stops renewal at once, a silent drop
  such as lost Wi-Fi within about 30 s, and renewal does not resume on reconnect. Without a
  lease, losing the client does not stop motion; the watchdog and limit switches still do.
- **Stop without the connection**: the console's reconnect dialog has a Stop button that
  posts to the controller directly (`POST /console/stop`), so Stop works while the console
  is disconnected as long as the controller is reachable.
- **Fault latch**: watchdog expiry, a VFD fault (IN3), a relay verification failure, repeated
  input read failures, contradictory limit inputs and a reasserted start limit latch the
  fault. Open and Close are refused until an explicit `ClearFault` succeeds with healthy
  inputs. Stop is never blocked by the latch and does not clear it.
- **Relay register read-back**: after every relay change the controller reads the HAT's
  relay register back and reports `Verified` or `Unverified`. This proves what the HAT
  register holds, not that the relay contacts opened; commissioning checks the contacts
  with a meter. A stop that cannot be verified is reported as a failure.
- **Limit switches** (IN1/IN2, polarity `UseNormallyClosedLimitSwitches`): the roof stops at
  the destination limit; leaving a limit is handled as a departure phase, and both limits
  active at once stops motion and refuses starts.
- **VFD fault input** (IN3): `FaultInputActiveHigh` (default `true`, raw HIGH = fault) must
  be confirmed against the real wiring during commissioning.
- **Drive-running interlock** (IN4, optional): with `AtSpeedConfirmationTimeout` set, the
  drive must report running within that window after a start, otherwise the roof stops
  with `DriveNotRunning`.
- **Input read failures**: `MaxConsecutiveInputReadFailures` (default 3) consecutive failed
  reads while moving stop the roof with `InputReadFailure`.
- `IgnorePhysicalLimitSwitches` is for simulation; on physical hardware it is refused
  unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local configuration.

Before connecting the roof mechanism, complete the bench checklist in
[docs/commissioning.md](docs/commissioning.md).

## Projects

| Project | Description |
|---------|-------------|
| `HVO.RoofControllerV4.RPi` | ASP.NET Core web app for Raspberry Pi (GPIO/I2C roof control) |
| `HVO.RoofControllerV4.Common` | Shared models and options |
| `HVO.WebSite.Themes` | CSS theme (Razor Class Library) |
| `HVO.RoofControllerV4.RPi.Tests` | Unit and API tests (`tests/`) |

## Quick Start

```bash
# Run dotnet from src/ so src/global.json selects the SDK
cd src

# Build the whole solution (server, shared libraries and tests)
dotnet build HVO.RoofController.sln

# Test the server
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj

# Release build (warnings are errors)
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj -c Release

# Run the server with the simulated HAT on http://localhost:5195
dotnet run --project HVO.RoofControllerV4.RPi
```

The solution builds on Linux without Apple tooling.

## Docker Deployment

Deploy to the Pi with the deployment script,
`src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh`. It builds the `linux/arm64`
image and first runs the image's deployment check (`--validate-deployment`) on the Pi with
the final container's configuration: roof options, a usable operator key, the HTTPS
listener and certificate. It then sends Stop to the running controller and aborts unless the
response shows a verified all-off relay register and no commanded motion (an explicit, typed
operator override exists for emergencies). The old container is stopped gracefully and kept
as `roof-controller-previous`. The new one must become ready and answer an authenticated
Status and a verified Stop at its published HTTPS URL from the deploying machine. If it does
not, or anything fails or interrupts the script after the old controller's stop begins, the
script restores the previous controller. `--rollback` swaps them on demand.

The production compose file, `src/HVO.RoofControllerV4.RPi/docker-compose.yaml`, has an
HTTPS profile (`pi`, port 8443) and an explicit plain-HTTP profile for an isolated LAN
(`pi-lan-http`, port 8080); both run the deployment check before the controller starts.
See [docs/deployment.md](docs/deployment.md) for the full procedure, including the operator
API key and TLS.

For local simulation only (Development environment, no HAT devices, on
`http://localhost:5200`):

```bash
cd src
docker compose up --build
```

Never run `src/docker-compose.yml` on the observatory Pi.

## Dev Container

This repository includes Dev Container configurations in `.devcontainer/` for
a consistent development environment. Open in VS Code and use
**Reopen in Container** to get started. `devcontainer.rpi.json` gives the container the
Pi's real I2C bus and GPIO and keeps the physical limit switches in force; use it only on
a commissioned bench.

## Documentation

| Document | Description |
|----------|-------------|
| [Roof controller hardware overview](docs/projects/roof-controller-v4-rpi/hardware-overview.md) | Canonical SMVector, relay, limit-switch, monitoring, and safety wiring reference |
| [Commissioning checklist](docs/commissioning.md) | Bench and HAT checks required before connecting the roof mechanism |
| [Security](docs/security.md) | API keys, roles, HTTPS and console sign-in |
| [Deployment](docs/deployment.md) | Pi deployment script and compose, deployment check, verified stop, rollback |
| [CI runners](docs/ci-runners.md) | Active CI workflows and runner security settings |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guidelines and development workflow |
| [CHANGELOG.md](CHANGELOG.md) | Version history and release notes |
| [copilot-instructions.md](.github/copilot-instructions.md) | Architecture and coding standards |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards,
and pull request guidelines.

## License

This project is proprietary software. See [LICENSE](LICENSE) for details.
