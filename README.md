# HVO.RoofController

[![CI](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/ci.yml/badge.svg)](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/ci.yml)
[![Pi image](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/pi-image.yml/badge.svg)](https://github.com/HualapaiValley/HVO.RoofController/actions/workflows/pi-image.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-Proprietary-red)

Roof Controller V4 for the HVO observatory: a Raspberry Pi controller with an
authenticated HTTP API, and its clients: a web UI, a command line and a terminal interface.

## Features

- **Roof Automation** — open/close/stop control of the observatory roll-off roof through a
  VFD driven by a Sequent Microsystems 4-relay/4-input HAT
- **Safety Systems** — see [Safety behavior](#safety-behavior); these are software
  controls and do not replace an independent hardware stop path
- **GPIO / I2C** — direct hardware interface on Raspberry Pi for motor and sensor management
- **Authenticated API** — every protected request needs an `X-Api-Key` or a person's session
  token; commands are `POST` only ([docs/security.md](docs/security.md))
- **People and sessions** — people sign in with a name and password, or a PIN at a kiosk;
  admins manage people, API keys and sessions through the API
  ([People, sessions and managed API keys](docs/security.md#people-sessions-and-managed-api-keys))
- **Remote settings** — every setting is described once in a catalogue and changed through the
  API by role (operators change the display settings, admins the rest), saved to a settings file
  on the Pi that can also be edited by hand; admins can restart the controller after a verified
  stop ([Settings](docs/security.md#settings))
- **Live status hub** — UI clients receive every status change, and a heartbeat each second,
  from the SignalR hub at `/hubs/roof` ([Status hub](docs/security.md#status-hub))
- **Web UI** — browser-based roof control for desktop, tablet and phone, a client of the
  controller's API and status hub in a process of its own
  ([docs/web.md](docs/web.md)); browser tests run it in Chromium with phone, tablet and
  desktop emulation against the emulated roof, including Stop from the reconnect dialog
  (commissioning C15)
- **Command line and terminal interface** — `hvo-roof`, one self-contained file for the Pi or
  a workstation: every API operation as a command with `--json` and documented exit codes,
  and `hvo-roof ui`, a full-screen terminal interface with Stop on F9 from every page
  ([docs/cli.md](docs/cli.md))
- **Docker Deployment** — containerized deployment to Raspberry Pi (linux-arm64) through a
  fail-closed deployment script ([docs/deployment.md](docs/deployment.md))

## Safety behavior

- **Maximum-run watchdog** (`SafetyWatchdogTimeout`, 5–600 s): an absolute cap on each
  movement, measured from its start. Repeating Open or Close in the same direction does
  not extend it. Expiry stops the roof and latches a fault. This is not a dead-man control.
- **Operator lease** (optional, `OperatorLeaseTimeout`, 2–120 s, off when unset): Open and
  Close start a lease that the client renews (`POST .../Lease`) while the roof moves. If the
  client stops renewing, the roof stops with `OperatorLeaseExpired`. The web UI renews
  only while its browser connection is up: a closed tab stops renewal at once, a silent drop
  such as lost Wi-Fi within about 30 s, and renewal does not resume on reconnect. Without a
  lease, losing the client does not stop motion; the watchdog and limit switches still do.
- **Stop without the connection**: the web UI's Stop bar and its reconnect dialog's Stop
  post a plain request (`POST /stop`), not through the page's live connection, so Stop works
  while the page is disconnected as long as the web UI and the controller are reachable.
- **Fault latch**: watchdog expiry, a VFD fault (IN3), a relay verification failure, repeated
  input read failures, contradictory limit inputs, a reasserted start limit, a failed
  drive-running check (IN4) and an unreleased start limit latch the fault. Open and Close are refused until an explicit `ClearFault` succeeds with healthy
  inputs. Stop is never blocked by the latch and does not clear it.
- **Relay register read-back**: after every relay change the controller reads the HAT's
  relay register back and reports `Verified` or `Unverified`. This proves what the HAT
  register holds, not that the relay contacts opened; the emulated plant's dead- and
  welded-contact tests show what the controller does when they differ. A stop that cannot
  be verified is reported as a failure.
- **Limit switches** (IN1/IN2, polarity `UseNormallyClosedLimitSwitches`, `false` in
  production because IN1/IN2 are on the ME-8108 normally open pair): the roof stops at the
  destination limit; leaving a limit is handled as a departure phase, and both limits
  active at once stops motion and refuses starts.
- **VFD fault input** (IN3): `FaultInputActiveHigh` is `false` in production, because the
  drive's fault relay (`P140 = 3`) holds IN3 HIGH while the drive is healthy. The code
  default stays `true` (raw HIGH = fault).
- **Drive-running interlock** (IN4, the drive's run output; `AtSpeedConfirmationTimeout`,
  3 s in production, off when unset): a start is refused with `InterlockActive` while the
  drive still reports running. After a ramp stop a reversal is refused until the drive
  has stopped; with the coast stop IN4 drops within milliseconds, so a reversal proceeds and the
  drive starts the other way while the roof is still coasting (a known limitation). The drive
  must report running within the window after a start, and IN4 dropping for 250 ms while
  moving without the destination limit (a trip, an external stop, drive power loss) stops
  the roof; both latch `DriveNotRunning`. IN4 still HIGH after a stop is logged as
  Critical once `DriveStopConfirmationTimeout` (default: the same window) has passed.
- **Departure-release timeout** (optional, `DepartureReleaseTimeout`, off when unset): a
  start limit that has not released within the window stops the roof with
  `DepartureLimitNotReleased`. It catches a jammed roof or swapped motor leads before the
  roof reaches the hard stop behind the limit. Set it from the release time with the
  installed acceleration (`P104`); the emulated plant gives that time
  (`PlantDocumentedFiguresTests`).
- **Input read failures**: `MaxConsecutiveInputReadFailures` (default 3) consecutive failed
  reads while moving stop the roof with `InputReadFailure`.
- `IgnorePhysicalLimitSwitches` is for simulation; on physical hardware it is refused
  unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local configuration.

The wiring these settings assume, and the settings' limits (stopping distance, `P104` and
`P105`), are in the
[hardware overview](docs/projects/roof-controller-v4-rpi/hardware-overview.md). Tests run
the controller against an emulated roof, drive, limit switches and HAT built from the
vendor documentation (`HVO.RoofControllerV4.Simulation`), including wrong-wiring and
wrong-setting variants. The commissioning checks in
[docs/commissioning.md](docs/commissioning.md) run as scenarios against that emulated roof in
CI, and each check lists the installation assumptions the emulator cannot prove.

## Projects

| Project | Description |
|---------|-------------|
| `HVO.RoofControllerV4.RPi` | ASP.NET Core web app for Raspberry Pi (GPIO/I2C roof control) |
| `HVO.RoofControllerV4.Common` | Shared models and options |
| `HVO.RoofControllerV4.Client` | Client library for the REST API and the status hub, shared by every UI client ([its README](src/HVO.RoofControllerV4.Client/README.md)) |
| `HVO.RoofControllerV4.Cli` | `hvo-roof`: the command line and terminal interface, over the client library ([docs/cli.md](docs/cli.md)) |
| `HVO.RoofControllerV4.Simulation` | Emulated roof plant: SMVector drive, ME-8108 limit switches, SM-I-010 HAT and the wiring |
| `HVO.RoofControllerV4.Emulator` | HAT emulator: serves the emulated plant's HAT registers over TCP, with a fault-injection API ([docs/emulator.md](docs/emulator.md)) |
| `HVO.WebSite.Themes` | CSS theme (Razor Class Library) |
| `HVO.RoofControllerV4.RPi.Tests` | Unit, API, simulation and emulated-plant tests (`tests/`) |

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

# Run the server against the HAT emulator on http://localhost:5195: start the emulator first,
# then the server in a second terminal (Development uses the emulator, limit switches in force)
dotnet run --project HVO.RoofControllerV4.Emulator
dotnet run --project HVO.RoofControllerV4.RPi
```

The solution builds on Linux without Apple tooling. Nothing in development or the tests
needs the HAT, the drive or the roof; see [docs/emulator.md](docs/emulator.md).

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
(`pi-lan-http`, port 8080); both run the deployment check before the controller starts. Its
`emulator` profile runs the production settings against the HAT emulator container on any
machine, for testing. See [docs/deployment.md](docs/deployment.md) for the full procedure,
including the operator API key, TLS, the identity store directory and the settings
directories.

For local development only (Development environment, against the HAT emulator container, no
devices, on `http://localhost:5200`):

```bash
cd src
HVO_DEV_ROOF_API_KEY=$(openssl rand -hex 24) docker compose up --build
```

Never run `src/docker-compose.yml` on the observatory Pi.

## Dev Container

This repository includes Dev Container configurations in `.devcontainer/` for
a consistent development environment. Open in VS Code and use
**Reopen in Container** to get started. `devcontainer.rpi.json` gives the container the
Pi's real I2C bus and GPIO and keeps the physical limit switches in force; use it only
with the roof mechanism isolated.

## Documentation

| Document | Description |
|----------|-------------|
| [Roof controller hardware overview](docs/projects/roof-controller-v4-rpi/hardware-overview.md) | Canonical SMVector, relay, limit-switch, monitoring, and safety wiring reference |
| [Commissioning](docs/commissioning.md) | Checks C1-C15 as automated scenarios against the emulated plant, the installation assumptions each depends on, and the settings file on the Pi (hand edits, backup) |
| [Security](docs/security.md) | API keys, roles, people and sessions, remote settings and restart, HTTPS and the web UI's sign-in |
| [Web UI](docs/web.md) | The browser interface: signing in, the pages, Stop, its settings and screenshots |
| [Deployment](docs/deployment.md) | Pi deployment script and compose, deployment check, verified stop, rollback |
| [Command line](docs/cli.md) | `hvo-roof`: install, setup and credentials, the commands, exit codes and the terminal interface |
| [HAT emulator](docs/emulator.md) | Running the controller without hardware: the emulator, its settings and fault-injection API, containers |
| [CI runners](docs/ci-runners.md) | Active CI workflows and runner security settings |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guidelines and development workflow |
| [CHANGELOG.md](CHANGELOG.md) | Version history and release notes |
| [copilot-instructions.md](.github/copilot-instructions.md) | Architecture and coding standards |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards,
and pull request guidelines.

## License

This project is proprietary software. See [LICENSE](LICENSE) for details.
