# Roof Controller Commissioning Checklist

> **Status: OPEN.** None of the checks below has been performed or recorded for the controller
> changes from issues #16-#22. Until every check has a recorded pass, treat the controller as
> unvalidated on physical hardware. The RV-5 telemetry-outage check (C13) and the RV-6 soak
> (C14) from the [architecture review](architecture-review-2026-08.md#required-validation-for-follow-up-prs)
> are also open.

This checklist covers what unit tests cannot prove: the real HAT, the VFD, the wiring and the
deployed container. The unit tests run against a simulated HAT (`TestSupport/FakeRoofHat`), so a
green CI run says nothing about relay contacts, input polarity or drive timing.

## Ground rules

- Work on the bench: Raspberry Pi, HAT stack and VFD with the motor **disconnected from the roof
  mechanism** (or the mechanism mechanically isolated), unless a step says otherwise. Steps that
  need the mechanism are marked **[mechanism]**; do them only after every bench step has passed,
  with nobody in the roof's path and someone at the independent stop.
- Do not automate physical roof cycling (scripts that repeatedly open and close the real roof)
  without an approved test rig and interlocks. Automated cycling is limited to simulation or a
  bench rig with the motor decoupled (see C14).
- The software controls described in the [README](../README.md#safety-behavior) do not replace an
  independent hardware stop path. Do C1 first.
- Record each result in the [results table](#results) with the controller commit SHA, date and
  initials. Do not put API keys, passwords, host names or IP addresses in this public repository;
  keep those in the private operations record.

## Prerequisites

- Wiring per the [hardware overview](projects/roof-controller-v4-rpi/hardware-overview.md),
  sections 8 and 9, and its bring-up sequence (section 14) completed with a meter.
- Controller deployed with the deployment script ([deployment.md](deployment.md)); an operator
  key and an admin key provisioned ([security.md](security.md)).
- Local configuration (`RoofControllerOptionsV4`) set for the installed wiring:
  `UseNormallyClosedLimitSwitches` (C3), `FaultInputActiveHigh` (C4),
  `IgnorePhysicalLimitSwitches = false`, `AllowIgnoringLimitSwitchesOnPhysicalHardware = false`.
- A multimeter, jumper leads or a switch box to simulate limit contacts, and a way to read status:

  ```bash
  # ROOF_URL is the controller's base URL; keys come from your environment, never from this file.
  curl -fsS -H "X-Api-Key: $ROOF_OPERATOR_API_KEY" "$ROOF_URL/api/v4.0/RoofControl/Status" | jq
  curl -fsS -X POST -H "X-Api-Key: $ROOF_OPERATOR_API_KEY" "$ROOF_URL/api/v4.0/RoofControl/Stop" | jq
  ```

  The status fields used below are `commandedMotion`, `relayRegisterState`,
  `relayRegisterMask` (bit n-1 = relay n), `isFaultLatched`, `latchedFaultReason`,
  `lastStopReason`, `isOpenLimitActive`, `isClosedLimitActive`, `isDriveFaultActive`,
  `isAtSpeed`, `inputsHealthy`, `consecutiveInputReadFailures`, `leaseSecondsRemaining` and
  `isClearFaultInProgress`.

## Checks

### C1. Independent hardware stop path

The controller can only stop the drive through the HAT. If the I2C bus, the HAT or the Pi fails
while a direction relay is energized, software cannot de-energize it.

1. Identify the independent stop: an E-stop that opens the VFD STOP loop (`TB-1`) or removes
   drive power, independent of the Pi.
2. With a direction commanded, operate it. The drive must stop within its ramp time and must not
   restart until the stop is reset, whatever the controller does.
3. Remove Pi/HAT power with a direction commanded. RLY1/RLY2/RLY4 must drop out and the drive
   must stop (hardware overview, section 14 step 13).
4. Confirm the hardwired limit contacts (section 8, terminals 1-2) stop the drive with the
   controller's limit inputs disconnected.

**Pass:** each of steps 2-4 stops the drive without software involvement. If the site has no
independent E-stop, record that as an open risk; it is not a software fix.

### C2. Relay register versus contacts

`relayRegisterState = Verified` means the HAT register read back as commanded. It does not
prove the relay contacts moved.

1. For Open, Close and Stop, and for a ClearFault pulse, compare `relayRegisterMask` with a
   meter reading across each relay's COM-NO contacts, and with the relay state table in the
   hardware overview, section 10.
2. After Stop: `relayRegisterState = Verified`, `relayRegisterMask = 0`,
   `commandedMotion = None`, and all four COM-NO contacts open on the meter.
3. Repeat after replacing the HAT or any relay.

**Pass:** register and meter agree in every state. A contact that disagrees with a Verified
register is a hardware fault the controller cannot detect; replace the part.

### C3. Limit input polarity (IN1/IN2)

1. Set `UseNormallyClosedLimitSwitches` for the installed monitoring contacts (the hardware
   overview's section 9 wiring reads HIGH when actuated, which needs `false`).
2. Actuate each limit by hand. `isOpenLimitActive` (IN1) and `isClosedLimitActive` (IN2) must be
   `true` only while the matching switch is actuated.
3. Actuate both. The controller must stop any motion with `ContradictoryLimitInputs`, latch the
   fault and refuse Open and Close.
4. Disconnect each monitoring wire and record what the controller reports. With normally-open
   monitoring contacts a broken wire reads "not at limit"; the hardwired contacts from C1 step 4
   remain the backstop. Record this limitation.

**Pass:** steps 2 and 3 behave as described for both switches; step 4 is recorded.

### C4. VFD fault input polarity (IN3), with a real VFD fault

`FaultInputActiveHigh` defaults to `true` (raw HIGH = fault). The fail-safe wiring in the
hardware overview, section 9.3 (`P140 = 3`), reads HIGH when healthy and needs `false`. Do not
change the setting without doing this check, and keep wiring, setting and this record in step.

1. Confirm how IN3 is actually wired on this installation and set `FaultInputActiveHigh`.
2. Healthy, powered drive: `isDriveFaultActive = false`, Open is accepted (motor decoupled).
3. Induce a real drive trip by a method the SMVector manual documents, with the motor decoupled.
   `isDriveFaultActive = true`; any motion stops with `DriveFault`; `isFaultLatched = true`;
   Open and Close are refused with `FaultLatched` (HTTP 409).
4. Disconnect the IN3 monitoring wire, then separately remove VFD power. With the fail-safe
   wiring both must read as a fault. With HIGH = fault wiring they read as healthy; record that
   as a known limitation.
5. Reset the drive and clear the latch (C5).

**Pass:** steps 2-4 match the expected result for the chosen polarity, and the value of
`FaultInputActiveHigh` is recorded with the wiring it was verified against.

### C5. ClearFault pulse length

The API accepts `POST .../ClearFault?pulseMs=N` with N from 50 to 2000 (default 250). The
hardware overview, section 8.3, recommends about 100-300 ms.

1. With a latched drive trip (C4 step 3), send ClearFault with the default pulse. Confirm with the
   meter or a scope that RLY3 closes for about the requested time and then opens.
2. Find the shortest pulse that reliably resets the drive over 10 attempts and choose a value
   with margin. Record it and make sure the operator clients use it.
3. With IN3 still reporting a fault after the pulse, the latch must remain.
4. With healthy inputs, `isFaultLatched` must become `false`; `lastStopReason` keeps the original
   cause.
5. ClearFault while moving must be refused. Stop during a long pulse (for example 2000 ms) must
   end the pulse: RLY3 opens on the meter.
6. Stop alone must not clear the latch.

**Pass:** all six steps behave as described.

### C6. Drive-running input (IN4) and `AtSpeedConfirmationTimeout`

1. With the motor decoupled, command Open and Close 10 times each (cold and warm drive) and
   measure the time from the command to `isAtSpeed = true`. The timing depends on `P142`
   (`1` = Run, `6` = At Speed).
2. Set `AtSpeedConfirmationTimeout` to the longest observed time plus a margin (for example twice
   the maximum), within 0.5-30 s and shorter than `SafetyWatchdogTimeout`.
3. Disconnect the IN4 wire and command a move. The controller must stop with `DriveNotRunning`
   within the timeout.
4. After Stop, measure how long IN4 takes to return LOW. The controller does not check this; if
   IN4 stays HIGH after the relays are verified off, the drive is still running: use the
   independent stop and investigate.

**Pass:** step 3 stops within the configured timeout; the timings from steps 1 and 4 are recorded.
If the interlock is left disabled, record why.

### C7. `MaxConsecutiveInputReadFailures` with I2C fault injection

The controller stops with `InputReadFailure` after this many consecutive failed input reads while
moving (default 3, range 1-10). Detection time is roughly this number times
`DigitalInputPollInterval`, plus one `PeriodicVerificationInterval` if polling is slow.

1. Bench only, motor decoupled. Interrupt the HAT's I2C lines through a breakout or switch made for
   the purpose while a move is commanded. Never hot-unplug the HAT stack.
2. The controller must report `inputsHealthy = false`, stop with `InputReadFailure`, latch the
   fault and report `/health/ready` as unhealthy.
3. While the bus is down the relays cannot be commanded either, so expect
   `relayRegisterState = Unverified` and a failed stop, not a success. Confirm the drive is
   stopped by the hardwired limits or the independent stop (C1), not by software.
4. Restore the bus. Open must stay refused until ClearFault succeeds with healthy inputs.
5. Repeat with a single short interruption (fewer failures than the threshold). Motion should
   continue; record whether any stop occurred.

**Pass:** steps 2-4 behave as described; the chosen threshold and measured detection time are
recorded.

### C8. Maximum-run watchdog cap

1. Measure the real full travel time in both directions **[mechanism]**, or use the drive's
   configured ramp and speed to estimate it on the bench.
2. On the bench, set `SafetyWatchdogTimeout` to a short test value (for example 10 s) through
   `POST .../Configuration` with `ExpectedVersion` and `ConfirmSafetyCriticalChange = true`.
3. Command Open, then send Open again every 3 s. The roof must stop 10 s after the **first** Open
   (repeated commands do not extend it) with `SafetyWatchdogTimeout`, and the fault must latch.
4. Restore the production value: full travel time plus a margin, within 5-600 s.

**Pass:** step 3 stops on time and latches; the production value and the travel times are
recorded.

### C9. Operator lease expiry

Only if `OperatorLeaseTimeout` is used (2-120 s).

1. Set a short lease (for example 5 s). Command Open and renew with `POST .../Lease` every 2 s:
   motion continues and `leaseSecondsRemaining` refreshes.
2. Stop renewing. The roof must stop with `OperatorLeaseExpired` within the lease time plus one
   `PeriodicVerificationInterval`.
3. `POST .../Lease` while idle returns 409 `LeaseNotActive` and starts nothing.
4. Client loss: start a move from a mobile browser and turn its Wi-Fi off; separately,
   close the browser tab during a move from the console. Each must stop within the lease time
   when a lease-renewing client is in use. If the web console does not renew leases, record
   this as a failed prerequisite rather than claiming client-loss protection.

**Pass:** steps 1-4 behave as described. If the lease is not used, record that losing the client
does not stop motion and that only the watchdog and limits bound a move.

### C10. Starting at a limit: departure from both limits **[mechanism]**

Do this after C1-C9 pass, or first with the limit contacts simulated by a switch box.

1. Roof at the closed limit: command Open. The closed limit releases during departure without a
   false stop, and the roof stops at the open limit with `LimitSwitchReached`.
2. Roof at the open limit: command Close, same checks in reverse.
3. Simulated only: after the start limit has released, re-actuate it by hand. The controller must
   stop with `StartLimitReasserted` and latch.
4. Simulated only: hold the start limit actuated after the command. Record how the move ends
   (the watchdog is the backstop).

**Pass:** steps 1-3 behave as described in both directions; step 4 is recorded.

### C11. Container stop with an active camera stream

1. Open a camera stream in the console (or through a stream ticket) and command a move with the
   motor decoupled.
2. Stop the container as the deployment script does (`docker stop -t 30 <container>`).
3. Check the logs for a stop with reason `HostShutdown` and verified relays, and check the exit
   status: `docker inspect --format '{{.State.ExitCode}}' <container>` must not be 137 (killed
   after the grace period).

**Pass:** the roof stops (relays verified off, meter confirms) within about 5 s of the stop
request, the stream ends, and the container exits within the grace period.

### C12. Deployment script stop gate and a failed new container

1. Deploy with the roof idle: the script's Stop returns a verified all-off and the deployment
   proceeds.
2. Deploy while a move is commanded (bench): the script stops the roof first and proceeds only
   after the verified result.
3. Deploy with a wrong operator key: the script aborts before stopping or replacing the
   container. Do not use the forced override in this test.
4. Deploy an image that cannot become ready (for example one started with an invalid
   configuration value). The script must report the failure loudly. Then restore service by
   redeploying the previous image as described in [deployment.md](deployment.md), and confirm
   the roof stayed de-energized throughout (`relayRegisterMask = 0`, meter).

**Pass:** steps 1-4 behave as described; the recovery time for step 4 is recorded.

### C13. RV-5: telemetry collector outage does not delay Stop

OTLP export runs whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set. A collector outage must not add
latency to commands or to the stop path.

1. Baseline with the collector reachable: 50 Stop requests (idle and during a bench move),
   recording the HTTP response time and the time until `relayRegisterMask = 0`.
2. Point `OTEL_EXPORTER_OTLP_ENDPOINT` at an address that times out (an unused address on an
   unrouted test network) and repeat. Then repeat with a closed port on the Pi (immediate
   refusal).
3. During each outage, run a limit stop (switch box) and a watchdog stop and compare their timing
   with the baseline.
4. Keep the outage for at least 1 h and watch memory and log volume.

**Pass:** Stop and limit-stop latency with the collector down stay within the baseline's range
(the 99th percentile adds no more than 100 ms, and every Stop completes in under 1 s); no command
errors; memory stays flat. Also still open: an automated test that runs the stop path with an
unreachable collector configured.

### C14. RV-6: soak test (24-72 h)

Run on the Pi with the production image and configuration. Two variants, in this order:

- **Simulated:** the container without the HAT devices mapped (simulated HAT), with a script that
  cycles Open, Stop, Close, Stop through the API, plus status polling at client rates and a camera
  stream opened and closed periodically.
- **Bench:** the real HAT and VFD with the **motor decoupled**. Limit inputs are driven by a relay
  board or switch box that the cycling script controls. Never the roof mechanism.

Sample every minute and keep the samples:

- Container memory and CPU (`docker stats --no-stream`), thread count and open file descriptors
  of the app process (`/proc/<pid>/status`, `/proc/<pid>/fd`).
- Container log size (the file at `docker inspect --format '{{.LogPath}}'`) and free space on the
  SD card or SSD.
- `/health/ready`, `relayRegisterState`, `inputsHealthy`, age of `lastSuccessfulInputReadUtc`,
  `consecutiveInputReadFailures`, `statusVersion` (must only increase).
- The safety-stop and limit-event counters from the metrics.

**Pass (all required):**

- Duration at least 24 h for each variant; 72 h on the bench before claiming soak validation.
- After the first hour, memory grows less than 10% over the rest of the run with no steady upward
  trend; thread and file-descriptor counts stay flat.
- Log and disk growth stay within the configured log rotation; projected 30-day growth fits in
  the free space with margin.
- Every commanded cycle ends with the expected stop reason, and limit events match the commanded
  cycles one for one (no missed transitions). No unexpected `InputReadFailure` or
  `RelayVerificationFailed` stop, `RelayStateUnverified` error, fault latch or Critical log
  entry.
- Hourly Stop latency stays within the C13 baseline.

## Results

Update this table as checks are completed. Keep private details (keys, hosts, addresses) out.

| Check | Status | Controller commit | Date | By | Result and recorded values |
|---|---|---|---|---|---|
| C1 Independent hardware stop | Open | | | | |
| C2 Relay register vs contacts | Open | | | | |
| C3 IN1/IN2 polarity | Open | | | | |
| C4 IN3 polarity (real VFD fault) | Open | | | | |
| C5 ClearFault pulse length | Open | | | | |
| C6 IN4 timing / `AtSpeedConfirmationTimeout` | Open | | | | |
| C7 `MaxConsecutiveInputReadFailures` | Open | | | | |
| C8 Watchdog cap | Open | | | | |
| C9 Operator lease expiry | Open | | | | |
| C10 Start-at-limit departure | Open | | | | |
| C11 Container stop with camera stream | Open | | | | |
| C12 Deployment stop gate / failed container | Open | | | | |
| C13 RV-5 telemetry outage | Open | | | | |
| C14 RV-6 soak | Open | | | | |
