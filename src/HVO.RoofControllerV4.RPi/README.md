# HVO Roof Controller V4

A .NET 10 ASP.NET Core service that automates the Hualapai Valley Observatory roof. It drives a Lenze SMVector VFD through the Sequent Microsystems 4-Relay/4-Input HAT, exposing a safety-focused REST API, a live status hub and a background watchdog service. People use it through separate clients of that API: the web UI ([docs/web.md](../../docs/web.md)) and the terminal UI and CLI ([docs/cli.md](../../docs/cli.md)).

## Highlights
- Safety-first motion sequencing: STOP-first relay logic with relay register read-back, an
  absolute maximum-run watchdog, an optional renewable operator lease, limit switch
  handling (including departure from the start limit) and a fault latch that only
  `ClearFault` resets. See the root [README](../../README.md#safety-behavior) for the exact
  behavior and its limits.
- A SignalR status hub (`/hubs/roof`) that pushes the controller's full status to every client as it changes
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
5. Call the API under `/api/v4.0/RoofControl` with an `X-Api-Key` header, or run the web UI
   (`dotnet run --project HVO.RoofControllerV4.Web`, [docs/web.md](../../docs/web.md)) and
   browse to `http://localhost:5188`.

Deploy to the Pi with `deploy-roofcontroller-rpi.sh` (or a `docker-compose.yaml` profile); see
[docs/deployment.md](../../docs/deployment.md). Both first run the image with
`--validate-deployment`, which checks the configuration, keys and HTTPS certificate without
starting the host or touching the HAT.

In the container, the supervisor runs the image with `--relays-off` whenever the controller is not
running: it turns every relay off through the controller's HAT access and verifies the register,
without the controller's settings checks, and exits 0 only when the register reads back all off
([docs/deployment.md](../../docs/deployment.md#the-relays-when-the-controller-exits)). It reads the
configuration with the controller's own host builder; when a source other than a settings file of
the API's cannot be read, it cannot tell which HAT the controller uses, and writes nothing. Never run
it while a controller drives the HAT: it would stop that controller's move.

## API testing with REST Client
- Install the VS Code REST Client extension (`humao.rest-client`).
- Launch the app locally with `dotnet run` (or the `watch` task when this folder is open in VS Code).
- Open `HVO.RoofControllerV4.RPi.http` in this project and use the **Send Request** action on any request snippet to exercise status, configuration, motion, and fault-clear endpoints. Commands are `POST` and need an `X-Api-Key` header.

## Documentation
- [Hardware Overview](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring, relay/limit mappings, safety philosophy
- [Wiring diagrams](../../docs/projects/roof-controller-v4-rpi/diagrams/) – source Graphviz DOT files, SVG diagrams, and PNG renderings for the hardware overview
- [Commissioning](../../docs/commissioning.md) – checks C1-C15 as automated scenarios against the emulated plant, with the installation assumptions each depends on
- [Security](../../docs/security.md) – API keys, roles, sign-in, HTTPS
- [Web UI](../../docs/web.md) – the browser client, with its screenshots
- [Deployment](../../docs/deployment.md) – deployment script, compose profiles, deployment check, verified stop and rollback
- [HAT emulator](../../docs/emulator.md) – running without hardware: the emulator, `HatEmulator` settings, fault injection, containers
- [Logging and telemetry](../../docs/telemetry.md) – log levels, OTLP export, roof metrics and the motion timing histograms

## Configuration Notes
- Operational settings live in `appsettings*.json` under `RoofControllerOptionsV4` and `RoofControllerHostOptionsV4`. Settings changed through the API, or by hand on the Pi, are in the settings file above them (see [Settings](#settings)).
- Safety options in `RoofControllerOptionsV4` (bounds are enforced at startup and on every configuration change):
  - `SafetyWatchdogTimeout` (5–600 s): absolute cap on one movement; repeated same-direction commands do not extend it.
  - `OperatorLeaseTimeout` (2–120 s, unset = off): clients renew with `POST /api/v4.0/RoofControl/Lease` while moving; expiry stops the roof with `OperatorLeaseExpired`.
  - `FaultInputActiveHigh` (code default `true`, production `false`): IN3 polarity. The documented wiring feeds IN3 through the drive's fault relay (`P140 = 3`), which is closed while the drive is healthy, so production reads LOW as a fault. Keep it in step with the wiring.
  - `AtSpeedConfirmationTimeout` (0.5–30 s, unset = off, production 3 s): the drive run interlock on IN4 (`TB-14`, `P142 = 1`). A start is refused with `InterlockActive` while IN4 already reports running; IN4 must then rise within this window, and IN4 low for 250 ms after it confirmed, without the destination limit, stops the roof. Both latch `DriveNotRunning`.
  - `DriveStopConfirmationTimeout` (0.5–60 s, unset = `AtSpeedConfirmationTimeout`): IN4 still reporting running this long after a stop is logged as Critical once per stop. Set it longer than the drive's deceleration (`P105`) when a ramp stop (`P111` = 2 or 3) is used, plus the DC brake time (`P175`) with a DC brake (`P111` = 1 or 3), since the Run output may stay on while braking. Local-only: the configuration endpoint reports it but never changes it, and the settings API changes it only with a local credential (see [Settings](#settings)).
  - `DepartureReleaseTimeout` (0.5–60 s, unset = off): a start limit that has not released, and stayed released for `LimitSwitchDebounce`, within this window stops the roof and latches `DepartureLimitNotReleased` (a jammed roof, or swapped motor leads driving it into the stop behind the limit). It must be shorter than `SafetyWatchdogTimeout` and at least `LimitSwitchDebounce` plus 0.5 s. Set it from the release time with the installed acceleration (`P104`); the emulated plant releases about 1.0 s after the command at `P104` = 2 s and about 2.8 s at 20 s. Local-only, like `DriveStopConfirmationTimeout`.
  - `MaxConsecutiveInputReadFailures` (1–10, default 3): consecutive failed input reads while moving before the roof stops with `InputReadFailure`.
  - Relay register reads are supervised as well (not configurable): one failed or stale read sets `relayRegisterReadsHealthy = false` and fails `/health/ready`; two consecutive failures stop motion and latch `RelayVerificationFailed` (when idle, the all-off sequence is re-run first).
  - `UseNormallyClosedLimitSwitches` (production `false`): IN1/IN2 polarity. IN1/IN2 are on the ME-8108 normally open pair (3-4), HIGH at the limit.
- On shutdown the roof is stopped and the relay register verified. If it cannot be verified, the all-off sequence is retried every 500 ms until it verifies, the controller is disposed or 15 s pass (disposal usually ends it after about 10 s). The host bounds each wait (5 s plus 1 s grace, up to three calls) and logs Critical if a HAT call is stuck; disposal then stops waiting for the controller lock after 2 s rather than block, and runs its all-off stop if the stuck call returns.
- `IgnorePhysicalLimitSwitches` is for the in-memory register simulation, which has no limit switches. On physical hardware, and against the HAT emulator, the controller refuses it unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is `true` in local configuration; that setting is local-only, so the settings API changes it only with a local credential. `appsettings.Development.json` runs against the HAT emulator with the limit switches in force and the production wiring, and `.devcontainer/devcontainer.rpi.json` uses the physical HAT (`HatEmulator__Enabled=false`) and forces `IgnorePhysicalLimitSwitches=false`.
- `HatEmulator` (`Enabled`, `Host`, `Port`, `ConnectTimeout`, `RequestTimeout`, `AllowOutsideDevelopment`): see [docs/emulator.md](../../docs/emulator.md). Outside Development, emulator mode is refused unless `AllowOutsideDevelopment` is `true`; a controller in emulator mode shows an `EMULATED HAT` banner and reports Degraded health, or worse, with every health description naming the emulator.
- `POST /api/v4.0/RoofControl/Configuration` is the `roof` settings group under its old route: an accepted change is saved to the settings file, and its version is the settings version. It needs the `RoofAdmin` role and the `ExpectedVersion` returned by `GET Configuration`. Every field except `ConfirmSafetyCriticalChange` must be sent; `OperatorLeaseTimeoutSeconds` and `AtSpeedConfirmationTimeoutSeconds` may be null, which turns them off. Safety-critical changes, including turning the lease or the IN4 interlock off, also need `ConfirmSafetyCriticalChange: true`.
- `GET /api/v4.0/RoofControl/History` pages the roof command history (#206), newest first: every Open, Close, Stop and Clear fault sent to the API, accepted or refused, and refused lease renewals, with who sent it and the roof afterwards. Filters `user`, `principalId`, `from`, `to`, and `skip` and `take` (1–500, default 50). An admin reads everyone's; anyone else, a PIN session included, their own, matched by their identifier (never by name). The web UI's commands carry the person's browser address in `X-Forwarded-For`, believed only from `RoofCommandHistory:TrustedProxies` (the loopback by default); anonymous Stops from one source are one entry with a count, under a cap of their own. It is kept in `RoofCommandHistory:StorePath`, or `command-history.jsonl` next to the identity store, for `RoofCommandHistory:Retention` (default 90 days) and up to `RoofCommandHistory:MaximumEntries` (default 10,000); see [The command history](../../docs/security.md#the-command-history).
- The latched fault is cleared only by `POST /api/v4.0/RoofControl/ClearFault` (optional `pulseMs`, 50–2000, default 250) with healthy inputs; Stop does not clear it.
- `HardwareDetection` section in `appsettings*.json` can provide default values for `ForceRaspberryPi`, `ContainerRpiHint`, or `UseRealGpio`; these populate the matching environment variables when not already set.
- When running inside Docker on the Raspberry Pi, hardware detection now respects the environment variable overrides `HVO_FORCE_RASPBERRY_PI=true` (force hardware) and `HVO_CONTAINER_RPI_HINT="raspberrypi-5"` (optional hint when GPIO devices are not mounted by default).
- Deployment and Docker use anonymous `/health/available`: initialized software, healthy fresh I/O, verified relays and security checks. A safely stopped `DriveFault`, `DriveNotRunning`, `ContradictoryLimitInputs`, `StartLimitReasserted` or `DepartureLimitNotReleased` latch is Degraded (HTTP 200); the deploy keeps the controller and warns to repair the cause and explicitly `ClearFault`. Healthy motion is available too; all-off is the deploy Stop gate. `/health` and `/health/ready` still report plant faults Unhealthy.
- Operational health check tags: `roof` and `hardware`. `/health/live` and `/health/ready` are anonymous with minimal bodies; `/health` needs the `RoofViewer` role and returns 503 with details when unhealthy.
- OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the production compose file supplies a default off-Pi collector. It never determines roof-control readiness. The [logging and telemetry reference](../../docs/telemetry.md) lists the settings, the log levels, and every roof metric, including the motion timing histograms (travel time by direction, the drive's IN4 start and stop delays, and the start limit's release) that set the timing options.

## Settings
Remote configuration goes through `/api/v4.0/Settings`; [docs/security.md](../../docs/security.md#settings) has the full rules.
- `GET Settings/Catalogue` describes every setting once (group, type, range, default, the role that may change it, and whether it is safety-critical, local-only, a secret or needs a restart), so the CLI, the web UI and the kiosk build their forms from it. `GET Settings` returns the values in effect, where each comes from, and the version.
- `POST Settings/{group}` replaces one group: the `ui` group needs `RoofOperator`, the others (`roof`, `controller`, `camera`, `security`, `identity`, `logging`) `RoofAdmin`. Send `ExpectedVersion` and every setting of the group you may change; a safety-critical change also needs `ConfirmSafetyCriticalChange`. Roof settings do not change while the roof moves or a clear-fault pulse runs.
- Local-only settings (`AllowIgnoringLimitSwitchesOnPhysicalHardware`, `DriveStopConfirmationTimeout`, `DepartureReleaseTimeout`) need a local credential: an admin API key configured with `Local: true`, or an admin's PIN session at a kiosk whose key is `Local`. The check is on the credential, never the address.
- Secrets (the Blue Iris user and password) are write-only and kept in the managed secrets file (`RoofControllerSettings:SecretsFilePath`), never in the settings file.
- Changes are saved atomically to the settings file (`RoofControllerSettings:FilePath`; on the Pi `/etc/hvo-roof/config/appsettings.Local.json`), which holds only the values that differ from the shipped defaults and the version. Environment variables, the command line and the secrets directory override it, and the API cannot change a setting set there. A save rewrites the file without its comments.
- A hand edit to the file is shown as pending and blocks API changes until an admin reloads it (`POST Settings/Reload`, checked like an API change) or discards it (`POST Settings/Discard`). See [docs/commissioning.md](../../docs/commissioning.md#the-settings-file).
- A file the controller cannot use stops startup with `The roof controller did not start: ...` on standard error and exit code 1.
- `POST /api/v4.0/System/Restart` (`RoofAdmin`) stops the roof, verifies the stop (409 `RestartRefused` if it cannot), answers 202 and exits with code 75. In the container, the supervisor starts the controller again at once and leaves the web UI running (see [docs/deployment.md](../../docs/deployment.md)). Every change and restart is logged as an `AUDIT` entry naming the caller.

## Testing
Run the dedicated test project to validate relay sequencing, watchdog behaviour, and idempotent command handling:
```bash
cd src
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
```
Most tests use a scripted HAT (`TestSupport/FakeRoofHat`). The emulated-plant tests (`Plant/`) run this controller, through the real `FourRelayFourInputHat` library, against `HVO.RoofControllerV4.Simulation`: an SMVector drive, two ME-8108 limit switches, the SM-I-010 HAT and the documented wiring, modelled from the vendor documentation, with the production `appsettings.json`. What software cannot prove (wiring, contact movement, the hardwired stop path) is a documented assumption; see the [tests README](../../tests/HVO.RoofControllerV4.RPi.Tests/README.md) and [docs/commissioning.md](../../docs/commissioning.md).

## Optional Linux identity integration

Linux authentication is disabled by default. The installer enables it only with explicit Linux identity settings;
the controller options under RoofControllerSecurity:Identity:LinuxAuth are Enabled, SocketPath, AssertionKeyPath and
Audience. A persistent Identity:StorePath is required. Enabling the feature requires a controller restart.
The separate native worker owns PAM and fixed sudo-helper verification. The controller never reads the password
database or accepts a user-supplied UID as proof of Linux identity. Native enrollment and controller enrollment
must both exist. They bind a native subject identifier and enrollment stamp to a canonical account name;
operators must rotate the enrollment when replacing an account or reusing its UID.

The additive API (all routes under api/v4.0):

| Route | Purpose |
| --- | --- |
| GET Auth/Providers | Advertise enabled providers. |
| POST Auth/ProviderSession | Provider, name and password using virtual or linux; returns the existing session response. |
| POST Auth/LocalSession | Exchange an assertion from the native helper for a short operation-bound session. |
| GET/POST Auth/Keys, DELETE Auth/Keys/{id} | List, create and revoke the signed-in person's scoped keys. |
| GET/POST Identity/Principals, PUT Identity/Principals/{id} | Explicit Linux enrollment and role/enabled changes by an administrator. |

Legacy password sign-in, configured/managed keys, PINs and kiosk device binding keep their existing behavior.
Virtual and Linux identities with the same display name are different principals. Linux sessions cannot change
a same-named virtual user's password. Role or enabled changes invalidate provider sessions and owned keys by epoch.
Linux sessions and keys additionally recheck native enrollment and account status on each authenticated request;
an unavailable worker fails closed. Local sudo grants recheck account expiry without requiring a usable password,
so an SSH-key-only administrator can use the explicit local flow; password sessions and keys also require the
password account to remain enabled.

Personal keys have an owner, expiration (at most 366 days), scopes and revocation state. Read, operate and
configure scopes restrict access in addition to the owner's controller role. Personal keys cannot administer
identities or mint credentials; identity scope is reserved for fresh native interactive authority. A key cannot
be exchanged for a session or used to create more keys. Stop accepts any valid authenticated role independently
of scope, and the kiosk's existing fallback Stop credential remains unchanged.

Native assertions are HMAC-SHA256 signed, audience/purpose/provider/operation bound and valid for at most 30 seconds.
Their random nonce is consumed atomically with session creation and persisted, so concurrent exchange or a restart
does not permit replay. Local session tokens expire with the assertion, are restricted to its operation category,
and must never be saved by clients. Within that short lifetime they remain bearer credentials, including for a
live status connection; clients obtain a new native proof for each REST request. A local session cannot create
personal keys. The native worker/key and controller are trusted privileged components; this is not a boundary
against compromise of root or the controller itself.

With the feature enabled, startup upgrades schema 1 to schema 2 and preserves the original bytes in
the .schema1.bak sibling of the store without replacing an existing backup. Legacy passwords, PINs, device sessions
and keys are preserved. Schema 2 remains readable with the feature disabled, while provider entry points and
credentials are denied. Do not automatically restore the schema 1 backup during rollback: that can resurrect
revoked credentials. Roll back only to a compatible binary; an older schema 1 binary must refuse the newer store.
The backup is sensitive and receives owner-only permissions.

RoofProviderIdentityTests exercise production persistence, migration, signature rejection, replay concurrency,
provider isolation, HTTP role/scope enforcement and the Stop invariant with a fake roof service. They do not
stand in for native PAM/sudo or a real installation; the disposable installation acceptance suite supplies those
separate checks.

PIN sessions are limited to roof operation; settings writes and identity administration are refused. See [Authentication and interface rights](../../docs/authentication-interfaces.md) for enrollment and credential policies.

## Kiosk owner commissioning API

Eligible installations expose POST api/v4.0/Auth/KioskPin and POST api/v4.0/Auth/KioskPin/Complete.
Both require a current kiosk device key. Setup tokens are not ordinary bearer sessions. See
[first-install commissioning](../../docs/kiosk-commissioning.md) for the state machine and schema-3 recovery rules.
