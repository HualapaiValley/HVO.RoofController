# Roof Controller V4 Test Suite

This test project validates safety, motion control and API-facing status semantics for `RoofControllerServiceV4`,
its hosted service and its health check, and runs the controller against an emulated roof, drive and HAT. No test
relies on physical hardware.

## Documentation

- [`docs/projects/roof-controller-v4-rpi/hardware-overview.md`](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring map and relay/limit switch context for the test assumptions.
- [`src/HVO.RoofControllerV4.RPi/README.md`](../../src/HVO.RoofControllerV4.RPi/README.md) – configuration options and their limits, as the validator and controller tests enforce them.
- [`src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.http`](../../src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.http) – the REST requests the controller API tests cover.
- [`docs/commissioning.md`](../../docs/commissioning.md) – the checks each emulated-plant test stands in for.

## Wiring and polarity assumptions

- Limit switches are **normally closed (NC)** by default in code: raw HIGH = circuit closed (not at the limit), raw LOW
  = limit engaged. `UseNormallyClosedLimitSwitches = false` inverts this. Production uses `false`: IN1/IN2 are on the
  ME-8108 normally open pair, HIGH at the limit.
- Inputs (raw electrical):
  - IN1: open limit
  - IN2: closed limit
  - IN3: drive fault. Active HIGH by default in code (`FaultInputActiveHigh = true`). Production uses `false`: the
    drive's fault relay (`P140 = 3`) is closed while healthy, so IN3 is LOW when faulted.
  - IN4: drive running (`TB-14`, `P142 = 1`). Enforced only when `AtSpeedConfirmationTimeout` is set (production 3 s).
- The service tests set polarity explicitly where it matters; the plant tests load the production `appsettings.json`.
- Relays:
  - RLY1: open direction
  - RLY2: close direction
  - RLY3: clear-fault pulse
  - RLY4: STOP permit (de-energized = stop asserted)

`FakeRoofHat` starts with all inputs LOW, which with NC switches means **both limits active** (contradictory). Tests
that need mid-travel call `hat.SetInputs(true, true, false, false)` **before** `Initialize`.

## Status model

`IsMoving` / `CommandedMotion` report the commanded motion. `Status` is the displayed state, derived in one place:

| Status | Meaning |
|--------|---------|
| Error | Relay register unverified, a latched safety fault, or both limits active |
| NotInitialized | `Initialize` has not succeeded |
| Opening / Closing | Motion commanded |
| Open / Closed | Idle at that limit |
| PartiallyOpen / PartiallyClose | Idle between the limits after an open/close motion |
| Stopped | Idle between the limits with no motion since start-up |

## Fault latching and reset policy

These stop reasons **latch** a safety fault:

- `SafetyWatchdogTimeout`
- `DriveFault` (IN3 active while moving, or IN3 active while idle, including at start-up)
- `RelayVerificationFailed` (a relay transition or supervision check did not read back the expected register value, or
  two consecutive periodic relay register reads failed)
- `InputReadFailure` (`MaxConsecutiveInputReadFailures` consecutive failed input reads while moving)
- `ContradictoryLimitInputs` (both limits active, while moving or idle)
- `StartLimitReasserted` (the departure limit reasserted after its release was verified)
- `DriveNotRunning` (no IN4 within `AtSpeedConfirmationTimeout` after a start, or IN4 low for 250 ms after it
  confirmed while the destination limit is not reached; only when the window is set)
- `DepartureLimitNotReleased` (the start limit did not release and stay released for the debounce within `DepartureReleaseTimeout`; only when it is set)

Rules for latched faults:

- **Precedence.** The first latched reason is kept. The exception is `RelayVerificationFailed`, which always takes
  precedence because relay state is then unknown.
- **What a latch blocks.** While a fault is latched, `Status` is `Error`, and `Open`/`Close` fail with `FaultLatched`.
  `Stop` is always allowed.
- **What does not reset it.** Stop, the passage of time, supervision cycles, an input returning to normal and a relay
  register that re-verifies all leave the latch in place.
- **The only reset.** A successful `ClearFault` resets the latch. After the RLY3 pulse, it clears only when all of
  these hold:
  1. The inputs read successfully (otherwise `HardwareUnavailable`).
  2. IN3 is inactive (otherwise `InterlockActive`).
  3. The limits are not contradictory (otherwise `InterlockActive`).
  4. The relay register is verified all-off (otherwise `RelayStateUnverified`).

  A failed, preempted or cancelled `ClearFault` leaves the latch unchanged.
- **Stop reason is separate.** `LastStopReason` records why motion last stopped. `ClearFault` does not change it, and a
  `Stop` while idle does not change it either.

A start that finds IN3 active or both limits active is refused with `InterlockActive`, and the same evaluation
latches `DriveFault` or `ContradictoryLimitInputs`. The next start is refused with `FaultLatched`. A start while IN4
still reports the drive running (with `AtSpeedConfirmationTimeout` set) is refused with `InterlockActive` without
latching; a reversal in that state stops the roof first.

These stop reasons are not latched: `NormalStop`, `LimitSwitchReached`, `OperatorLeaseExpired`, `EmergencyStop`,
`StopButtonPressed`, `SystemDisposal` and `HostShutdown`.

Relay read-back proves only what the HAT **register** holds. It never proves the relay contacts moved. The fake register
models exactly that register.

## Deterministic approach

- **Manual time and supervision.** Most tests use `ManualTimeProvider` with background supervision disabled. They
  advance time explicitly and call `RunSupervisionCycle()`, so watchdog, lease, at-speed, debounce and staleness
  deadlines are exact.
- **ClearFault pulses.** Pulses run on the manual clock. Tests call `time.Advance(pulse)` and then await the returned
  task with `WaitAsync`, because the continuation runs asynchronously.
- **Background loops.** A small number of tests exercise the real loops: supervision, the HAT input poll loop and the
  status dispatcher. They use real time with generous timeouts and wait only for an outcome, never for an interleaving.
  The input-polling test first waits until the HAT poll loop has read the mid-travel level; the poll loop only raises
  edges relative to its own last read.
- **Fault injection.** `FakeRoofRegisterClient` (through `FakeRoofHat.Registers`) injects faults: relay write, read and
  input read failures (persistent or next-N), stuck relay bits, ignored set commands, and a relay write observer. It
  also records `MaskHistory` and `EverBothDirectionBits`.
- **Serialized classes.** `[DoNotParallelize]` is used for classes that assert the process-wide telemetry gauges or
  share one HAT.

## Test files

| File | Covers |
|------|--------|
| `Services/RoofControllerRelayBehaviorTests` | Energize order (opposite off → STOP → direction), limit stops, reversal, repeat commands, telemetry |
| `Services/RoofControllerRelayVerificationTests` | Read-back mismatch, stuck and unreadable register, supervision register checks, retry, precedence |
| `Services/RoofControllerNegativeTests` | Contradictory limits, fault interlock, relay guard, commands before `Initialize` |
| `Services/RoofControllerWatchdogTests` | Absolute watchdog cap, stale callbacks, supervision backstop, latch reset |
| `Services/RoofControllerLeaseTests` | Optional operator lease, `RenewLease`, repeat-command renewal, `LeaseNotActive` |
| `Services/RoofControllerAtSpeedTests` | IN4 confirmation window (`DriveNotRunning`), drive still at-speed after stop |
| `Services/RoofControllerDriveRunTests` | IN4 start interlock, reversal refusal, 250 ms run loss, destination re-read, `DriveStopConfirmationTimeout` |
| `Services/RoofControllerDepartureTimeoutTests` | `DepartureReleaseTimeout` (`DepartureLimitNotReleased`), disarm on release, supervision wake |
| `Services/RoofControllerInputReadFailureTests` | Fresh read on start, failure threshold, staleness, `Initialize` read failure |
| `Services/RoofControllerLimitDepartureTests` | Departure-limit release debounce, chatter, `StartLimitReasserted` |
| `Services/RoofControllerLimitEdgeTests`, `LimitPolarityTests`, `PartialStatusTests`, `LedIndicatorTests` | Edge handling, NC/NO polarity, partial states, indicator LEDs |
| `Services/RoofControllerPeriodicVerificationTests` | Supervision detects lost edges, input-polling edge path, production configuration |
| `Services/RoofControllerClearFaultTests` | Pulse bounds, serialization, Stop preemption, release/assert failures, reset conditions |
| `Services/RoofControllerConfigurationTests` | Versioned and transactional updates, local-only consent, validation |
| `Services/RoofControllerStatusDispatchTests` | Ordered delivery, `StatusVersion`, drop-oldest queue, handler isolation |
| `Services/RoofControllerRelayReadFailureTests` | Failed and stale periodic relay register reads: unhealthy after one, stop and latch after two, idle all-off |
| `Services/RoofControllerDisposalTests`, `HatConcurrencyTests` | Shutdown and disposal, two controllers on one HAT |
| `Services/RoofControllerShutdownRetryTests` | Unverified shutdown stop: background all-off retry, give-up, disposal cancels it |
| `HostedServices/…`, `HealthChecks/…`, `Models/…` | Host shutdown path (bounded wait, abandoned blocking call), health rules, options validation |
| `Security/DeploymentValidatorTests` | `--validate-deployment`: roof options (and whether the HAT device is mapped), other options sections and log levels, keys and deploy key, the listeners and endpoints Kestrel would use, certificate loading and expiry, `AllowedHosts` |
| `Security/DeploymentValidationCommandTests` | The `--validate-deployment` command as `Program.Main` runs it, with real configuration sources and a temporary secrets directory |
| `Simulation/…` | The emulator models on their own (`PlantRig`, raw register writes): SMVector drive, ME-8108 switch, SM-I-010 board and bus, the assembled plant |
| `Plant/PlantProductionCycleTests` | Open and close on the limits, stop, reversals, watchdog and lease, with the production configuration |
| `Plant/PlantDriveTests` | Drive trips and power loss, clear-fault pulse, external stops and run loss, stop methods and stop distance, acceleration, DC brakes after a stop and before a start (`P175`, `P110` = 2), `P100` and `P112` at their factory defaults |
| `Plant/PlantLimitSwitchTests` | Contact action, bounce and transfer time, stuck switches, broken wires, a jammed roof |
| `Plant/PlantHatTests` | HAT power loss and LED modes, dead and welded relays, register faults, failed relay writes while energizing (abort with every relay off), input read failures below and at the limit |
| `Plant/PlantDocumentedFiguresTests` | The stop distances, start-limit release times and wrong-way timing the documents quote |
| `Plant/PlantWiringFaultTests` | Each wiring mistake from closed, mid-travel and open; swapped motor leads; wrong polarity and output settings |
| `Plant/ProductionConfigurationTests` | The deployed `appsettings.json` validates and matches the documented wiring |

## Emulated plant

`HVO.RoofControllerV4.Simulation` models the installation from the vendor documentation: the Lenze SMVector drive
(terminals, `P1xx` settings, ramps, stop methods, trips), the two ME-8108 limit switches (NC pair in the drive's run
circuit, NO pair to the HAT), the Sequent SM-I-010 HAT (relay and input registers, LED modes, power loss) and the
documented wiring, with injectable faults (`WiringFault`, `LimitSwitchFault`, `RelayContactFault`, bus failures). It
records `PlantViolation`s such as a hard-stop contact or both direction contacts closed.

`PlantHarness` runs the production controller, through the real `FourRelayFourInputHat` library and the emulated I2C
client, with the production `appsettings.json` on a `ManualTimeProvider`. It stands in for the two background loops
(input polling and supervision) so the whole run is deterministic. Each I2C transaction takes the HAT library's bus time
(`EmulatedBusTiming.LibraryDefault`: the transfer at 100 kHz, then the 15 ms post-transaction pause), which the calling
code spends without firing timers, so relay writes land in their real order and spacing. Timing assertions measure
from the plant's `History` (`PlantHarness.EventAt`, `CoilOnAt`, `CoilOffAt`) relative to the command.

The plant's defaults are **assumptions**, documented where they are defined (`RoofPlantOptions`,
`SmVectorAssumptions`, `Me8108Options`): 2 m of travel at 0.1 m/s, hard stops 60 mm past each limit's operate point,
the installed `P1xx` values, a 4 ms drive input response, the Run output during a DC brake, and switch bounce and
transfer times (the bounce needs a plant step of 1 ms or less). Tests that depend on an
assumption vary it. Software cannot prove the wiring, that contacts move, or the hardwired stop path; those remain
commissioning checks ([docs/commissioning.md](../../docs/commissioning.md)).

Known limitations the plant tests document:

- A ramp stop with the 2 s deceleration (`P105`) runs into the hard stop; coast (`P111 = 0`), a short ramp or DC braking
  stops clear of it.
- Swapped motor leads, from a limit, drive the roof into the stop behind that limit before the stall trip. Only
  `DepartureReleaseTimeout`, set from the release time with the installed `P104`, stops it first.
- An open limit that never operates reaches the hard stop; only a travel-time or position check would catch it.
- A welded direction relay is invisible to the register read-back; RLY4 still stops the roof.
- If the Run output stays on during a DC brake, the next move is refused until `P175` has passed; with `P175` = 999.9
  every move after a stop is refused.

## Adding new tests

1. Build the service with `SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), …)` and set the inputs
   before `Initialize`.
2. Assert the relay register (`hat.RelayMask`), the commanded motion and the snapshot. For safety logic, also assert
   `LatchedFaultReason`.
3. Never weaken a safety assertion to make a test pass.

## Running tests

```
cd src
dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests -c Release
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests -c Release --no-build
```

The deploy script has its own tests, `tests/deploy/deploy-script-tests.sh` (bash 3.2 or later, `python3`, `jq`,
`sha256sum` or `shasum`, and `setsid` or `perl` for the script itself). They run the script against fake `docker` and
`curl` commands (`tests/deploy/fakes`) that keep containers in a temporary state file: settings checked before any
Docker call, a Docker context that would prompt, pre-flight parity with the controller's options, HTTPS-only
publishing, the verified-stop gate, the remote check, restoring the old controller after a failure, a signal
(including a Ctrl-C that reaches docker, a second one during the restore, and a stop the daemon finishes after its
client was cut off) or a lost terminal (`tests/deploy/on-terminal` runs the script on a pseudo-terminal), the
restore's report when Docker cannot be read, `--rollback` and its undo, and that the key never appears in an argument
list. An unknown test name counts as a failure. Run `tests/deploy/deploy-script-tests.sh [test_name ...]` from the
repository root.
