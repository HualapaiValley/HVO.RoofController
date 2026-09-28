# Logging and telemetry reference

The roof controller writes structured logs to the console, and exports metrics and traces over OTLP when a collector is
configured. Telemetry never decides whether the roof can move: a collector that is down or slow does not delay a stop
(commissioning check C13).

## Logs

The production log level is `Information` (`Microsoft.AspNetCore` at `Warning`), so `Debug` entries are not written.
Containers use Docker's `local` log driver, rotated at 10 MB with five files kept (`docker-compose.yaml`, `x-logging`).
Keys, cookies and Blue Iris credentials are never logged. Each command logs the caller's key name and remote address,
and configuration changes log an `AUDIT` entry ([security.md](security.md#logging)).

| Level | What it means | Examples |
|---|---|---|
| Critical | The relay state is unverified or a hard safety condition needs an operator. Use the independent hardware stop. | `Stop sequence for {Reason} could not verify all relays off`, `Drive still reports running (IN4) {Seconds}s after stop`, `Shutdown stop retry gave up` |
| Error | A safety fault latched. Motion is refused until ClearFault. | `Safety fault latched: {Reason} - {Message}` |
| Warning | A safety stop, or one about to happen, and camera failures. | `Roof motion ({Direction}) stopped by safety supervision: {Reason}`, `Drive run input (IN4) dropped while {Direction}`, `Camera {CameraId}: Blue Iris answered {StatusCode}` |
| Information | Normal motion. | `Roof {Direction} started.`, `Roof motion ({Direction}) stopped: {Reason}` |

## Export

OTLP export is on when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The production compose file supplies a default off-Pi
collector, and the emulator profile uses `HVO_EMULATED_ROOF_OTLP_ENDPOINT`.

| Setting | Default | |
|---|---|---|
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | Use this protocol. |
| `OTEL_SERVICE_NAME` | `hvo-roof-controller` | |
| `OTEL_SERVICE_INSTANCE_ID` | `roof-controller-rpi` | |
| `OTEL_METRIC_EXPORT_INTERVAL` | `10000` (ms) | |

The resource carries `hvo.roof.hat.mode` (`hardware`, or `emulated` against the HAT emulator) and, in emulator mode,
`hvo.roof.hat.emulator.endpoint`, so an emulator run's telemetry is never mistaken for the roof's.

- **Traces:** ASP.NET Core and outbound HTTP spans, and a `roof.command` span for each command with `roof.command` and
  `roof.outcome` tags.
- **Metrics:** the standard runtime meters, and the roof meter `HVO.RoofController.RPi` below.

The shared collector prefixes metric names with `hvo_` and applies the Prometheus conventions:

- Dots become underscores.
- Counters end in `_total`.
- The `s` unit adds `_seconds`.
- A histogram is exported as `_bucket`, `_sum` and `_count`.

For example, `roof.controller.travel.duration` becomes `hvo_roof_controller_travel_duration_seconds_bucket`, and the
`roof.direction` tag becomes the `roof_direction` label.

## Roof metrics

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `roof.controller.commands` | counter | `roof.command`, `roof.outcome` (`success`, `failure`) | Commands. The controller service counts each `open`, `close`, `stop` and `clear-fault` from any source (API, console, host shutdown). The API also counts its own requests (`open`, `close`, `stop`, `lease`, `clear_fault`), so an API command is counted twice, once under each name. |
| `roof.controller.command.duration` | histogram, s | as above | How long the command call took. This is not the travel time. |
| `roof.controller.safety.stops` | counter | `roof.stop.reason`, `roof.stop.source` | Stops while moving for a safety reason, including arrival at a limit (`LimitSwitchReached`). Operator and host-shutdown stops are not counted. Sources: `open-limit`, `closed-limit`, `fault`, `watchdog`, `relay`, `inputs`, `lease`, `drive`, `limits`, `operator`, `unknown`. |
| `roof.controller.limit.switch.events` | counter | `roof.limit.switch` (`open`, `closed`), `roof.limit.state` (`reached`, `cleared`) | Limit switch transitions. |
| `roof.controller.fault.events` | counter | `roof.fault.state` (`active`, `cleared`) | Drive fault input (IN3) transitions. |
| `roof.controller.clear_fault` | counter | `roof.clear_fault.outcome` | ClearFault outcomes, for example `cleared` or `fault_active`. |
| `roof.controller.limit.switch.state` | gauge | `roof.limit.switch` | `1` while the named limit is reached. |
| `roof.controller.fault.active` | gauge | | `1` while the drive fault input is active. |
| `roof.controller.watchdog.active` | gauge | | `1` while the maximum-run watchdog is armed. |
| `roof.controller.watchdog.remaining` | gauge, s | | Time left on the watchdog. |
| `roof.controller.drive.at_speed` | gauge | | The drive run input (IN4) level. |
| `roof.controller.status` | gauge | `roof.status` | Always `1`; the label is the controller status. |
| `roof.controller.travel.duration` | histogram, s | `roof.direction`, `roof.stop.reason`, `roof.travel.from_limit` | [Motion timing](#motion-timing). |
| `roof.controller.drive.start_delay` | histogram, s | `roof.direction` | [Motion timing](#motion-timing). |
| `roof.controller.drive.stop_delay` | histogram, s | `roof.direction`, `roof.stop.reason` | [Motion timing](#motion-timing). |
| `roof.controller.departure.release` | histogram, s | `roof.direction` | [Motion timing](#motion-timing). |

`roof.direction` is `opening` or `closing`, and `roof.stop.reason` is the stop reason's name, as in `lastStopReason`.

## Motion timing

These histograms measure the installation. Their values set the timing options, instead of values estimated at a
bench.

All four are measured on the controller's monotonic clock, from the points its supervision windows start at:

- **A move starts** when the controller accepts Open or Close, before its input read and relay writes. The watchdog
  starts there too.
- **A stop** is when the controller decides to stop, before its relay writes. The drive stop window starts there too.
- **An input change** counts when the controller processes it. Inputs arrive from the HAT's input poll
  (`DigitalInputPollInterval`, 25 ms), or from the supervision cycle (`PeriodicVerificationInterval`, 1 s) with
  polling off.

Each value is the physical time plus the controller's own overhead: up to one input poll, and the HAT reads and writes
of the start or stop. Those take milliseconds on the I2C bus, and about 0.1 s against the HAT emulator, whose register
server runs over TCP. So each value is an upper bound of the physical time, which is what setting a timeout needs.

### `roof.controller.travel.duration`

The time from a move's start to its stop, recorded for every move whatever stopped it.

- `roof.travel.from_limit` is `true` when the move started at the opposite limit.
- A full travel is `roof.stop.reason="LimitSwitchReached"` with `roof.travel.from_limit="true"`.

Its maximum in each direction is the baseline for `SafetyWatchdogTimeout`: full travel plus a margin, within 5–600 s
(commissioning C8). It is also the baseline for the optional travel-time supervision (issue #32).

### `roof.controller.drive.start_delay`

The time from a move's start until the drive run input (IN4) first reports running. The run output follows the drive's
start, so this is the drive's response to the run command.

A move that started while IN4 still reported running records no start delay: that HIGH is the last move's run-down.
With the interlock configured such a start is refused, so this happens only with the interlock off.

`AtSpeedConfirmationTimeout` (production 3 s) must be well above its maximum (commissioning C6).

### `roof.controller.drive.stop_delay`

The time from a stop until IN4 reports that the drive has stopped. This covers the deceleration and any DC brake if
the drive's run output (TB-14) stays on through them. The SMVector manual does not say; the emulator assumes it does
(`RunOutputDuringDeceleration`, `RunOutputDuringDcBrake`).

- It is recorded when the controller sees IN4 drop after a stop, or as 0 when it had already seen IN4 drop.
- At a limit stop, IN4 drops a few milliseconds before the limit reports (the ME-8108 transfer), usually within the same
  input poll. The controller handles the limit first, so the value is then its own stop sequence.
- A move with no start delay (IN4 never confirmed it, or it started while IN4 still reported running) records no zero.
- A new move before IN4 drops discards the pending measurement.

`DriveStopConfirmationTimeout` (or `AtSpeedConfirmationTimeout` when that is unset) must be above its maximum. Otherwise
every such stop logs `Drive still reports running (IN4)` at Critical (commissioning C6).

This is not the roof's coast. No input reports the roof's movement after the drive stops.

### `roof.controller.departure.release`

The time from a move's start at a limit until that limit's release was first observed. It is recorded when the
release has held for `LimitSwitchDebounce` and is verified. Any earlier release that bounced back (chatter) does not
count.

`DepartureReleaseTimeout`, off in production, must be above its maximum (commissioning C10).

### Buckets

| Histograms | Bucket boundaries (s) |
|---|---|
| `travel.duration` | 1, 2, 5, 10, 15, 20, 30, 45, 60, 75, 90, 120, 150, 180, 240, 300, 450, 600 |
| `drive.start_delay`, `drive.stop_delay`, `departure.release` | 0.025, 0.05, 0.1, 0.15, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 5, 7.5, 10, 15, 30, 60 |

The explicit boundaries replace the SDK's defaults (0, 5, 10, 25, ...), which would put every drive delay in the first
bucket. The exact maximum is not exported; use the bucket that holds it, which is an upper bound.

For example, a PromQL query for the slowest full travel in each direction over 30 days:

```promql
histogram_quantile(1, sum by (le, roof_direction) (increase(
  hvo_roof_controller_travel_duration_seconds_bucket{roof_stop_reason="LimitSwitchReached", roof_travel_from_limit="true"}[30d])))
```
