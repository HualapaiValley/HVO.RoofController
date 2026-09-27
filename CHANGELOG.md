# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Retired client

- Retired the native iPad/MAUI app and its iOS/M5 build workflows. The authenticated web
  console is the supported operator client; tablet and phone use require the browser checks
  tracked in #20 and #26. Server API and physical safety commissioning remain in #24.
- Updated the pinned SDK image and `src/global.json` together to 10.0.401 (Dependabot #25).

The August 2026 architecture review led to safety, security and operations changes
(issues #16-#22). The hardware checks in [docs/commissioning.md](docs/commissioning.md)
are still open; do not treat this release as validated on the roof until they pass.

### BREAKING

Upgrade the controller and API automation scripts together. Older clients cannot operate
the roof against this server; use the authenticated browser console for operator access.

- **API keys required.** Every protected endpoint needs an `X-Api-Key` header with a key
  from `RoofControllerSecurity:ApiKeys` (roles `RoofViewer`, `RoofOperator`, `RoofAdmin`).
  Keys come from environment variables or Docker secrets, never committed settings. With no
  keys configured, protected endpoints return 401 and the controller logs a Critical message
  at startup. The web console now has a sign-in page. See [docs/security.md](docs/security.md).
- **Commands are POST only.** `Open`, `Close`, `Stop`, `ClearFault` and the new `Lease` under
  `api/v4.0/RoofControl` accept only `POST`; the old `GET` command routes return 405. Stop
  needs any authenticated role (anonymous Stop only with `AllowAnonymousStop=true`); Open,
  Close, ClearFault and Lease need `RoofOperator`.
- **Error responses** are RFC 7807 ProblemDetails with `code` and `roofStatus` extensions
  (for example 409 `FaultLatched`, 409 `LeaseNotActive`, 503 `RelayStateUnverified`).
- **Configuration updates** (`POST .../Configuration`, `RoofAdmin` only) must send every
  field, plus `ExpectedVersion` from the last `GET` (a stale version returns 409
  `ConfigurationVersionConflict`). Changes to relay mapping, limit or fault polarity, or
  `IgnorePhysicalLimitSwitches` also need `ConfirmSafetyCriticalChange: true`. New fields:
  `FaultInputActiveHigh`, `MaxConsecutiveInputReadFailures`, `OperatorLeaseTimeoutSeconds`,
  `AtSpeedConfirmationTimeoutSeconds`.
- **HTTPS by default outside Development.** `RoofControllerSecurity:RequireHttps` defaults to
  true outside Development; plain-HTTP requests to protected endpoints get 403 except from
  loopback and for `/health/live` and `/health/ready`. Provide a certificate or opt out
  explicitly (the controller logs a warning). `/health` now needs a Viewer key; `/health/live`
  and `/health/ready` stay anonymous.
- **Limit override on hardware.** `IgnorePhysicalLimitSwitches` is refused on physical
  hardware unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local
  configuration (it cannot be set through the API).
- **Camera.** Blue Iris credentials come from `BlueIris:UserName` and `BlueIris:Password`
  (environment variables or Docker secrets); the hard-coded credential was removed and must be
  rotated because it remains in the git history. `GET api/v1.0/Camera/{id}/mjpeg` needs a
  Viewer key, the console sign-in, or a ticket from `POST api/v1.0/Camera/{id}/ticket`.
- **Deployment.** `deploy-roofcontroller-rpi.sh` needs the operator key
  (`ROOF_OPERATOR_API_KEY` or `~/.config/hvo-roof/operator.key`) and aborts unless the Stop it
  sends is verified. See [docs/deployment.md](docs/deployment.md).

### Added

- Fault latch: watchdog expiry, VFD fault (IN3), relay verification failure, repeated input
  read failures, contradictory limits and a reasserted start limit latch a fault that blocks
  Open and Close until `ClearFault` succeeds with healthy inputs. Stop is never blocked.
- Relay register read-back after every relay change (`relayRegisterState`,
  `relayRegisterMask`); an unverified stop is reported as a failure.
- Optional operator lease (`OperatorLeaseTimeout`, renewed with `POST .../Lease`), optional
  drive-running interlock on IN4 (`AtSpeedConfirmationTimeout`), configurable IN3 polarity
  (`FaultInputActiveHigh`, default unchanged) and `MaxConsecutiveInputReadFailures`.
- Status fields: `statusVersion`, `commandedMotion`, fault latch, input health, lease and
  controller identity.
- The roof stops with reason `HostShutdown` when the container or host shuts down.
- Short-lived camera stream tickets (`POST api/v1.0/Camera/{id}/ticket`).
- `pi-image.yml`: builds the `linux/arm64` Pi image in CI.
- [docs/commissioning.md](docs/commissioning.md) (bench checklist, including the RV-5
  telemetry-outage and RV-6 soak procedures) and [docs/ci-runners.md](docs/ci-runners.md).
- Root `.dockerignore` for the repository-root build context.

### Changed

- The safety watchdog is an absolute cap on each movement; repeating a command no longer
  extends it.
- CI: all actions pinned to commit SHAs, read-only token permissions, `dotnet` run from `src/`
  so `src/global.json` applies, a Release build with warnings as errors, and a coverage filter
  that matches the project assemblies.
- The Pi Dockerfile pins the SDK to `src/global.json` and the runtime to a patch version, and
  cross-compiles on the build platform.
- Documentation describes the implemented safety behavior, API, ports and deployment path.
  The wiring package zip is rebuilt from `hardware-overview.md`.
- Removed `.LocalPackages` directory — all HVO packages now sourced from nuget.org
- Removed `LocalPackages` NuGet source from `NuGet.config`
- Removed `.LocalPackages` COPY from Dockerfile
- Repository documentation standardization

## [1.0.0] - 2025-03-01

### Added

- Initial extraction from HVOv9 monorepo
- RPi controller (ASP.NET Core + Blazor Server with GPIO/I2C roof control)
- iPad .NET MAUI client
- Shared Common models and options library
- CI/CD workflows for tests and the Pi image (the iOS workflow was retired later)
- Dev Container setup for consistent development environment
- Docker deployment configuration for Raspberry Pi (linux-arm64)
