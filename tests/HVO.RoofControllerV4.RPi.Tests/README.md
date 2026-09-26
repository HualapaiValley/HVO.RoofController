# Roof Controller V4 Test Suite

This test project validates safety, motion control, and API-facing status semantics for `RoofControllerServiceV4`.

## Documentation

- [`docs/projects/roof-controller-v4-rpi/hardware-overview.md`](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md) – wiring map and relay/limit switch context for the test assumptions.
- [`docs/projects/roof-controller-v4-rpi/api-reference.md`](../../docs/projects/roof-controller-v4-rpi/api-reference.md) – REST contract enforced by controller API tests.
- [`docs/projects/roof-controller-v4-rpi/logging-reference.md`](../../docs/projects/roof-controller-v4-rpi/logging-reference.md) – structured logging catalog referenced in verification assertions.

## Wiring & Polarity Assumptions
- Limit switches are **Normally Closed (NC)**. Raw HIGH = circuit closed (not at limit); raw LOW = limit engaged.
- Digital Inputs Mapping (raw electrical):
  - IN1: Forward/Open limit
  - IN2: Reverse/Closed limit
  - IN3: Fault notification (active HIGH)
  - IN4: IsAtSpeed (roof movement velocity threshold reached)
- Relays:
  - RLY1: OPEN direction
  - RLY2: CLOSE direction
  - RLY3: CLEAR FAULT pulse output
  - RLY4: STOP / fail-safe enable (de-energized asserts STOP)

## Status Model
| Status | Meaning |
|--------|---------|
| Open / Closed | At corresponding limit only |
| Opening / Closing | Motion in progress (watchdog active) |
| PartiallyOpen / PartiallyClose | Movement stopped between limits after Open/Close sequence |
| Stopped | Idle mid‑travel without recent directional command context |
| Error | Fault input HIGH, both limits active, or watchdog timeout |

## Test Categories
### 1. Limit & Indicator Tests
Validate LED mask logic and NC polarity handling.

### 2. Edge / Transition Tests
Limit edge transitions, proper status changes, partial states.

### 3. Watchdog & Periodic Verification
Ensures timers start/stop correctly and emergency stop transitions to `Error`.

### 4. Relay Behavior (positive path)
`RoofControllerRelayBehaviorTests` covers:
- Safe power‑up (all relays de‑energized)
- Open / Close sequencing (STOP + direction energized, then drop) 
- Manual Stop mid‑travel transitions to partial states
- Fault trip behavior and refusal of new commands until cleared
- IsAtSpeed propagation during motion

### 5. Negative / Defensive Scenarios
`RoofControllerNegativeTests` adds:
- Both limits active refusal (Open & Close)
- Command refusal while fault active
- Relay guard preventing simultaneous Open+Close
- Idempotent Stop
- Persistence of Error state across subsequent commands

## Simulation Harness
Each behavior/negative test defines a lightweight `FakeHat` implementing I2C register semantics similar to the physical Sequent Microsystems HAT:
- Register 0x00: Relay mask (bits 0..3)
- Register 0x01: Relay SET (1..4)
- Register 0x02: Relay CLEAR (1..4)
- Register 0x03: Digital inputs mask (bits 0..3 for IN1..IN4)

Tests manipulate raw inputs directly and invoke protected event handlers via a `TestableRoofControllerService` subclass for deterministic, race‑free transitions.

## Safety Invariants Enforced by Tests
- Never energize both direction relays simultaneously (guard path tested).
- Movement commands are refused when a fault is active or both limits are triggered.
- STOP relay must be energized only during permitted motion (Open/Close sequences) and de‑energized on any stop or fault.
- Fault or watchdog induced stops set `Error` and require explicit fault clear sequence before resuming motion.

## Adding New Tests
1. Prefer extending existing category test classes when adding similar scenarios.
2. Use the existing `FakeHat` pattern to simulate raw electrical states; avoid real hardware dependencies.
3. When validating new safety logic, assert both relay mask and resulting `Status`.
4. For timing dependent logic (watchdog, pulses) use shortened timeouts in test-specific options to keep suite fast.

## Running Tests
The solution uses MSTest. Typical invocation from repository root:
```
dotnet test --filter FullyQualifiedName~RoofControllerV4
```
(Launch configurations / tasks in VS Code build projects first.)

## Future Enhancements
- Add explicit watchdog timeout negative test in negative suite with shortened timeout for determinism.
- Introduce fuzz tests for random input sequences ensuring no invalid relay combinations.
- Add API contract tests asserting JSON field names (e.g., `isAtSpeed`).

---
Maintained as part of safety‑critical validation for Roof Controller V4.
