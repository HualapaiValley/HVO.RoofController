# Roof Controller V4 Test Suite

This test project validates safety, motion control and API-facing status semantics for `RoofControllerServiceV4`,
its hosted service and its health check.

## Documentation

- [`docs/projects/roof-controller-v4-rpi/hardware-overview.md`](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring map and relay/limit switch context for the test assumptions.
- [`docs/projects/roof-controller-v4-rpi/api-reference.md`](../../docs/projects/roof-controller-v4-rpi/api-reference.md) – REST contract enforced by controller API tests.
- [`docs/projects/roof-controller-v4-rpi/logging-reference.md`](../../docs/projects/roof-controller-v4-rpi/logging-reference.md) – structured logging catalog referenced in verification assertions.

## Wiring and polarity assumptions

- Limit switches are **normally closed (NC)** by default: raw HIGH = circuit closed (not at the limit), raw LOW = limit
  engaged. `UseNormallyClosedLimitSwitches = false` inverts this.
- Inputs (raw electrical):
  - IN1: open limit
  - IN2: closed limit
  - IN3: drive fault. Active HIGH by default (`FaultInputActiveHigh = true`); the polarity must be confirmed on the bench.
  - IN4: drive at-speed. Enforced only when `AtSpeedConfirmationTimeout` is set.
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
- `DriveNotRunning` (no IN4 at-speed within `AtSpeedConfirmationTimeout`; only when it is set)

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
latches `DriveFault` or `ContradictoryLimitInputs`. The next start is refused with `FaultLatched`.

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
| `Security/DeploymentValidatorTests` | `--validate-deployment`: roof options, keys and deploy key, HTTPS listener, certificate loading and expiry |

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

The deploy script has its own tests, `tests/deploy/deploy-script-tests.sh` (bash, `python3` and `jq`). They run the
script against fake `docker` and `curl` commands (`tests/deploy/fakes`) that keep containers in a temporary state
file: pre-flight parity with the controller's options, HTTPS-only publishing, the verified-stop gate, the remote
check, rollback, `--rollback` and that the key never appears in an argument list. Run
`tests/deploy/deploy-script-tests.sh [test_name ...]` from the repository root.
