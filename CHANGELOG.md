# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- First-install kiosk PIN commissioning for one explicitly enrolled human Linux owner: temporary setup-only access,
  mandatory Choose/Confirm, and fresh personal-PIN login capped at Operator. Existing installations are unchanged.
- Offline installs and restores: each release carries the controller and HAT emulator images as offline archives for
  amd64 and arm64 (`<image>-<version>-linux-<arch>.oci.tar`), listed in `release.json`
  (`images.<name>.offlineArchives`, schema 1 unchanged), `SHA256SUMS` and the attestation. `install.sh --from DIR` and
  `hvo-roof-install --release DIR` load them, accept an image only with an ID `release.json` names, and pull nothing;
  the deploy script takes one as `IMAGE_ARCHIVE` (with its SHA-256 from `release.json` as `IMAGE_ARCHIVE_SHA256`),
  and refuses before loading it an archive that holds anything but that image (another image, name or file, a link or
  other special member, or JSON that gives a name twice). Docker loads only a private copy that the installer or the
  deploy script has checked, never the file itself, so the archive needs room for a copy in `TMPDIR`; the load can
  point the image's own `<repository>:<tag>` at it, and no other name. The installer, the deploy script and
  `install.sh` check the physical `TMPDIR` ancestry's owners and Unix modes before creating private work folders;
  cleanup checks the saved path's current owner/mode under the [temporary-folder trust model](docs/install.md#temporary-files).
  The installer trusts a container's release
  label only for an image with one of those IDs. See
  [The release it installs](docs/install.md#the-release-it-installs) (#127).
- Resource limits for the controller's container: a CPU, a memory (no swap) and a process limit, by default 1 vCPU,
  2 GiB and 512 processes (#149, under Changed), set with `hvo-roof-install`'s `controller.resources` answers or
  `--cpus`, `--memory` and `--pids-limit`, or the deploy script's `CONTAINER_CPUS`, `CONTAINER_MEMORY` and
  `CONTAINER_PIDS_LIMIT`. A CPU limit below 1 vCPU or a memory limit below 1 GiB, RoofController's floor, is refused,
  with no exception (also checked for Compose starts and its deployment check). The install record keeps
  them, and upgrade and rollback give them again (#129).
- State contracts (#130): each image declares, in its `io.hvo.roof.state-contracts` label and in `release.json`'s
  `stateContracts`, the schema it writes and those it reads of each store the controller keeps (the identity store and
  the settings files). `upgrade` and `rollback` refuse (exit 3, nothing changed) a release that cannot read the stores
  here, and `upgrade` says when a release makes a breaking state change. A store's file that is a folder, or not a
  file at all (a FIFO, a device, a socket: refused before anything reads it, and never waited on), or that root
  cannot read, is refused too; only a plan without sudo leaves it to the run as root. See
  [State contracts](docs/install.md#state-contracts).
- `upgrade` now backs up the controller's stores before it changes anything, as `backup` does, to
  `/var/backups/hvo-roof`; the record names the backup (`upgradeBackup`). No log, program or image is copied. The
  installer takes it before it replaces itself and hands its file and SHA-256 to the new release's installer, which
  reads the file once and checks that what it read has that SHA-256 and is this machine's backup of the release
  installed. A backup that cannot be taken, or a store's file a backup cannot hold (a link), refuses the upgrade
  (exit 3) with nothing changed. Through an installer from 4.1.1 or earlier, which replaces itself before the new one
  takes the backup, the new installer stays in place and nothing else changes. The upgrade marks its record
  (`upgradingFrom`, #131) only once the backup is taken, and the mark names it, so a refused backup leaves the record
  as it was. The backup is on disk (the archive synced, then its folder and each folder made for it) before the
  installer hands over, the record names it, or anything installed changes; a sync that fails stops the upgrade
  (exit 1), the archive taken away again. A backup's manifest names the stores' files that were not there (`absent`). Backups are written to and
  read from `/var/backups/hvo-roof` only while it, and each folder above it, is root's alone and not a link: one that
  is not is refused (exit 3), never repaired (#130).
- `rollback --restore-backup [FILE] [--yes]`: across a breaking state change, puts back from that backup the stores the
  release before cannot read, after the controller's verified Stop, saying plainly that the changes made to them since
  the upgrade are lost; the files it replaces are kept beside them. Without it, such a rollback is refused. Nothing is
  put back unless Docker says, just before each file, that the controller and the one kept for a rollback are both
  stopped (or not there), and a store's file is removed only when the backup names it as not there. The file kept
  beside a store, the store put back (with its owner) or moved aside, and the stores' folders are on disk before the
  controller before starts; a sync that fails stops the rollback before the swap (exit 1) (#130).

### Changed

- Redesign every Web route and inline workflow with the approved responsive HVO Dark layout, canonical colours,
  grouped panels and fields, 48px controls and 320px reflow. Web now owns its styles, icon fonts and notices; remove
  the obsolete `HVO.WebSite.Themes` project and unused OpenSans assets (#174).
- Redesign the kiosk and Mac screens with the approved HVO hierarchy, permanent red Stop, bounded kiosk readings/notices/people
  pages, private PIN steps, clearer password administration and startup recovery. Preserve 12 mm targets, consumed wake input,
  Operator-only PIN policy and all shared Stop messages, including acknowledgement without register-verification evidence.
  Keep complete long-token/multiline messages reachable across pages, and qualify cached or never-read compact input values.
  Align the shared palette and existing terminal RGB schemes with the 20 final Sky roles; terminal capability/layout work remains
  separate (#175, refs #158).

- Compose starts verify the supervisor's own cgroup v2 CPU quota, memory, zero swap and PID limits against the
  configured expectations. Missing, unlimited, unreadable, malformed or different limits, cgroup v1 and unverifiable
  placement refuse both applications (exit 2). The supervisor still turns every relay off and verifies the register,
  retrying failures while both applications stay stopped; Stop remains bounded. One-shot deployment checks never
  touch the HAT, and the deploy script's degraded rollback recovery remains available (#152).

- `install.sh --from DIR` no longer checks that ghcr.io answers: it checks that `DIR` holds the release's offline
  image archives for this machine instead (#127).
- **The controller's container runs with CPU and memory limits by default: 1 vCPU and 2 GiB, with no swap beyond it,
  RoofController's default (owner decision on #149), now that the supervisor turns the relays off whenever the
  controller exits, an OOM kill included (#138).** A fresh install and an upgrade both give them, the upgrade to a
  controller that had none. A value set persists, also through a rollback on a host that cannot enforce it: the install
  record keeps it for upgrade and rollback, and the deploy script records the limits each deploy or rollback gives as
  the desired ones (each record a Docker volume of its own, `roof-controller-limits-g<generation>`, replaced whole, so a
  failed or interrupted update keeps the last good one, and validated on every read: each label read on its own, as
  JSON, and its whole value checked). Given no `CONTAINER_CPUS` or `CONTAINER_MEMORY`, it keeps those (the running
  controller's without a record, the default for a controller without one), and the installer and `install.sh` resolve a
  limit not given the same way; the installer's uninstall and purge of the controller remove the records whether or not
  the container is there. Compose's `HVO_ROOF_CPUS` and `HVO_ROOF_MEMORY` default to `1` and `2g`. A value below the 1
  vCPU / 1 GiB floor is refused before anything changes; more is accepted. There is no longer a setting for no limit: an
  empty `CONTAINER_CPUS` or `CONTAINER_MEMORY` is the default, not none (#149).
- **The installer, `install.sh` and the deploy script refuse a host whose Docker does not enforce the container's
  limits, with nothing changed and no override, as SkyMonitor does (#1285), on every install, upgrade and deploy**
  (SkyMonitor checks only at install). One `docker info` must report `MemoryLimit` and `SwapLimit`, `CPUCfsQuota` and
  `PidsLimit` as `true` (one it does not report counts as unsupported), and at least the CPUs of the CPU limit. The
  refusal lists every finding, with the cgroup version, the kernel's cgroup v2 controllers and Docker's warnings about
  limits, and the remedy. **A stock Raspberry Pi OS is refused**: its firmware adds `cgroup_disable=memory` to the
  kernel's command line, so append ` cgroup_enable=memory` to the end of the single line in `/boot/firmware/cmdline.txt`
  (the later argument wins) and reboot the Pi with the roof idle before the first deploy. The deploy script's
  `--dry-run` reports the refusal (exit 1); `--stop` is not refused. A **rollback**, a recovery path, is not refused for
  a limit Docker cannot enforce: it goes ahead with the ones it can, and warns loudly, naming each limit and the fix, in
  its output and in the controller's log (`docker logs`); its other refusals (the floor, the CPUs, a Docker that does
  not answer, the verified stop) stand; `install.sh rollback` warns and starts it too. Memory and swap are separate:
  `SwapLimit` false alone is `docker-swap-limit-unsupported`, and a rollback there keeps the memory limit. An upgrade to
  another release and a restore check the limits before their first change (the hand-over's backup and installer, the
  restored files). A memory limit above the machine's memory is kept, with a warning: a limit is a cap, not a
  reservation (#129, #149).
- The mounted secrets, certificate, identity and settings directories must be root's, as the installer and
  [deployment.md](docs/deployment.md#one-time-preparation-on-the-pi) make them: the controller no longer has
  `CAP_DAC_OVERRIDE`, and the deployment check refuses a directory it cannot reach (#129).
- The deploy script's `EXTRA_DOCKER_ARGS` refuses options that would change the container's privileges or limits,
  `--privileged` included in every mode (#129).

- Upgrading from installed 4.1.1 to this schema-3 release requires the new release's
  `install.sh upgrade --version <VERSION>` bootstrap, which verifies the downloaded installer. The old installed installer rejects schema 3 before replacing
  itself or showing upgrade notes. See [Upgrading](docs/install.md#upgrading); existing identities and configuration
  are retained, with no default access added. Schema-incompatible rollback remains refused unless
  `rollback --restore-backup` is asked for.
- The installer reads any `identityStoreSchema` in a release's `release.json`, and the release's state contracts
  decide whether it reads the stores, so a later identity schema needs no bootstrap like 4.1.1's. `check-image-labels.sh`
  requires each image's state contract label (#130).
- `uninstall` no longer takes a controller Docker cannot answer for (Docker not running, say) for none: stopping it is
  blocked, and nothing is removed, until Docker answers (#130).

### Fixed

- Keep the permanent red direct-REST Stop visible in browser camera fullscreen and red under keyboard focus. Preserve
  its actual verified-register or verification-unavailable acknowledgement and failure messages. Inline editors now
  focus their fields, associate validation errors and return focus after Escape/Cancel. Camera operation failures
  now show separate feedback without presenting a live stream as offline, and a failed recording stop remains
  available to retry. Reconnect Stop stays initially visible in short landscape views while explanations scroll
  independently (#174).

- Corrected the kiosk guide's PIN-access instructions: PIN unlock permits roof operation, while settings and
  administration use the existing web, CLI/TUI or password-authenticated Mac paths. Authorization behavior is unchanged (#171).
- Installation and deployment documentation now states the accepted single-user temporary-folder assumptions, the
  remaining ACL, ancestor-replacement and cleanup-identity gaps, and role-specific sudo/`TMPDIR` handling. Runtime
  behavior is unchanged (#147).
- Upgrades prepare recovery before approved kiosk package setup, including the wizard's approval action:
  state compatibility and the durable upgrade backup are checked before any apt command. A refused backup or
  state contract leaves packages unchanged, and the install keeps the same backup (#146).
- Pull-mode deploy logs read version and commit labels independently, so whitespace in a version cannot forge the
  displayed commit. Pull and archive deploy logs show only printable ASCII labels without JSON escapes; other labels,
  including ordinary Unicode, control characters, quotes and backslashes, are suppressed independently without
  refusing deployment (#157).
- Deploys and first installs keep a safely stopped controller with a classified drive or limit fault, with a warning
  naming the fault and requiring repair and explicit `ClearFault`, instead of removing it or rolling back. Docker
  uses software availability; operational `/health` and `/health/ready` still fail on latched faults. Input/relay,
  initialization, authentication, HAT-mode, TLS and web checks remain strict; older images retain strict readiness (#94).

- The installer installs and unpacks every offline release asset from a private copy checked as it is written,
  including `hvo-roof`, the installer, the Linux authentication worker, the kiosk tarball and the Mac app zip.
  Replacing or modifying the source after verification cannot change the bytes used (#145).
- `restore` now makes what it puts back durable (each file and folder with its owner, then the folders above them)
  before the install that follows relies on it; a sync that fails stops it before that install (exit 1) (#130).
- An upgrade that stopped part way (the new controller deployed, the kiosk's download failed) left the record saying
  the old release, so `upgrade` to that release, or an older installer's install, wizard or `--plan`, could deploy the
  older controller again without `rollback`. The record now says the new release from before the upgrade's first
  change, marked `upgradingFrom` until it finishes, and its folder is synced to disk before that change, so a power
  loss does not take it back (on a Mac with `F_FULLFSYNC`, never `fsync` in its place, so the record's folder must be
  on a file system that supports it, as APFS on the Mac's own drive does); an upgrade that cannot sync it stops
  (exit 1) with nothing else changed. `upgrade` again
  finishes it, `rollback` goes back one release (with that release's files only), and anything older is refused
  (exit 3), by older installers too. Until it is finished or rolled back, another release is refused as well (exit 3),
  so that `rollback` still goes back to the controller the deploy script kept (#131).
- A `--plan` without `sudo` for the machine's roles checked your own record's release instead of the machine's: it
  refused a valid upgrade when your `hvo-roof` was newer, and skipped the check when you had no record. Each role's
  record is now checked (#131).
- **A controller that dies no longer leaves the drive running.** The SM-I-010 holds its relays as last written, so a
  controller crashed, killed or ended by the kernel during a move left the direction relay and the STOP permit
  energized until it started again, and for good when it could not start (a crash loop, a settings file it refuses).
  The container's supervisor now turns every relay off and verifies the register whenever the controller is not
  running: as soon as it exits, before each start, again after a failed try while the controller is not running, and
  when the supervisor stops or ends otherwise. It starts the controller only once a read-back shows every relay off:
  when they cannot be verified off (the HAT does not answer, or a relay stays on), the controller is held stopped
  (`relays-unverified`: a warning in the log, the health check failing, the web UI saying why) while the supervisor
  tries again after 1, 2, 4, 8 and 16 s, then every 30 s. The web UI's preparation (its certificate and Stop key
  copies, its keys directory, its environment) runs in the background within 10 s (`HVO_SUPERVISOR_UI_PREPARE_SECONDS`), so a hung
  filesystem can neither delay the relays nor a stop, and a stop abandons it and starts no web UI; a process that does
  not end at SIGKILL is left after 1 s. It uses the controller's own HAT access (`--relays-off`),
  never turns a relay on, and raises the web UI's and the controller's `oom_score_adj` above its own, so the kernel
  favours ending them before it when memory runs out. On the x64
  emulator rig the relays were off 0.10-0.17 s after a kill during travel (2.0-2.4 s before, when the restarted
  controller turned them off). A whole container killed (`docker kill`) still leaves them held until it starts again.
  The supervisor now waits 22 s (was 25 s) for the controller's shutdown, then up to 5 s for the relays, starts nothing
  once a stop is asked for, and times the whole stop from the signal, so it ends within 27 s, inside Docker's 30 s
  ([deployment.md](docs/deployment.md#the-relays-when-the-controller-exits), #138). That holds at the container's
  process limit too, where a fork would wait about 15 s and then end the supervisor: the stop forks nothing until the
  controller, the web UI and its preparation have ended (the web UI is now stopped at the same time as the controller),
  and at the limit a relay-off outside a stop first kills the web UI to make room, while starts wait for room
  ([deployment.md](docs/deployment.md#the-supervisor-at-the-process-limit)). Should something else hold the tasks
  then (a task stuck in the kernel, `docker exec`), the stop still ends within 27 s, but its relay-off may begin late,
  or not at all (logged).

### Security

- Persist commissioning retirement and guess limits; reject pending-state restore and schema-incompatible rollback.
  Native revalidation distinguishes confirmed account revocation from unavailable checks.
- The controller's container runs with a read-only root filesystem, no Linux capabilities but the supervisor's
  (`CHOWN`, `KILL`, `SETGID`, `SETUID`) and `no-new-privileges`, from the deploy script, the Compose profiles and so the
  installer. The supervisor's forced-restart directory is now root's, writable by the web UI's group. What a controller
  killed at its limits, or otherwise, leaves the relays and the drive in is recorded in
  [deployment.md](docs/deployment.md#the-relays-when-the-controller-exits) (#129): the supervisor now turns them off
  whenever the controller exits (#138, under Fixed).

## [4.1.1] - 2026-10-15

### Fixed
- Detailed CLI health and the terminal System page now authorize GET /health as a native read operation in local Linux mode (#118).
- Show the running GUI build in an always-accessible About view and keep local TUI/web versions visible when controller reads fail. Label controller metadata separately and suppress stale GUI/TUI values (#117).

## [4.1.0] - 2026-10-15

Installer readiness, optional native authentication and kiosk display updates (#111).

### Added

- Configurable validated DRM video mode, independent of UI scale; installer preserves the requested mode.
- Explicit verified sudo-caller enrollment and Linux-user role/access management in CLI, TUI and web.

- Ubuntu GRUB splash installation with official archive validation; explicit Pi-only real-HAT
  and rotation guards keep virtual machines unrotated and in emulation.

- Explicit kiosk-library setup consent, prerequisite guidance and readiness/reboot checks before application installation.
- Optional OS username defaults and interactive remote sign-in retry/cancel, preserving native local authorization and credential precedence.

- Optional Linux account sign-in and sudo-authorized local CLI access, with explicit controller enrollment,
  separate Linux and virtual identities, personal expiring scoped keys, and installer-managed native authentication.
  Existing installations retain their current authentication until the feature is enabled.
- Optional installer-managed image splash with mode-aware labels, bounded console handoff,
  explicit stable display choices, recovery backups and fresh-media setup guidance.

### Security

- Kiosk PIN sessions grant roof operation only; administrative accounts do not gain configuration or identity rights through a PIN.
- New virtual passwords require seven characters without composition rules; new personal PINs use four digits. Existing credential hashes and legacy PIN sign-in are preserved.
- Explicit emulator-only temporary kiosk grants cannot survive restart or return after a personal PIN is configured; no default credential is installed.

- Update Microsoft packages and container ASP.NET runtime to 10.0.13 for the SignalR security fix (GHSA-334f-vvp4-7g6p).

### Fixed

- Preserve live status for operation-only admin PIN sessions, and complete Ubuntu splash disable after restoring the distribution theme.

- Finish Plymouth console restoration before kiosk DRM initialization, preventing the later getty reset from cropping a requested video mode to the boot console resolution. Console recovery and terminal policies remain unchanged.

- Include and verify the configured HVO theme and script renderer in Ubuntu initramfs images,
  independently of the distribution theme alternatives.

- Verify Ubuntu initramfs files against bootable kernel images, ignoring leftover module directories
  from removed kernels; wait briefly for a concurrent package transaction.

- Order reboot, power-off, halt and kexec Plymouth startup after the kiosk releases DRM.
  Upgrade existing splash installations with new managed files and preserve their removal backups.

- Report effective kiosk rotation and managed splash state after installation, and distinguish
  service health from physical display acceptance. Document explicit Pi display choices for emulator installs.

- Default a fresh emulated rig kiosk to zero-degree rotation while preserving explicit and existing settings.

- Explain the separate per-user CLI installation after a controller, rig or kiosk install,
  and show the status and terminal-interface commands after installing the client.
- Accept the official Debian `.pgp` archive keyring used by Raspberry Pi OS Trixie when
  planning the optional splash, while requiring an official signing key in every archive entry.

## [4.0.2] - 2026-10-12

### Fixed

- Register the kiosk's HarfBuzz text shaper before DRM startup, so the display can render its text (#102).
- Include Blazor framework assets when Docker restores before copying Razor components, so the published web UI
  starts its live connection after sign-in instead of failing to load `blazor.web.js` (#102).

## [4.0.1] - 2026-10-01

Two messages corrected after the first install of 4.0.0 on the Pi (#95). What a deploy and
the installer do is unchanged.

### Fixed

- A first deploy no longer says that the previous version is kept for `--rollback`: the
  deploy script now says there is none, or that an older `roof-controller-previous` is left as
  it was (#95).
- When a running controller accepts none of the keys the installer has, the installer's
  message now leads with stopping the controller by hand, which always works. Copying its
  operator key is offered second, for a controller that has one (#95).

## [4.0.0] - 2026-10-01

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

- One product version, 4.0.0, for every program and image (#62). It is set in `Directory.Build.props`; a workstation
  build is `4.0.0-dev`, a CI build `4.0.0-ci.<run>`, and every build carries its commit (`4.0.0-dev+<sha>`).
  `hvo-roof --version`, the controller's startup log and the System pages of the web UI, the terminal interface, the
  kiosk and the Mac app show it; both images carry it in their OCI labels (`org.opencontainers.image.version`,
  `.revision`, `.created`, `.source`), which the image workflows check; the Mac app's `Info.plist` has `4.0.0`
  (`bundle.py --version`); `hvo-roof info --json` gives `hvo-roof`'s own in `client.version`. `build/version.sh`
  reads it, and checks that a release tag names it. See [docs/releasing.md](docs/releasing.md).
- Released images (#63). With `IMAGE_REF=<registry>/<name>[:<tag>]@sha256:<digest>`, `deploy-roofcontroller-rpi.sh`
  pulls a released image into the Pi's Docker instead of building one, deploys it only if Docker reports that digest
  and the Pi's platform for it, and prints its version; it needs no checkout, and runs on the Pi itself with
  `DOCKER_CONTEXT=default`. A reference without a digest is refused; `--rollback` and `--verify-remote` ignore it.
  `build/release-compose.py` makes a release's compose file: the Pi compose file with the released GHCR images and
  nothing built, for the Pi profiles and the `emulator` profile, which CI checks against the source profile by profile,
  with and without the digests. The controller's image is now built for `linux/amd64` as well as `linux/arm64`. The
  container scenarios deploy a two-platform image index from a local registry by its digest (C12 step 11). See
  [docs/deployment.md](docs/deployment.md#deploying-a-released-image).
- The release workflow (#64). A tag `vX.Y.Z`, or `vX.Y.Z-rc.N`, on main runs CI (building every program with the
  release's version) and the scenarios for that commit; then builds both images for `linux/amd64` and `linux/arm64`,
  pushes them to `ghcr.io/hualapaivalley` with the version as their tag and attests them, and makes a draft release of
  `hvo-roof` for Linux arm64 and x64, the kiosk's tarball, the Mac app's zip, the release compose file and the deploy
  script, with `release.json` (the version, the commit, each image's digest and each asset's SHA-256), `SHA256SUMS` and
  a build provenance attestation. A final release needs `docs/upgrade-notes/X.Y.Z.md` and its CHANGELOG section; its
  notes start with the upgrade notes. The release job runs in the `release` environment, whose reviewers and allowed
  refs an admin sets before the first release. Publishing the newest final release moves each image's `latest` tag
  (`release-latest.yml`); a release candidate, or a fix to an older version, leaves it. A manual run is a dry run,
  `X.Y.Z-dryrun.<run>`, ending in a draft to delete. `build/release-assets.py` gathers the assets,
  `build/push-image.sh` pushes an image from its OCI archive with the digest it was checked with, and
  `build/release-latest.sh` decides whether `latest` moves; CI tests all three. See
  [docs/releasing.md](docs/releasing.md).
- Trusting a private CA (#65). The client library's `ServerCaCertificate` trusts only the CA that issued the
  controller's certificate, such as the installer's: the certificate must chain to it and name the host, the CA's name
  constraints apply, and a certificate reissued under the same CA needs no change at the clients. It covers requests,
  Stop and the status hub, and replaces the pin where a CA is in use: setting both is refused. `hvo-roof setup
  --ca-certificate <file>` saves the CA in the credentials file (or `HVO_ROOF_CA_CERT` names the file), and the kiosk
  and the Mac app take `ServerCaCertificateFile`. A certificate that is not accepted says why: another CA, expired,
  another host name, outside the name constraints, or not the pinned one, and `hvo-roof` names the option that would
  help. See [docs/cli.md](docs/cli.md#setup).
- `hvo-roof` on a Mac (#66). CI publishes `hvo-roof` for `osx-arm64` as well, signs it ad hoc (rcodesign, as the Mac
  app) and checks the signature, and the release adds it as `hvo-roof-osx-arm64`. A `mac-cli` job on a macOS runner
  checks the signature and the version with `codesign`, runs the terminal smoke test against a controller and the HAT
  emulator started with `dotnet run` (`tests/cli/dotnet-run-smoke.sh`), and runs the certificate tests (the pin, the
  CA and the name constraints) on macOS; the smoke test's screens are kept as `mac-terminal-screens-<run id>`. The
  smoke test now also checks that `setup` keeps the credentials file at mode 600 in a folder at 700. See
  [docs/cli.md](docs/cli.md#on-a-mac).
- The installer, `hvo-roof-install` (#67). One self-contained file for Linux arm64 and x64 and Apple silicon Macs, which
  CI publishes as `hvo-roof-install-<run id>` and checks with `--version` and `--plan`. With no options it opens a
  wizard in HVO Dark: what it found on the machine (the Pi and the HAT's devices, Docker, the install records,
  `/etc/hvo-roof`, the containers and who made them, the kiosk's service, `hvo-roof` and the Mac app), the roles this
  machine may have (each one it cannot have says why), their choices, the plan to review, the install and Done.
  `--answers FILE` installs from the answers file the review page saves, which never holds a secret, and `--plan`
  prints every folder, file, container, service and port an install would make or change and changes nothing. It
  never drives the real HAT anywhere but a 64-bit Pi with its devices, never installs a test rig where the real HAT is,
  asks for the host name before a rig on a machine with the HAT's I2C bus, installs the machine's roles only as root
  and the person's never as root, and replaces no container that Docker Compose made. Each step changes only what
  differs, so a second run changes nothing. It records what it installed in `/etc/hvo-roof/install.json` (the
  controller, a rig on Linux, the kiosk) or `~/.config/hvo-roof/install.json` (`hvo-roof`, the Mac app, a rig on a
  Mac), and logs each command and change with no secret. It makes the controller's folders and adopts a controller the
  deploy script runs; it deploys the controller and a rig (#69), the kiosk (#70), and `hvo-roof` and the Mac app (#71).
  CI keeps the wizard's pages as `installer-renders-<run id>`. HVO Dark for the terminal moves to
  `HVO.RoofControllerV4.TerminalUi`, which `hvo-roof ui` and the installer share. See [docs/install.md](docs/install.md).
- Certificates in the installer (#68). The controller's connection is a private CA (the default), your own certificate,
  a self-signed certificate, or plain HTTP, which must be confirmed by typing `http`. The installer makes the CA
  (ECDSA P-256, ten years, its key owner-only in `/etc/hvo-roof/ca`, its certificate in `/etc/hvo-roof/ca.crt`), whose
  name constraints let it issue only for `local`, `localhost`, the machine's short names, the domains you give and
  private addresses. It issues the controller's certificate (397 days) for the host name, the names and domains you
  give, `.local`, `localhost` and the machine's private addresses, and writes it as `roof-controller.pfx` with a random
  password that is never shown. `sudo hvo-roof-install cert` renews what needs it (within 30 days of expiry, a name
  that changed, a CA that no longer covers it; `--renew`, `--new-ca`), with the defaults when nothing is recorded yet
  (keeping a certificate this machine's CA did not issue), and refuses while the clock is before the CA's start or a
  record cannot be read. `cert show` prints what is served and the SHA-256 fingerprints, and `cert import FILE` puts
  your own certificate in place (PKCS#12, or PEM with its chain and key), refusing one that has expired, is not yet
  valid, is a CA's or is not for a server, and a key needing more than 300,000 iterations to open. `cert` and
  `cert import` each have `--plan`, and each redeploys a controller the installer runs to serve the new certificate,
  once the roof is idle and you agree (#69). The wizard's settings page asks
  for the names and domains, listing no domain unasked and naming those the machine's resolver searches as a hint.
  Done shows where clients get the CA and its fingerprint. The controller serves its CA at the anonymous `GET /ca.crt`
  (the handshake leaves a self-signed root out), and `/health` reports `https_certificate`:
  Degraded within 30 days of expiry, Unhealthy when it has expired or cannot be read. See
  [docs/install.md](docs/install.md#certificates).
- Deploying with the installer (#69). On the observatory's Pi it deploys the controller from the release's image, by its
  digest, through the deploy script, which replaces a running controller only after a verified Stop and, with
  `REQUIRE_IDLE_ROOF` (which the installer sets), only while the roof is idle. It makes the controller's API keys, the
  deploy script's operator key among them, straight into files only root reads, adds the first admin with a password
  and, if you choose, a PIN, which it never shows or logs, writes the camera's secrets, and can start from the settings
  file of a backup (`controller.importSettingsFrom`). A test rig runs the controller against the release's HAT emulator:
  on Linux (amd64 or arm64) as root, or on an Apple silicon Mac with Docker Desktop as you. Each install reads the
  release's `release.json` from GitHub, or from `--release DIR`. `cert` redeploys the controller to serve a new
  certificate once the roof is idle and you agree (`--redeploy`, `--no-redeploy`). The `installer-rig` job installs a
  rig end to end against the HAT emulator. See [docs/install.md](docs/install.md#running-it).
- The kiosk from the installer (#70). On the controller's Pi it installs the kiosk's packages, its `hvo-kiosk` user, its
  program from the release (checked by its SHA-256), its device key (a Viewer key only `hvo-kiosk` reads), its settings
  (the controller on `localhost`, trusting the installer's CA or pinning the certificate), its service and the
  backlight's udev rule, and gives each person in `kiosk.pins` a PIN. It refuses the kiosk without a connected screen,
  or while a desktop holds the display now or at the next boot. `cert` re-pins a self-signed certificate it renews and
  restarts the kiosk. See [docs/install.md](docs/install.md#the-kiosk).
- `hvo-roof` and the Mac app from the installer (#71). As you, it installs `hvo-roof` (on Linux or an Apple silicon Mac)
  and the Mac app (on the Mac) from the release, connected to the controller and trusting its CA once you have checked
  the CA's SHA-256 fingerprint. An admin signs in once to make the Mac's device key (`mac-HOST-USER`, a Viewer key),
  which is written to a file only you read and never shown or put on the clipboard. With `client.trustInKeychain` it
  trusts the CA in your login keychain for Safari and Chrome. The app opens once with `--check` when the Mac's own
  screen is there to open it on. The `installer-mac` job installs the app on a macOS runner. See
  [docs/install.md](docs/install.md#hvo-roof-and-the-mac-app).
- Upgrading, rolling back, backing up, restoring and uninstalling (#72). `hvo-roof-install upgrade` shows each release's
  upgrade notes, puts the new release's installer in place and hands over to it, which deploys the new controller
  through the deploy script and keeps what it replaces; `rollback` goes back one release, with the deploy script's
  `--rollback`. `backup` writes one root-only archive of `/etc/hvo-roof`, `/var/lib/hvo-roof` and the kiosk's key and
  settings, listing each file's mode, owner and SHA-256, and reads it back before keeping it; it prints only paths and
  counts. `restore FILE` puts it back on a new Pi, keeping the CA for the same host name. `uninstall` stops the
  controller after a verified Stop and removes what was installed, keeping the data; `--purge` removes the data too,
  once the machine's name is typed and a backup is made or declined. The upgrade notes come from the new release's
  `release.json`, which carries every release's notes from `docs/upgrade-notes`. See
  [docs/install.md](docs/install.md#upgrading).
- `install.sh`, the one-line install (#73). Each release carries it: it checks the platform, the roles, its commands,
  sudo, the free space, the network and the clock, ghcr.io, Docker and I2C, before it downloads anything; offers to
  install Docker or turn I2C on only once every other check has passed (`--install-docker`, `--enable-i2c`); and runs
  the release's installer for this machine only when its SHA-256 matches `SHA256SUMS`, its version is the release's
  and, with `gh` signed in, its attestation says the release workflow built it from the release's tag. `--check` only
  checks, and `--from DIR` takes the release from a folder. Its tests (`tests/install/install-sh-tests.sh`) run on
  Linux and macOS. See [docs/install.md](docs/install.md#installsh).
- The install end to end (#74). `tests/installer/rig-scenario.sh` installs a rig with `install.sh`, runs it again,
  connects `hvo-roof`, opens, stops part way and closes the emulated roof, changes its time scale, renews the
  certificate, changes the rig's addresses and host name, backs up, restores, upgrades and rolls back, on Linux amd64
  and arm64 runners. The release workflow's `e2e` job runs it on both, without the upgrade and rollback, against the
  draft release's own assets and images, and a draft is published only once that job has passed. See
  [docs/install.md](docs/install.md#the-rig-end-to-end).
- The install guide (#75). [docs/install.md](docs/install.md) starts with preparing the Pi (Raspberry Pi OS Lite,
  64-bit, with Raspberry Pi Imager; a fixed address) and has a troubleshooting section; the README starts with the
  one-line install. CI checks every relative link and anchor in the Markdown files (`build/check-doc-links.py`).
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
- Touchscreen kiosk (#47). `hvo-roof-kiosk` (`HVO.RoofControllerV4.Kiosk`) shows the roof full
  screen on a Raspberry Pi touch display, drawn through DRM with libinput touch and no desktop,
  a client of the controller's API and status hub with a device key of its own (a `RoofViewer`
  key marked `Kiosk`). Anyone can watch the roof and press Stop; a person unlocks it with their
  name and PIN for the operator's and admin's pages (Roof, Settings with a keypad and keyboard
  drawn on the screen, System and Restart). It locks after `IdleLockSeconds` without a touch,
  when the controller ends the PIN session, and on SIGTERM, and stops renewing the lease when it
  locks. The screen goes black after the controller's `KioskScreenTimeout`, and the touch that
  wakes it goes no further. Every button is at least 12 mm. Its screens
  (`HVO.RoofControllerV4.Screens`) are Avalonia controls in HVO Dark, drawn headless at
  1280x720 and 800x480 in the tests, which check the layout and Stop and take the screenshots in
  [docs/kiosk.md](docs/kiosk.md); a nightly soak runs it against the emulated roof through
  controller restarts. See [docs/kiosk.md](docs/kiosk.md).
- Mac app (#48). `HVO Roof.app` (`HVO.RoofControllerV4.Mac`) is the kiosk's screens in a window
  on a Mac with Apple silicon, with the Mac's own device key (an ordinary `RoofViewer` key, not a
  kiosk key). Anyone at the Mac can watch the roof and press Stop; a person signs in with their
  name and password, and the app signs them out after `IdleLockSeconds` without use (15 minutes
  by default), when the controller ends the session, and when it quits. Settings are typed in a
  text box. Its settings and device key are in `~/Library/Application Support/HVO Roof`;
  settings it cannot start with open a window that says why and where they are (exit code 78).
  It is built on Linux without Xcode: `bundle/bundle.py` makes the bundle from the osx-arm64
  publish and signs it ad hoc with rcodesign, and checks it; the icon is drawn in code. CI keeps
  the signed bundle as the `hvo-roof-mac-<run id>.zip` artifact, and a macOS job checks its
  signature with `codesign` and runs it with `--check`, which opens and draws the window. See
  [docs/mac.md](docs/mac.md).
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

- `GET api/v1.0/System/info` reports the product version and commit in `applicationVersion`
  (`4.0.0-dev+<sha>`), not the assembly version, which was `1.0.0.0` (#62).
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
