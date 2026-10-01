# Agent instructions: HVO.RoofController

These are the instructions for every coding agent that works in this repository. Claude Code reads them through
[`CLAUDE.md`](CLAUDE.md) and GitHub Copilot through
[`.github/copilot-instructions.md`](.github/copilot-instructions.md); Codex and other agents read this file directly.
The controller adds its own rules in [`src/HVO.RoofControllerV4.RPi/AGENTS.md`](src/HVO.RoofControllerV4.RPi/AGENTS.md).
People contributing by hand will find the same workflow in [CONTRIBUTING.md](CONTRIBUTING.md).

Keep this file true. When a change makes something here wrong (a project, a workflow, a rule), fix it in the same pull
request. What the docs already explain is linked from here, not repeated.

## Rules that come first

- **Never move the real roof.** No agent sends Open or Close to a controller that drives the real HAT, unless a person
  has said, for that task, that no roof or drive is connected to it; nothing an agent writes (a test, a script, a
  deploy step) may do it either. Reading the status and sending Stop are fine. The installer never moves the roof.
  Motion is tested against the emulator.
- **No test relies on hardware.** Every test, check and commissioning step runs against the emulated HAT, roof and VFD
  ([Testing without hardware](#testing-without-hardware)).
- **The repository is public.** Never commit, print, log or post a credential, key, PIN, token or private address, and
  never show one in a screenshot. Run `build/secret-scan.py` before every commit, push, issue, pull request and comment
  ([Secrets](#secrets)).
- **Keep the working drive setup.** The Lenze/AC Tech SMVector VFD's parameters and the control wiring in the
  [hardware overview](docs/projects/roof-controller-v4-rpi/hardware-overview.md) work today, and the software and the
  emulators follow them as they are. Change them only for a major issue, such as a safety gap or a mismatch that stops
  the roof working. Offer improvements (ramps, current limits, braking, preset speeds, position sensing) as separate,
  optional recommendations, each with its trade-offs and how the emulator could test it.
- **Publishing is a person's job.** Agents never approve the `release` environment, publish a release, change a
  package's visibility, or delete or move a published tag ([Releases](#releases)).

## The project

HVO.RoofController automates the observatory's roof. A controller on a Raspberry Pi drives the roof through a relay
and input HAT and a VFD, and serves an authenticated REST API and a live SignalR status hub. Every user interface is a
separate client of that API and hub. The whole system also runs against an emulated HAT, roof and VFD, on any Linux or
macOS machine with Docker.

### Projects

| Project | What it is |
|---|---|
| `HVO.RoofControllerV4.RPi` | The controller: ASP.NET Core on the Pi. It drives the Sequent Microsystems HAT over I2C (`HVO.Iot.Devices`) and serves the REST API and the status hub. It serves no pages. Its own rules: [AGENTS.md](src/HVO.RoofControllerV4.RPi/AGENTS.md). |
| `HVO.RoofControllerV4.Web` | The web UI, Blazor Server: a separate process in the controller's container ([docs/web.md](docs/web.md)). |
| `HVO.RoofControllerV4.Client` | The client library every UI uses: the REST API, the status hub, credentials and the CA, Stop, and the shared wording and palette. |
| `HVO.RoofControllerV4.Cli` | `hvo-roof`: the commands and the Terminal.Gui interface ([docs/cli.md](docs/cli.md)). |
| `HVO.RoofControllerV4.TerminalUi` | HVO Dark for Terminal.Gui, shared by `hvo-roof` and the installer. |
| `HVO.RoofControllerV4.Screens` | The Avalonia views, view models and HVO Dark theme shared by the kiosk and the Mac app. |
| `HVO.RoofControllerV4.Kiosk` | `hvo-roof-kiosk`: the touchscreen on the Pi, full screen with no desktop, controls behind a PIN ([docs/kiosk.md](docs/kiosk.md)). |
| `HVO.RoofControllerV4.Mac` | `hvo-roof-mac`: the kiosk's screens in a window on a Mac, built and signed ad hoc on Linux ([docs/mac.md](docs/mac.md)). |
| `HVO.RoofControllerV4.Installer` | `hvo-roof-install` and `install.sh`: installs, upgrades, rolls back, backs up, restores and uninstalls every role ([docs/install.md](docs/install.md)). |
| `HVO.RoofControllerV4.Common` | The models, options and DTOs the controller and its clients share. |
| `HVO.RoofControllerV4.Simulation` | The emulated drive, limit switches, HAT and wiring, used by the tests and the emulator. |
| `HVO.RoofControllerV4.Emulator` | The HAT emulator: the emulated plant's HAT registers over TCP, and a control API for fault injection ([docs/emulator.md](docs/emulator.md)). |
| `HVO.WebSite.Themes` | A Razor class library with the web UI's HVO Dark CSS. |

```text
src/                          # the projects, HVO.RoofController.sln, global.json, docker-compose.yml (development)
tests/
  HVO.RoofControllerV4.RPi.Tests/   # every .NET test (MSTest): unit, API, UI, installer, emulated plant and scenarios
  cli/ container/ deploy/ emulator/ install/ installer/ mac/   # shell and Python tests of the scripts and containers
  docs/ releasing/ security/ versioning/                       # tests of the build/ scripts
build/                        # version, release, image, doc-link and secret-scan scripts
docs/                         # how everything works; start from README.md's documentation table
```

### Key safety systems

Describe these exactly as implemented; do not call the watchdog a dead-man control.

- Maximum-run watchdog (`SafetyWatchdogTimeout`, 5-600 s): an absolute cap measured from the start of a movement.
  Repeating Open or Close in the same direction does not extend it. On expiry the roof stops and the fault latches.
- Operator lease (optional, `OperatorLeaseTimeout`, 2-120 s; off when unset): Open and Close start a lease that the
  client renews with `POST .../Lease` while the roof moves. If renewals stop, the roof stops with
  `OperatorLeaseExpired`. Renewal never starts motion. Losing the client stops motion only when the lease is enabled.
- Limit switches (IN1/IN2): stop at fully open and fully closed; polarity is set by `UseNormallyClosedLimitSwitches`
  (production `false`: the ME-8108 normally open pair). Starting at a limit is handled as a departure phase;
  contradictory limits (both active) stop motion and refuse starts. The optional `DepartureReleaseTimeout` stops a
  start limit that has not released in time with `DepartureLimitNotReleased`.
- Fault latch: watchdog expiry, drive fault (IN3), relay verification failure, input read failure, contradictory
  limits, a reasserted start limit, the drive-running check (`DriveNotRunning`) and an unreleased start limit latch
  the fault. Open and Close are refused (`FaultLatched`) until a successful `ClearFault`. The latch never blocks Stop,
  and Stop does not clear it.
- Relay register read-back: every relay transition is verified by reading the HAT's relay register back. This proves
  the register, not the physical contacts; never describe it as contact verification. An unverified stop is reported
  as a failure (`RelayStateUnverified`), never as success.
- VFD fault input (IN3): polarity is `FaultInputActiveHigh` (code default `true`: raw HIGH = fault; production `false`
  for the `P140 = 3` fault relay, closed while healthy).
- Drive-running interlock (IN4): with `AtSpeedConfirmationTimeout` set (production 3 s), a start is refused with
  `InterlockActive` while IN4 reports running, IN4 must assert within the window after a start, and IN4 low for
  250 ms while moving without the destination limit stops the roof; the last two latch `DriveNotRunning`. IN4 still
  high `DriveStopConfirmationTimeout` after a stop is logged as Critical.
- Input read failures: `MaxConsecutiveInputReadFailures` consecutive failed reads while moving stop the roof with
  `InputReadFailure`.
- Relay register reads: supervision re-reads the register. One failed or stale read marks relay reads unhealthy
  (readiness fails; the last verified state is kept); two consecutive failures stop motion and latch
  `RelayVerificationFailed`.
- Repeated commands: a repeated same-direction Open or Close and `RenewLease` enforce the watchdog, lease and at-speed
  deadlines before renewing; a repeat never revives an expired lease.
- Shutdown: the host requests a verified stop. An unverified one is retried (all-off every 500 ms until verified,
  disposal or 15 s); the host's wait is bounded even if HAT I/O blocks, and disposal stops waiting for a lock held by
  blocked HAT I/O after 2 s, deferring its all-off stop until that call returns.
- `IgnorePhysicalLimitSwitches` is for local simulation. On physical hardware it is refused unless
  `AllowIgnoringLimitSwitchesOnPhysicalHardware` is set in local configuration (never through the API).
- None of this replaces an independent hardware stop path (E-stop, the VFD's STOP input); see
  [docs/commissioning.md](docs/commissioning.md).

### HTTP API

- Routes are under `api/v4.0/RoofControl`. Commands (`Open`, `Close`, `Stop`, `ClearFault`, `Lease`) are `POST`;
  `GET Status` and `GET`/`POST Configuration` complete the surface. There are no GET command routes.
- Every protected request needs an `X-Api-Key` header or a person's session (`Authorization: Bearer`). Roles are
  `RoofViewer`, `RoofOperator` and `RoofAdmin` (Admin includes Operator includes Viewer); Stop accepts any
  authenticated role. Failures are RFC 7807 ProblemDetails with `code` and `roofStatus` extensions.
- `POST Configuration` requires `ExpectedVersion`, and safety-critical changes also need
  `ConfirmSafetyCriticalChange`.
- Keys, roles, people, sessions and HTTPS: [docs/security.md](docs/security.md).

### Technology

- .NET 10, with the SDK pinned by `src/global.json`; ASP.NET Core and SignalR (the controller), Blazor Server (the web
  UI), Avalonia (the kiosk and the Mac app), Terminal.Gui (`hvo-roof` and the installer).
- `HVO.Core`, `HVO.Core.SourceGenerators` and `HVO.Iot.Devices`, NuGet packages from HVO.SDK.
- Docker for the controller and the emulator, with images on GHCR.
- Tests: MSTest, FluentAssertions, Moq, bUnit, Playwright (Chromium), Avalonia headless; Bash with ShellCheck and
  Python `unittest` for the scripts.

### Platforms

Linux x64, Linux arm64 and macOS arm64 only: there are no Intel Mac builds, so never add `osx-x64`. The Pi is the only
machine that drives the real HAT; the controller, the web UI and the emulator run anywhere else in Docker against the
HAT emulator, and `hvo-roof` and the Mac app run natively. Versions follow SemVer from 4.0.0.

## Building and testing

Run `dotnet` from `src/` so `src/global.json` selects the SDK:

```bash
cd src
dotnet build HVO.RoofController.sln
# What CI's build-and-test job runs; the scenario, browser and soak categories run in scenarios.yml
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj \
  --filter "TestCategory!=Scenario&TestCategory!=Browser&TestCategory!=Soak"
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj \
  --filter "TestCategory=Scenario"
# Release treats warnings as errors; CI builds both configurations
dotnet build HVO.RoofController.sln -c Release
```

The browser tests need Chromium from Playwright
(`pwsh ../tests/HVO.RoofControllerV4.RPi.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`). The
scripts have their own tests, which CI runs; run the ones for what you change, from the repository root:

```bash
python3 -m unittest discover -s tests/<docs|releasing|security|cli|mac> -p 'test_*.py'
tests/versioning/version-tests.sh
tests/deploy/deploy-script-tests.sh
build/check-doc-links.py          # every relative link and anchor in the Markdown resolves
shellcheck <the shell script>
```

To run the controller locally, start the HAT emulator first: `dotnet run --project HVO.RoofControllerV4.Emulator`
(its registers on port 5291, its control API on 5290), then `dotnet run --project HVO.RoofControllerV4.RPi`
(port 5195). `src/docker-compose.yml` runs both in containers (port 5200); it is for development only, never for the
observatory's Pi.

## Testing without hardware

- No test, check or commissioning step relies on physical hardware, in CI or on the Pi. Control and safety logic is
  tested against the emulated HAT, roof and VFD, with the limit switches, the drive-fault input and the drive-running
  input driven by the emulated plant.
- Model each emulated device on its vendor documentation (the VFD's manual, the HAT's documentation, the wiring in the
  [hardware overview](docs/projects/roof-controller-v4-rpi/hardware-overview.md)), not on the controller's code, so the
  emulator catches a controller that disagrees with the real device. Where the documentation leaves a behaviour open,
  say so and model the fail-safe reading.
- What software cannot prove (wiring polarity, that the relay contacts switch, the independent stop path, real
  timings) is an installation assumption, tied to the setting it depends on and listed in
  [docs/commissioning.md](docs/commissioning.md). Where possible, show in emulation that a wrong assumption fails safe,
  for example the plant wired the wrong way.
- Never describe a check as needing hardware, and never propose bench, meter or scope steps. Something the emulator
  cannot do yet is a gap to fill in the emulator.
- A change a commissioning check covers needs its scenario on the emulated plant, marked `[CommissioningCheck]` and
  listed in `docs/commissioning.md`; `ScenarioCoverageTests` fails when the two disagree.

## User interfaces

- Every UI (the web UI, the kiosk, the Mac app, `hvo-roof`) reaches the controller only through its REST API and status
  hub, through `HVO.RoofControllerV4.Client`. None runs in the controller's process. Behaviour belongs in the API or
  the client library.
- Stop is always visible, never disabled, goes straight to the REST API, and is worded the same everywhere. The hub
  pushes full status snapshots; commands stay on REST.
- All the UIs share one look, HVO Dark, set by the web UI. Its source is `hvo-dark.css` on the `main` branch of
  HualapaiValley/HVO.SkyMonitor, fetched with `gh api`, not a local copy (the copies may be out of date). Define a
  colour once, as a documented token, and reuse it in the Terminal.Gui and Avalonia themes.
- Each UI's doc page shows its main screens. The screenshots are made from the emulated controller by a script or test,
  so they can be made again, and look like the real UI, colour included. A screenshot never shows a real credential,
  key, PIN or private address. When the screens or the theme change, make the screenshots again.
- Work on the UIs runs headless, since development is over SSH: Playwright for the web UI, Avalonia's headless platform
  for the kiosk and the Mac app, and tmux or Terminal.Gui's test driver for the terminal.

## Deploying and installing

- `hvo-roof-install` sets up every role (the controller on the Pi, a test rig with the HAT emulator, the kiosk,
  `hvo-roof`, the Mac app); `install.sh` downloads and starts it ([docs/install.md](docs/install.md)). It is a
  separate program, not a subcommand of `hvo-roof`. It installs the controller through the deploy script, so the
  deployment check, the verified Stop, the previous version and rollback are the deploy script's.
- `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh` deploys the controller, built from source or pulled from
  GHCR: it runs the image's `--validate-deployment` check first, requires a verified Stop before replacing the
  container, verifies the new one, and restores `roof-controller-previous` on any failure
  ([docs/deployment.md](docs/deployment.md)). Keep the script, its tests (`tests/deploy/deploy-script-tests.sh`) and
  the compose file (`src/HVO.RoofControllerV4.RPi/docker-compose.yaml`) in step.
- With HTTPS only port 8443 is published; plain HTTP on the LAN is an explicit opt-out (`ALLOW_INSECURE_HTTP=true`,
  compose profile `pi-lan-http`). HAT emulator mode is set only through `HAT_EMULATOR_ENDPOINT` with
  `ALLOW_EMULATED_HAT=true` ([docs/emulator.md](docs/emulator.md)).
- The wiring-dependent settings change only through [docs/commissioning.md](docs/commissioning.md).
- Access details for the observatory's machines (addresses, keys, accounts) are not in this repository; the owner keeps
  them. Never add them.

## Releases

Follow [docs/releasing.md](docs/releasing.md). In short:

1. **The version bump** (`VersionPrefix` in `Directory.Build.props`), in a pull request of its own.
2. **The release pull request:** the CHANGELOG's `## [Unreleased]` becomes the version and the day, with a new, empty
   `## [Unreleased]` above it, and `docs/upgrade-notes/<version>.md` says what someone upgrading must do (or that
   there is nothing to do).
3. **The tag:** an annotated `v<version>` tag on the merged commit on main starts `release.yml`, which makes a draft
   release and installs a rig from it end to end.
4. **A person** approves the `release` environment, checks the draft
   ([Before publishing](docs/releasing.md#before-publishing)) and publishes it. Publishing moves the images' `latest`
   tag (`release-latest.yml`).

A published release is final: never delete it, move its tag or push its images' version tags again; fix a mistake in
the next version.

## CI

The workflows and their triggers are in [docs/ci-runners.md](docs/ci-runners.md). Every pull request runs `ci.yml` and
`scenarios.yml`, and `pi-image.yml` and `emulator-image.yml` when it touches their inputs; all of them must pass before
merging. In a workflow:

- Pin every action to a full commit SHA with a `# vX.Y.Z` comment; the repository rejects tag references.
- Keep `permissions: contents: read` at the top, and give a job more only when it needs it.
- Stay on GitHub-hosted runners; never attach a self-hosted runner to this public repository.

## Working in git and GitHub

- Name the repository in each `gh` command: `gh -R HualapaiValley/HVO.RoofController ...`, or
  `gh api repos/HualapaiValley/HVO.RoofController/...`.
- **One branch and one pull request per issue** (or per task, such as a release), from an up-to-date `main`. Name
  the branch `<type>/<short-name>`, with the type one of `feat`, `fix`, `chore`, `docs`, `test`, `refactor` or
  `release`, and the issue's number where it helps (`fix/installer-small-95`). Work in a git worktree when more than
  one task is in flight.
- **Commits** follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`,
  `refactor:`, `chore:`). To set work aside, commit it (a WIP commit), never a bare `git stash`. To bring a branch up to
  date, merge `main` into it. Never force-push or rewrite `main`.
- **The pull request** fills in the [template](.github/PULL_REQUEST_TEMPLATE.md), and its description says
  `Fixes #<n>` so that merging closes the issue.
- **The CHANGELOG.** A change a user or operator would notice adds its entry under `## [Unreleased]` in
  [CHANGELOG.md](CHANGELOG.md), under a Keep a Changelog heading (Added, Changed, Fixed, Removed, Security), in the
  same pull request, with the issue's number. A change only contributors see (tests, CI, contributor docs) needs none;
  say so in the pull request.
- **Docs and screenshots** change in the same pull request as the behaviour they describe.
- **Merging.** When every check has passed and the review's findings are dealt with, merge with a merge commit
  (`gh pr merge <n> --merge`), not a squash or a rebase. Then check that the issue closed; if not, close it with a
  comment naming the pull request. Delete the branch: the repository does not.

### Reviews

When asked to review a pull request or the repository, post the review on GitHub, not only in the terminal:

- one summary comment listing every finding, the most severe first;
- one comment per finding, anchored to the file and line when the line is in the diff (otherwise naming `path:line`),
  linking to the summary, with the failure it causes and a suggested fix.

## Secrets

- Run `build/secret-scan.py` before a commit, a push, and posting an issue, a pull request or a comment. It looks in
  the files git tracks or would add, every commit since the old Blue Iris credential's (#22), and any file you name.
  Write a body to a file and scan it before posting: `build/secret-scan.py body.md`. It must print `PASS`. It names
  where a secret is, never the secret.
- The camera proxy's old Blue Iris credential is still in the history (#22); never copy it out.
- Never print a secret to check it: check that a file exists, its permissions, a hash, or the status a command
  returns.
- Pass a token on standard input or in a file only its user can read, never as a command-line argument: arguments
  show in `ps` and in shell history.
- Keys, passwords and PINs are generated and written straight to files only their user can read; a person types only
  passwords and PINs. None goes in an answers file, a plan, a log or a test's output.

## Coding standards

- Follow the code around you: its naming, its comments, its patterns.
- Packages are managed centrally in `Directory.Packages.props`, and build settings in `Directory.Build.props`.
- Public types and members carry XML documentation comments, as the code around them does (the build does not enforce
  it: `CS1591` is suppressed).
- Keep controllers thin: logic belongs in services. Use dependency injection throughout.
- Keep hardware behind interfaces that the tests and the emulator can fake.
- Warnings are errors in Release.

## Dev container

`.devcontainer/devcontainer.json` is the standard environment: no devices; run the controller against the HAT emulator.
`devcontainer.rpi.json` runs on a Pi with the real I2C bus and GPIO, so it can move the roof: use it only when a person
asks, with the roof mechanism isolated. Do not install tools or extensions into the dev container without changing its
configuration.
