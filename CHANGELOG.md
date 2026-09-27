# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

Safety, security and operations fixes from the August 2026 architecture review
(issues #16-#22). The hardware checks in [docs/commissioning.md](docs/commissioning.md) are
still open; do not treat this release as validated on the roof until they pass.

### BREAKING

Upgrade the controller and API automation scripts together. Older clients cannot operate
the roof against this server; use the authenticated browser console for operator access.

- **iPad app retired.** The native iPad app is retired; operators must use the authenticated
  web console. Tablet and phone use of the console still needs the browser checks tracked in
  #20 and #26.
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
  Viewer key or the console sign-in.
- **Deployment.** `deploy-roofcontroller-rpi.sh` needs the operator key
  (`ROOF_OPERATOR_API_KEY` or `~/.config/hvo-roof/operator.key`) and aborts unless the Stop it
  sends is verified. It needs `HTTPS_CERT_DIR` (or `ALLOW_INSECURE_HTTP=true`); with HTTPS it
  publishes only port 8443 and plain HTTP stays on loopback inside the container, so automation
  that used `http://<pi>:8080` must move to `https://<pi>:8443`. The compose `pi` profile
  likewise needs a certificate and publishes only 8443; the new `pi-lan-http` profile is the
  explicit plain-HTTP opt-out. See [docs/deployment.md](docs/deployment.md).

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
- Web console: the reconnect dialog has a Stop button that works while the console's
  connection is down (`POST /console/stop`, console sign-in plus the page's antiforgery
  token), and the console stops renewing the operator lease as soon as its connection drops.
  Renewal does not resume on reconnect.
- `pi-image.yml`: builds the `linux/arm64` Pi image in CI.
- [docs/commissioning.md](docs/commissioning.md) (bench checklist, including the RV-5
  telemetry-outage and RV-6 soak procedures) and [docs/ci-runners.md](docs/ci-runners.md).
- Root `.dockerignore` for the repository-root build context.
- Deployment check: `dotnet HVO.RoofControllerV4.RPi.dll --validate-deployment` validates the
  configuration without starting the host or touching the HAT: the roof options (including the
  limit-switch override on the roof hardware), the other options sections, the API keys and the
  deploying key's role, the listeners Kestrel would actually use, each HTTPS listener's
  certificate (loaded as Kestrel loads it, with the Server Authentication usage) and that
  `AllowedHosts` includes `localhost`. The deploy script runs it with the final container's
  configuration before stopping anything; both compose profiles run it before the controller
  starts.
- The deploy script verifies the new controller from the deploying machine (authenticated
  Status and a verified Stop at the published URL, `REMOTE_CA_CERT` for a private CA), keeps
  the old container as `roof-controller-previous` and rolls back to it when the new one fails.
  `--rollback` swaps them on demand.
- The deploy script checks its settings before contacting Docker (whole decimal numbers, the
  poll interval, `EXTRA_DOCKER_ARGS`, which may not set the name, detach, removal, restart
  policy, cidfile or ports, and the HTTPS choice, now also for `--rollback`). It stops with
  nothing changed when Docker cannot report the containers' state, or when
  `roof-controller-previous` is running, restarting or paused. Once the old controller's stop
  begins, any failure, signal or lost terminal restores it; only the container the run created
  is removed. An older `roof-controller-previous` is removed only after the stop succeeds.
  `--rollback` undoes a failed or interrupted swap and refuses to run while
  `roof-controller-swap` exists. Needs Docker CLI 20.10 or later.
- Relay register read supervision: `relayRegisterReadsHealthy`, `lastSuccessfulRelayReadUtc`
  and `consecutiveRelayReadFailures` in Status. One failed or stale read fails readiness; two
  consecutive failures stop motion and latch `RelayVerificationFailed`.
- An unverified shutdown stop is retried (all-off every 500 ms until it verifies, the controller
  is disposed or 15 s pass), and the host's shutdown wait is bounded even if a HAT call blocks
  (it logs Critical and moves on). While that call is still blocked, later shutdown triggers do
  not queue another one, and disposal gives up on the controller lock after 2 s.
- CI job `deploy-script`: ShellCheck and tests for the deploy script against fake
  `docker`/`curl`, and `docker compose config` for both profiles.

### Changed

- The safety watchdog is an absolute cap on each movement; repeating a command no longer
  extends it. A repeated Open/Close or a lease renewal first enforces the watchdog, lease and
  at-speed deadlines, so it cannot revive an expired lease.
- CI: all actions pinned to commit SHAs, read-only token permissions, `dotnet` run from `src/`
  so `src/global.json` applies, the whole solution built in Debug and in Release with warnings
  as errors, a coverage filter that matches the project assemblies, and a check that the dev
  container SDK satisfies `src/global.json`.
- Updated the pinned SDK image, the dev container image and `src/global.json` together to
  10.0.401 (Dependabot #25).
- The Pi Dockerfile pins the SDK to `src/global.json` and the runtime to a patch version, and
  cross-compiles on the build platform.
- Documentation describes the implemented safety behavior, API, ports and deployment path.
  The wiring package zip is rebuilt from `hardware-overview.md`.
- Removed `.LocalPackages` directory — all HVO packages now sourced from nuget.org
- Removed `LocalPackages` NuGet source from `NuGet.config`
- Removed `.LocalPackages` COPY from Dockerfile
- Repository documentation standardization

### Removed

- The native iPad/MAUI app and its iOS and self-hosted M5 build workflows. The authenticated
  web console is the supported operator client. Server API and physical safety commissioning
  remain in #24.

## [1.0.0] - 2025-03-01

### Added

- Initial extraction from HVOv9 monorepo
- RPi controller (ASP.NET Core + Blazor Server with GPIO/I2C roof control)
- iPad .NET MAUI client
- Shared Common models and options library
- CI/CD workflows (ci.yml for tests, ios.yml for iPad build)
- Dev Container setup for consistent development environment
- Docker deployment configuration for Raspberry Pi (linux-arm64)
