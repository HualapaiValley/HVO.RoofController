# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

Safety, security and operations fixes from the August 2026 architecture review
(issues #16-#22). The commissioning checks in [docs/commissioning.md](docs/commissioning.md) run
as automated scenarios against the emulated plant (#31). Each check lists the installation
assumptions the emulator cannot prove, tied to the setting that depends on them; confirm them
against the installed drive and wiring before relying on the roof.

### BREAKING

Upgrade the controller and API automation scripts together. Older clients cannot operate
the roof against this server; use the web UI (port 8088) for operator access.

- **iPad app retired.** The native iPad app is retired; operators use the web UI (#46).
  Browser tests run it with phone, tablet and desktop emulation (#31, #46).
- **API keys required.** Every protected endpoint needs an `X-Api-Key` header with a key
  from `RoofControllerSecurity:ApiKeys` (roles `RoofViewer`, `RoofOperator`, `RoofAdmin`).
  Keys come from environment variables or Docker secrets, never committed settings. With no
  keys configured, protected endpoints return 401 and the controller logs a Critical message
  at startup. People sign in to the web UI (#46). See [docs/security.md](docs/security.md).
- **Commands are POST only.** `Open`, `Close`, `Stop`, `ClearFault` and the new `Lease` under
  `api/v4.0/RoofControl` accept only `POST`; the old `GET` command routes return 405. Stop
  needs any authenticated role (anonymous Stop only with `AllowAnonymousStop=true`); Open,
  Close, ClearFault and Lease need `RoofOperator`.
- **Error responses** are RFC 7807 ProblemDetails with `code` and `roofStatus` extensions
  (for example 409 `FaultLatched`, 409 `LeaseNotActive`, 503 `RelayStateUnverified`).
- **Configuration updates** (`POST .../Configuration`, `RoofAdmin` only) must send every
  field except `ConfirmSafetyCriticalChange`, with `ExpectedVersion` from the last `GET` (a
  stale version returns 409 `ConfigurationVersionConflict`). A missing field returns 400; only
  `OperatorLeaseTimeoutSeconds` and `AtSpeedConfirmationTimeoutSeconds` may be null, which
  turns them off (#18). Changes to relay mapping, limit or fault polarity, or
  `IgnorePhysicalLimitSwitches`, and turning off the operator lease or the IN4 interlock, also
  need `ConfirmSafetyCriticalChange: true`. New fields:
  `FaultInputActiveHigh`, `MaxConsecutiveInputReadFailures`, `OperatorLeaseTimeoutSeconds`,
  `AtSpeedConfirmationTimeoutSeconds`. The response also reports the local-only
  `DriveStopConfirmationTimeoutSeconds` and `DepartureReleaseTimeoutSeconds`.
- **Production configuration matches the documented wiring (#29).** `appsettings.json` now
  sets `UseNormallyClosedLimitSwitches = false` (IN1/IN2 on the ME-8108 normally open pair),
  `FaultInputActiveHigh = false` (IN3 on the drive's `P140 = 3` fault relay, closed while
  healthy) and `AtSpeedConfirmationTimeout = 00:00:03` (IN4 on `TB-14`, `P142 = 1`). The
  previous values read the documented wiring inverted: the roof would not have moved, and the
  drive fault would have latched at start-up. A deployment that overrides these settings keeps
  its own values. It also pins `LimitSwitchDebounce = 00:00:00.025`, the code default that the
  commissioning checklist's noise assumption (C3) depends on.
- **HTTPS by default outside Development.** `RoofControllerSecurity:RequireHttps` defaults to
  true outside Development; plain-HTTP requests to protected endpoints get 403 except from
  loopback and for `/health/live` and `/health/ready`. Provide a certificate or opt out
  explicitly (the controller logs a warning). `/health` now needs a Viewer key; `/health/live`
  and `/health/ready` stay anonymous.
- **Limit override on hardware.** `IgnorePhysicalLimitSwitches` is refused on physical
  hardware unless `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local
  configuration (through the API, only a local credential may change it; #42).
- **Camera.** Blue Iris credentials come from `BlueIris:UserName` and `BlueIris:Password`
  (environment variables or Docker secrets); the hard-coded credential was removed and must be
  rotated because it remains in the git history. `GET api/v1.0/Camera/{id}/mjpeg` needs a
  Viewer key or a person's session (#41).
- **Deployment.** `deploy-roofcontroller-rpi.sh` needs the operator key
  (`ROOF_OPERATOR_API_KEY` or `~/.config/hvo-roof/operator.key`) and aborts unless the Stop it
  sends is verified. It needs `HTTPS_CERT_DIR` (or `ALLOW_INSECURE_HTTP=true`); with HTTPS it
  publishes only port 8443 and plain HTTP stays on loopback inside the container, so automation
  that used `http://<pi>:8080` must move to `https://<pi>:8443`. The compose `pi` profile
  likewise needs a certificate and publishes only 8443; the new `pi-lan-http` profile is the
  explicit plain-HTTP opt-out. See [docs/deployment.md](docs/deployment.md).
- **The controller serves no pages (#46).** The web UI on port 8088 replaces the controller's
  browser console: the controller's pages, `/login`, `/account`, `/console/stop`, its sign-in
  cookie and its log view are gone, and ports 8080 and 8443 answer only the API, the status hub,
  `/health` and the camera proxy. Move bookmarks and reverse proxies to `https://<pi>:8088/`.
  `RoofControllerSecurity:AllowedOrigins` and `ConsoleLogBuffer:MinimumLevel` are retired; the
  web UI's own origins are `RoofWeb__AllowedOrigins__N`. A settings file that an earlier version
  saved with them still loads: they are ignored, the controller logs a warning at start and shows
  it to admins with the settings, and the next change through the API leaves them out of the
  file. For Stop without a session, give the web UI a Viewer key with
  `HVO_ROOF_WEB_STOP_KEY_FILE` (Compose) or `RoofWeb__StopKeyFile`
  ([docs/deployment.md](docs/deployment.md#the-web-uis-user-and-settings)).
- **Settings directories (#42).** Before upgrading, create `/etc/hvo-roof/config` (mode 0755)
  and `/var/lib/hvo-roof/settings-secrets` (mode 0700) on the Pi. The deploy script and both
  Pi compose profiles mount them, and Docker refuses to start the controller while either is
  missing. A version from before this change ignores the settings file, so after rolling back
  to one, check `GET api/v4.0/RoofControl/Configuration` against the wiring. See
  [docs/deployment.md](docs/deployment.md#upgrading-to-the-settings-file).

### Added

- Live status hub (#40): the SignalR hub `/hubs/roof` pushes every status change, the current
  status on connect and a heartbeat after 1 s without a change, as `RoofStatusHubMessage`
  (status, sequence, server time, instance id). Any role may connect with the `X-Api-Key`
  header or a person's session; a browser cookie is not accepted. A slow client receives only the newest status
  and never delays the controller or other clients. Connections whose key is removed, rotated
  or re-roled are closed within about a second, even while the roof moves; at most 32 are open
  at once, and at most 8 with one key. The hub accepts no commands. See [docs/security.md](docs/security.md#status-hub).
- People, sessions and managed API keys (#41). People sign in with a name and password
  (`POST api/v4.0/Auth/Session`, anonymous), or with a name and PIN at a kiosk
  (`POST .../Auth/Pin` with a kiosk key; operators and admins only). They get a session token
  sent as `Authorization: Bearer`, which every API route, `/health`, the camera and the status
  hub accept alongside `X-Api-Key`. PIN sessions end after 10 minutes idle. Five failures in a
  row lock the name out for 5 minutes, doubling up to 4 hours (429 with `Retry-After`); wrong
  PINs count for the name and for the kiosk. Attempts are counted before they are checked, so
  parallel guesses get no more than five; people and kiosks are always remembered, and a flood
  of made-up names only pushes out other made-up names. Each kiosk key, signed-in person or
  address (a public IPv6 address from another network by its /64) may try 30 sign-ins a
  minute (`SignInAttemptsPerMinute`). Stop is
  never locked out, and accepts the client's API key beside a session that has just ended.
  A PIN session cannot manage people, keys or sessions (403 `CredentialNotAllowed`). Admins manage people, API keys (generated by the
  controller, shown once, rotatable) and open sessions under `api/v4.0/Identity`; configured
  keys are listed read-only, and a change that would leave no admin credential is refused.
  Configured keys can be marked `Kiosk`. Everything is kept, as hashes only, in an identity
  store file (`RoofControllerSecurity:Identity:StorePath`, saved atomically with mode 0600 and
  the directory flushed),
  which the deploy script and both Pi compose profiles mount from `/var/lib/hvo-roof/identity`.
  A store that cannot be read refuses sign-in and management with 503 and reports
  `identity_store` Unhealthy in `/health`, while configured keys and Stop keep working. The
  deployment check fails on an unusable store (including a `StorePath` that is a directory). See
  [docs/security.md](docs/security.md#people-sessions-and-managed-api-keys).
- Remote settings and restart (#42). `GET api/v4.0/Settings/Catalogue` describes every
  setting once: group, type, range, default, the role that may change it, and whether it is
  safety-critical, local-only, a secret or needs a restart. `GET api/v4.0/Settings` returns the
  values in effect, where each comes from and the version. `POST .../Settings/{group}` changes
  one group (`roof`, `controller`, `camera`, `security`, `identity` and `logging` need
  `RoofAdmin`; `ui`, the default camera and the kiosk screen timeout, needs `RoofOperator`),
  with the configuration rules: `ExpectedVersion`, every field, `ConfirmSafetyCriticalChange`
  for a safety-critical change, no roof change while the roof moves or a clear-fault pulse
  runs, and an `AUDIT` entry naming the caller. The local-only settings
  (`AllowIgnoringLimitSwitchesOnPhysicalHardware`, `DriveStopConfirmationTimeout`,
  `DepartureReleaseTimeout`) need a local credential: a configured admin key marked `Local`,
  or an admin's PIN session at a kiosk whose key is marked `Local`; the check is on the
  credential, never the address. Changes are saved atomically to a settings file
  (`RoofControllerSettings:FilePath`, on the Pi `/etc/hvo-roof/config/appsettings.Local.json`)
  that holds only the values that differ from the shipped defaults, and the version, so
  changes and `ExpectedVersion` survive a restart. The Blue Iris user and password are
  write-only and kept in a separate managed secrets file (mode 0600). A hand edit to the file
  is shown as pending and blocks API changes (409 `SettingsHandEditPending`) until an admin
  reloads it, checked like an API change, or discards it. A file the controller cannot use
  stops startup with a clear error and exit code 1. `POST api/v4.0/System/Restart`
  (`RoofAdmin`) stops the roof, verifies the stop (409 `RestartRefused` otherwise), answers 202
  and exits with code 75 for the restart policy. `POST .../RoofControl/Configuration` is now
  the `roof` group under its old route, and its changes are saved too. The deploy script
  (`CONFIG_DIR`, `MANAGED_SECRETS_DIR`) and both Pi compose profiles mount the two
  directories, and the deployment check fails when they cannot be written. See
  [docs/security.md](docs/security.md#settings) and
  [docs/commissioning.md](docs/commissioning.md#the-settings-file).
- Client library (#44). `HVO.RoofControllerV4.Client` is what every UI client uses to reach the
  controller: a typed call for every API endpoint (a test fails when an endpoint has none),
  refusals as `RoofApiException` with the controller's `code`, its roof status and the shared
  wording, and a status feed that reconnects with backoff, orders snapshots by `sequence` and
  `instanceId`, and says since when its snapshot may be stale. Credentials are an API key, a
  session, or a kiosk's device key with a PIN session; a command-line client reads them from
  `HVO_ROOF_*` environment variables or a `0600` credentials file. Stop is sent at once on a
  connection of its own, never queued, and every client uses the same Stop wording, pinned by a
  test; a key the controller does not accept is reported as refused, not as a sign-in. A
  certificate pin covers requests, Stop and the status hub's WebSocket. Settings forms are built
  from the catalogue; a secret left empty keeps its value, and `ClearSecret` removes it.
  `X-On-Behalf-Of` must be a user name, and a key or token must be printable ASCII, so no
  request, Stop included, fails on a header it cannot send. A credentials file in a directory other users can
  change is refused, and requests print secrets only as `(set)`. A status feed handler that
  throws is logged and the feed carries on. The web UI takes its Stop wording and status
  rules from the library (#46). See
  [src/HVO.RoofControllerV4.Client/README.md](src/HVO.RoofControllerV4.Client/README.md).
- Command line and terminal interface (#45). `hvo-roof` (`HVO.RoofControllerV4.Cli`) reaches the
  controller only through the client library. It is published as one self-contained file for
  `linux-arm64` and `linux-x64`, and CI keeps both as an artifact. It has a command for every
  operation: status (with `--watch` over the status hub), health, Stop, open and close, the
  operator lease, clearing a fault, settings, people, PINs, API keys, sessions, the
  controller's information and restart. Every command takes `--json`, and the exit codes are
  documented and pinned by a test. Stop never needs more than the credential in use and uses
  the shared wording. Open and close follow the motion and renew the operator lease, and Ctrl+C
  sends Stop, after the command's answer (waiting up to 3 s for it); before the command is
  sent, Ctrl+C sends nothing. When their answer never arrives, they say that the roof may be
  moving and how to stop it. A safety-critical settings change, or a restart that would load
  one, is shown and not sent until it is confirmed (exit 10). Credentials come from
  `HVO_ROOF_*` variables or a `0600` file under `~/.config/hvo-roof/`, and a secret is never
  taken from the command line.
  `hvo-roof setup` saves the address, a key and a certificate pin, and adds the first admin.
  `hvo-roof ui` is a Terminal.Gui interface with Roof, Settings, People, System and Setup
  pages, and Stop on F9 from every page, even over a prompt. It says plainly when the status is
  stale, offers no motion then, and stops a roof it moved, or may have moved, before it quits.
  That Stop waits up to 3 s for the answer to an Open or Close still on its way, says so when the
  command may still reach the controller after it, and the interface sends Stop again when an
  Open or Close overtakes one. It never closes while a Stop is on its way, and it
  shows the newest Stop's result. It is drawn in HVO Dark, the web UI's theme, and in the
  terminal's own colours with `NO_COLOR`. SIGINT, SIGTERM and SIGHUP end a command that moves the
  roof only after it sends Stop, even when a closing terminal sends SIGHUP twice; nothing cuts
  `stop` short; and the process still ends within 5 s, or 15 s for `open`, `close`, `stop` and
  `ui`. A Stop that nothing confirms (a server error, no answer) exits 9, and the interface does
  not close on one without saying so. Tests cover every
  command, the interface on Terminal.Gui's in-memory driver, scenarios against the emulated
  roof with a lease shorter than the travel, and the published binary in a real terminal
  (tmux) against the compose emulator. That run also draws each screen as an SVG image
  (`tests/cli/ansi-to-svg.py`), and those images are the screenshots in
  [docs/cli.md](docs/cli.md).
- Two processes in one container (#43). The image runs the controller and a new web UI
  (`HVO.RoofControllerV4.Web`, a client of the controller's API over the container's loopback)
  under `roof-supervisor`, with `tini` as PID 1. `docker stop` stops the controller first and
  waits for its verified safe stop (up to 25 s), then the web UI (up to 2 s), within the
  container's 30 s grace period. A controller that exits with 75 (`POST System/Restart`) is
  started again at once in the same container; a crash is restarted with a backoff (1 s,
  doubling, at most 30 s), and the fifth crash within 120 s leaves the controller stopped, with
  the container's health check failing, instead of looping. A web UI that exits is restarted on
  its own, and the controller is not touched. A controller that does not answer can be restarted
  by force (a kill, with the guarantees of `docker kill`): for now with `docker exec` and the
  supervisor's control file, or the web UI's System page (#46). The
  web UI runs as the unprivileged `app` user with only its `RoofWeb__*` settings, and cannot read
  the secrets directory; for HTTPS it holds a copy of the certificate it serves and its password
  (by default the controller's; `RoofWeb__Certificate__Path` gives it its own). It
  listens on port 8088 (`WEB_HOST_PORT`; HTTPS with the controller's certificate, or plain HTTP
  with `ALLOW_INSECURE_HTTP=true`), which the deploy script and the Compose profiles publish.
  The controller keeps 8080 and 8443, so existing clients do not change. The health check stays
  the controller's readiness. The deploy script checks the web UI after a deploy and rolls back
  when it cannot start. `tests/container/supervisor-tests.sh` tests the supervisor with fake
  processes, and the `supervisor` container scenarios run it on real Docker (C11 steps 5-9). See
  [docs/deployment.md](docs/deployment.md#the-containers-two-processes).
- Web UI (#46). `HVO.RoofControllerV4.Web` is the browser interface for phones, tablets and
  desktops, in HVO Dark, a client of the controller's API and status hub like `hvo-roof`. People
  sign in with a name and password (the controller's session, kept in an HttpOnly,
  `SameSite=Strict` cookie that never outlasts it; `RoofWeb:SignInAttemptsPerMinute` per
  address), and change their own password. The Roof page follows the live status, with Open,
  Close and Clear fault for operators, the operator lease renewed only while the page's
  connection is up, a stale view after 3 s without a status, and the roof camera relayed with
  the person's session (Live, Stalled, Reconnecting or Offline, with the last frame kept). The
  Health page shows readiness, the supervisor and the health checks; the Settings page changes
  one setting at a time, with a review, confirmation for safety-critical changes, secrets set
  and never shown, and hand edits; admins manage people, API keys and sessions, and restart
  the controller or force a restart through the supervisor. Stop is on every page, the
  sign-in page too, in a bar that is never disabled and works without the page's live
  connection (`POST /stop`), with the controller's wording; with `RoofWeb:StopKeyFile` it
  still works after the person's session ends, from a Stop pass (a cookie sent only with
  `/stop`, naming the person) that lasts `RoofWeb:StopAfterSessionHours` (default 12) after the
  session would have expired and is removed at sign-out. `/stop` allows 30 Stops at once from
  each person (signed-out pages: from each address), then four a second. Sign-out ends the
  session in the web UI even when the controller does not answer, and an unknown address shows a Not found page (404). A mode banner marks an emulated HAT on every
  page, the sign-in page too, which reads it from the new anonymous
  `GET /api/v4.0/RoofControl/Mode` (only the HAT mode and whether the limit switches are
  ignored). The sign-in page also says whether the controller is running and ready (the Health page's
  headline, without the details), so while nobody can sign in, for example after repeated crashes, it says why.
  Form posts and the live connection must come from the web UI or `RoofWeb:AllowedOrigins`.
  The supervisor gives the web UI a private copy of its Stop key and a directory for the keys
  that protect its cookie (`/var/lib/hvo-roof-web/keys`, made by root in a directory it keeps
  root's; any other, set with `RoofWeb__DataProtectionPath` or `HVO_SUPERVISOR_UI_DATA_DIR`, is
  made by the web UI's user).
  Every button a person taps is at
  least 44 x 44 CSS pixels and 8 CSS pixels from its neighbours on each screen tested. The
  pages are tested with bUnit against a fake controller, and in Chromium against the emulated
  roof, which also takes the screenshots in [docs/web.md](docs/web.md).
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
- Web UI (#46): the reconnect dialog has a Stop button that works while the page's live
  connection is down (`POST /stop` on the web UI), and the page stops renewing the operator
  lease as soon as its connection drops. Renewal does not resume on reconnect.
- Drive-running interlock on IN4 (#29), with `AtSpeedConfirmationTimeout` set: a start is
  refused with `InterlockActive` while IN4 still reports the drive running (after a ramp stop a
  reversal is refused until the drive has stopped; with the coast stop IN4 drops within
  milliseconds and the reversal proceeds while the roof is still coasting), and IN4 low for 250 ms after it confirmed,
  without the destination limit, stops the roof and latches `DriveNotRunning` (an external stop,
  a trip the fault input missed, drive power loss). The inputs are re-read first, so a limit
  edge not yet delivered still ends the move as `LimitSwitchReached`.
- `DriveStopConfirmationTimeout` (0.5-60 s, local only; default: `AtSpeedConfirmationTimeout`):
  how long IN4 may stay high after a stop before a Critical log entry. Set it longer than the
  drive's deceleration when a ramp stop is used, plus `P175` with a DC brake.
- `DepartureReleaseTimeout` (0.5-60 s, local only, off by default): a start limit that has not
  released, and stayed released for `LimitSwitchDebounce`, in time stops the roof and latches
  the new stop reason `DepartureLimitNotReleased` (15), catching a jammed roof or one driving
  the wrong way before the stop behind the limit. It must be shorter than
  `SafetyWatchdogTimeout` and at least `LimitSwitchDebounce` plus 0.5 s.
- `HVO.RoofControllerV4.Simulation` and the emulated-plant tests (#29): the production
  controller and configuration, through the real HAT library, drive an emulated Lenze SMVector
  drive, two ME-8108 limit switches, the SM-I-010 HAT and the documented wiring, modelled from
  the vendor documentation. Each I2C transaction takes the HAT library's bus time (the transfer
  at an assumed 100 kHz, then its 15 ms pause), so the relays close in their real order and
  spacing. The drive model follows `P100` (0-5; 6 is only for 15 HP and larger drives),
  `P112`, `P110` = 2, `P174` and `P175` (including 999.9), with the Run output during a DC
  brake as a named assumption and the manual's other open points listed in
  `SmVectorAssumptions`; the ME-8108 bounce follows elapsed time. The tests cover normal
  cycles, drive trips and power loss, external stops, stop methods and DC brakes, the
  factory `P100` and `P112`, switch and wire faults, relay and I2C faults, and each wiring
  mistake from closed, mid-travel and open; none relies on physical hardware.
- HAT emulator (#30): `HVO.RoofControllerV4.Emulator` serves the emulated plant's HAT registers
  over TCP (register port 5291) and has a fault-injection API on loopback port 5290 (drive trips
  and power, HAT power, external stop, jam, limit switch, relay, wiring, I2C and link faults,
  plant history and violations, time scale 0.1-100). See [docs/emulator.md](docs/emulator.md).
- HAT emulator mode (#30): with `HatEmulator:Enabled`, the controller sends its HAT register
  accesses to the emulator instead of the I2C bus, with nothing else changed. A lost link, a
  timeout or a malformed reply is an I/O error, which the controller fails safe on as for a
  failed I2C transfer. Emulator mode is refused outside Development unless
  `HatEmulator:AllowOutsideDevelopment` is set. It shows as an `EMULATED HAT` banner on every
  web UI page, `hatMode: "Emulated"` in Status, Degraded health (every health description
  names the emulator, a fault's too), startup warnings and the
  telemetry attributes `hvo.roof.hat.mode` and `hvo.roof.hat.emulator.endpoint`. The deployment
  check fails on refused emulator settings and on emulator mode with `/dev/i2c-1` mapped, and
  warns on allowed ones.
- Deploy script (#30): `HAT_EMULATOR_ENDPOINT` with `ALLOW_EMULATED_HAT=true` deploys a
  test-rig controller against the HAT emulator, without `/dev/i2c-1`. The endpoint without the
  flag is refused, and so is a `HatEmulator` setting in `EXTRA_DOCKER_ARGS`, given directly or
  in an `--env-file` (which must be readable). In emulator mode an I2C `--device`,
  `--privileged` and a mount of the host's `/` or `/dev` are refused too. The verification
  checks the `hatMode` the new controller reports against the one deployed and rolls back on a
  mismatch. `--rollback` accepts a version that uses the emulator only with
  `ALLOW_EMULATED_HAT=true`: one deployed for the emulator is refused before anything is stopped,
  and the restored version's `hatMode` is checked once it runs. A version from before emulator
  mode reports none and passes only with `isUsingPhysicalHardware` true: without an I2C bus it
  ran on the register simulation, whose verified Stop says nothing about the roof. The dry-run
  and the final report name the HAT the controller uses.
- Emulator container (#30): `src/HVO.RoofControllerV4.Emulator/Dockerfile` (non-root,
  `linux/amd64` and `linux/arm64`), the compose `emulator` profile (production settings
  against the emulator container, no devices, loopback only, on its own network), and
  `tests/emulator/compose-smoke-test.sh`, which opens and closes the emulated roof through the
  containerized controller. CI workflow `emulator-image.yml` builds the image for both
  platforms and runs the smoke test.
- `pi-image.yml`: builds the `linux/arm64` Pi image in CI.
- [docs/commissioning.md](docs/commissioning.md) (the commissioning checks C1-C15, #31): no
  meter, oscilloscope or bench step remains. Each step names the scenario that covers it, and
  each check lists its installation assumptions with the setting that depends on each one and
  what the emulated plant shows when it is wrong. Also [docs/ci-runners.md](docs/ci-runners.md).
- Commissioning scenarios (#31): every check C1-C15 in docs/commissioning.md is an automated
  scenario. `EmulatedRoofRig` starts the whole controller host with the production
  `appsettings.json` against an in-process HAT emulator and plant (a 25 cm roof in real time,
  so the controller's windows keep their production values), and each scenario drives it
  through the API as an operator does. `[CommissioningCheck]` names the check and step a
  scenario covers, and `ScenarioCoverageTests` fails when the document and the scenarios
  disagree: a check or step without a scenario, a scenario naming a step the document lacks, a
  scenario that CI does not run, a check without its installation assumptions.
- C14 soak (#31): the production settings cycle the emulated roof, with a mid-travel Stop every
  fifth cycle, the camera streaming through the proxy and OTLP export to a collector that never
  answers. It checks the #29 plant invariants, every stop reason, Status, input and relay read
  freshness, Stop latency drift, memory, threads and file descriptors, and writes a summary
  with the motion timing. It runs for 90 s with the scenarios and for `HVO_SOAK_DURATION`
  (two hours nightly), with its results in `HVO_SOAK_RESULTS_DIR`.
- Container scenarios (#31): `tests/emulator/deploy-scenarios.sh` runs, on Docker against the
  HAT emulator, `docker stop` and `docker kill` during travel (C11), commissioning C12 with the
  deploy script (an idle deploy, a deploy while moving, pre-flight failures, a failed remote
  check that rolls back, `--rollback` twice, with the relays sampled throughout), and the move
  from Compose to the deploy script and back. They also run (#17) `docker stop` with a camera
  stream open, which must end within 5 s; `docker stop` while relay writes fail, both until a
  shutdown retry verifies the relays off and for good; a Stop that cannot be verified, which
  aborts the deploy before the controller is stopped; and a new controller that never becomes
  ready, which is rolled back. The move between Compose and the script checks each Compose
  controller with the deploy script's `--verify-remote` (#18).
- CI workflow `scenarios.yml` (#31): the scenarios, the browser tests and the container
  scenarios on pull requests to `main` and `feature/**` and on pushes to `main`, and the
  two-hour soak nightly and on demand, with the soak's invariant results in the run summary
  and its artifacts.
- Browser tests (#31, #46): Playwright runs the web UI in Chromium on an iPhone 13 and an iPad
  (gen 7), each upright and sideways, on an iPhone SE sideways and in a desktop window, the
  phones over HTTPS, against the whole controller and the emulated plant: sign-in, the session
  cookie, a session that expires while its page is open, another origin's form posts and live
  connection refused, Stop in view and uncovered on every page and stopping the roof, what each
  role is offered, the stale status, the camera stalling and going offline, the lease when the
  page closes or loses its connection (C9) and the reconnect dialog's Stop (C15). Every page's
  screenshots on a phone, a tablet and a desktop are kept with the results (CI artifacts), and a
  failed test attaches a screenshot, the trace and the browser log.
- Emulated camera (#31): the HAT emulator serves an MJPEG camera of the emulated roof at
  `/mjpg/camNN/video.mjpg`, the Blue Iris path the camera proxy requests, and
  `POST /api/emulator/camera` freezes it, refuses with 503 or 401, changes its frame rate or ends
  the open streams. The Development settings, the compose `emulator` profile and
  `src/docker-compose.yml` point the camera proxy at it.
- Motion timing metrics (#31): the histograms `roof.controller.travel.duration`,
  `roof.controller.drive.start_delay`, `roof.controller.drive.stop_delay` and
  `roof.controller.departure.release` record what the timing options should be set from. See
  [docs/telemetry.md](docs/telemetry.md).
- Root `.dockerignore` for the repository-root build context.
- Deployment check: `dotnet HVO.RoofControllerV4.RPi.dll --validate-deployment` validates the
  configuration without starting the host or touching the HAT: the roof options (including the
  limit-switch override when `/dev/i2c-1` is mapped), the other options sections and log levels,
  the API keys and the deploying key's role, the listeners Kestrel would actually use (including
  the `http://localhost:8080` listener the health check needs, and endpoints Kestrel would refuse),
  every configured certificate (loaded as Kestrel loads it, with the Server Authentication usage)
  and that `AllowedHosts` includes `localhost`. The deploy script runs it with the final
  container's configuration before stopping anything; both Pi compose profiles (`pi`,
  `pi-lan-http`) run it before the controller starts, and the `emulator` profile does not.
- The deploy script verifies the new controller from the deploying machine (authenticated
  Status and a verified Stop at the published URL, `REMOTE_CA_CERT` for a private CA), keeps
  the old container as `roof-controller-previous` and rolls back to it when the new one fails.
  `--rollback` swaps them on demand.
- The deploy script checks its settings before contacting Docker (whole decimal numbers, the
  poll interval, `EXTRA_DOCKER_ARGS`, which may not set the name, detach, removal, restart
  policy, cidfile, ports, stop timeout or stop signal, and the HTTPS choice, now also for
  `--rollback`). It stops with nothing changed when Docker cannot report the containers' state,
  or when `roof-controller-previous` is running, restarting or paused. Once the old controller's
  stop begins, any failure, signal or lost terminal restores it; only the container the run
  created is removed. From that stop on, docker runs in its own session, so an interrupt waits
  for the call in progress instead of cutting it short, and the restore stops an old controller
  that is still running before starting it again. An older `roof-controller-previous` is removed
  only after the stop succeeds. `--rollback` undoes a failed or interrupted swap and refuses to
  run while `roof-controller-swap` exists. Needs Docker CLI 20.10 or later, and `setsid` or
  `perl`; the Docker context must connect without prompting, which is checked before anything
  changes.
- Relay register read supervision: `relayRegisterReadsHealthy`, `lastSuccessfulRelayReadUtc`
  and `consecutiveRelayReadFailures` in Status. One failed or stale read fails readiness; two
  consecutive failures stop motion and latch `RelayVerificationFailed`.
- An unverified shutdown stop is retried (all-off every 500 ms until it verifies, the controller
  is disposed or 15 s pass), and the host's shutdown wait is bounded even if a HAT call blocks
  (it logs Critical and moves on). While that call is still blocked, later shutdown triggers do
  not queue another one, and disposal stops waiting for the controller lock after 2 s (its
  all-off stop runs if the stuck call returns; until then the controller is shutting down, not
  disposed).
- Deploy script `--verify-remote` (#18), the authenticated remote check for a Compose
  deployment. It checks the published URL from the deploying machine and nothing else: an
  authenticated `GET Status` must report the expected `hatMode`, then `POST Stop` must be
  verified. It makes no Docker call and changes no container. Run it before
  `docker compose up` replaces a controller and after, as the script's own deploy does in its
  step 7. A controller from before emulator mode reports no `hatMode` and passes as the
  physical HAT when it reports `isUsingPhysicalHardware` true. Any key may send a Stop, so it
  does not prove that the key can operate the roof; only a deploy's pre-flight checks that.
- Health and readiness semantics per deployment in [docs/deployment.md](docs/deployment.md#health-and-readiness)
  (#18): what `/health/live`, `/health/ready` and `/health` answer, which conditions make the
  controller Degraded or Unhealthy, what each deployment should report, and why readiness does
  not prove remote access.
- CI job `deploy-script`: ShellCheck and tests for the deploy script against fake
  `docker`/`curl` (including `--force-unverified-stop` without a terminal, without the flag,
  with a wrong answer and with the typed confirmation, #17), and `docker compose config` for
  the Pi compose file's profiles (rejecting `pi` with `pi-lan-http`, and checking that the Pi
  profiles pin the HAT emulator off and the `emulator` profile maps no devices and has its own
  network) and for `src/docker-compose.yml`.
- API security tests (#18; tests only, no behaviour change): every mapped endpoint must refuse
  an anonymous caller unless allow-listed; a viewer is refused Close, ClearFault and Lease;
  `System/metrics` needs an admin; malformed, empty and `text/plain` configuration bodies are
  refused and change nothing; and a host outside `AllowedHosts` gets 400.

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
- Wiring documentation (#29): with `P120 = 2`, `TB-4` is the drive's +15 V reference, so the
  IN1-IN3 opto commons return to `TB-2` (0 V), and RLY4 NO lands on `TB-4` itself. The earlier
  drawing, with the commons on `TB-4`, never conducts (the drive reads as faulted) and, with
  RLY4 on 0 V, holds the drive stopped. The diagrams, `hardware-overview.md` (sections 2, 3, 5,
  7, 8, 10-12 and the bring-up checks) and the wiring package zip are updated.
- The SM-I-010 LED modes (LED1-LED3 from the controller, LED4 showing IN4) are re-applied with
  each indicator change, and while idle when a mode write failed or the modes read back wrong,
  so a HAT that reset shows the logical states again instead of the raw inputs. During a move
  the LEDs are written once per change, so a failed LED write is not retried with every poll.
- The supervision loop wakes when a start limit's release is first seen and when IN4 drops, so
  the release is verified and the run-loss window enforced on time rather than at the next
  verification interval.
- Development runs against the HAT emulator (#30): `appsettings.Development.json` enables
  emulator mode with the limit switches in force and the production wiring, instead of the
  in-memory register simulation with the limit switches ignored. Start the emulator before
  the controller. The in-memory simulation remains for unit tests and as the fallback with
  emulator mode off and no I2C bus. `devcontainer.rpi.json` sets `HatEmulator__Enabled=false`
  to use the physical HAT, and `src/docker-compose.yml` runs the controller in Development
  against the emulator container (admin key from `HVO_DEV_ROOF_API_KEY`).
- The deploy script sets `HatEmulator__Enabled=false` and
  `HatEmulator__AllowOutsideDevelopment=false` for a physical-HAT deployment. A setting in the
  secrets directory is read later and could override them, so the verified `hatMode` is what
  makes the HAT certain: a physical deployment whose controller reports anything else is rolled
  back. The Pi compose profiles set the same two settings. With `/dev/i2c-1` mapped, such an
  override now fails the deployment check, so the script's pre-flight and the profiles' check
  service refuse it before anything is stopped.
- The deploy script (#31) refuses a `<name>` or `<name>-previous` container that Docker Compose
  created, before anything changes: it replaces and restores only controllers it created, and
  [docs/deployment.md](docs/deployment.md) describes moving between Compose and the script.
  `BUILD_PLATFORM` (default `linux/arm64`) builds `linux/amd64` for a test rig on the HAT
  emulator, and in emulator mode the container maps no host device or Pi file.
- The deploy script fails the new controller's readiness check at once when the controller
  exits before it is ready, whether its container stopped or Docker restarted it ("exited
  before it became ready"), instead of waiting out `READY_TIMEOUT_SECONDS`, and then rolls
  back (#17).
- The idle supervision loop runs an overdue drive-stop check at once. It could wake just before
  the deadline and then fall back to the 1 s idle interval, so "Drive still reports running
  (IN4)" was logged about 1.1 s late.
- Removed `.LocalPackages` directory — all HVO packages now sourced from nuget.org
- Removed `LocalPackages` NuGet source from `NuGet.config`
- Removed `.LocalPackages` COPY from Dockerfile
- Repository documentation standardization

### Known limitations

Found with the emulated plant (#29); the plant's travel, speed and hard-stop clearance are
assumptions, so the distances are indicative. Mitigations beyond these settings are tracked in
#32.

- A ramp stop (`P111` = 2) with `P105` = 2 s runs into the hard stop; coast (the factory
  default) stops about 10 mm past the limit's operating point.
- Swapped motor leads, starting from a limit, reach the stop behind it before the stall trip
  unless `DepartureReleaseTimeout` is set between the release time with the installed `P104`
  and the time a wrong-way move takes to reach that stop.
- An open limit that never operates reaches the hard stop; only a travel-time or position check
  would catch it.
- A welded direction relay is invisible to the register read-back; RLY4 still stops the roof.
  A following reversal closes RLY4 about 30 ms before the new direction, so the drive runs the
  welded way for that long, then refuses both inputs and latches `DriveNotRunning`.
- With the coast stop, a reversal while moving proceeds as soon as IN4 drops (in the emulated
  plant about 8 ms after the RLY1 write), and the drive starts the other way about 170 ms after
  the write, while the roof is still coasting to rest (`PlantDocumentedFiguresTests`).
  A ramp stop keeps IN4 high while it decelerates, and the reversal is refused.
- If the Run output stays on during a DC brake (`P111` = 1 or 3; SV01J does not say), the next
  move is refused until `P175` has passed, and with `P175` = 999.9 every move after a stop is
  refused. Keep `P175` short, or use coast.

### Removed

- The controller's browser console (#46), replaced by the web UI: its pages and scripts, its
  sign-in cookie and `/login`, `/account` and `/console/stop`, its origin check, its log view
  (`ConsoleLogBuffer`) and the settings only it used (`RoofControllerSecurity:AllowedOrigins`,
  `ConsoleLogBuffer:MinimumLevel`, and the settings catalogue's `StringList` type).
- The native iPad/MAUI app and its iOS and self-hosted M5 build workflows. The web UI (#46) is
  the supported operator client. The server findings from its review were fixed
  in #28, and the safety checks run as the emulated commissioning scenarios (#31).

## [1.0.0] - 2025-03-01

### Added

- Initial extraction from HVOv9 monorepo
- RPi controller (ASP.NET Core + Blazor Server with GPIO/I2C roof control)
- iPad .NET MAUI client
- Shared Common models and options library
- CI/CD workflows (ci.yml for tests, ios.yml for iPad build)
- Dev Container setup for consistent development environment
- Docker deployment configuration for Raspberry Pi (linux-arm64)
