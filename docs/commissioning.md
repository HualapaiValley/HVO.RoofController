# Roof Controller Commissioning

Every commissioning check runs as an automated scenario against the emulated HAT, drive, limit switches, camera and roof
([HAT emulator](emulator.md)). The scenarios run in the "Scenarios" CI workflow on pull requests to `main` and
`feature/**`, and the long soak runs nightly. No check needs the Pi, the HAT, the VFD, test instruments or the roof
mechanism.

A scenario proves the controller's logic against the documented installation. It cannot prove that the installation
matches that documentation. Each check therefore lists **installation assumptions**. An assumption is a fact about the
wiring, the drive parameters or the site. Each one names the setting that depends on it and the fail-safe result the
emulator shows when the assumption is wrong.

The assumptions come from the [hardware overview](projects/roof-controller-v4-rpi/hardware-overview.md) and from the
vendor documentation the emulated plant is modelled on. Where that documentation is silent, the plant's figure is an
assumption of its own; `SmVectorAssumptions` and `PlantDocumentedFiguresTests` hold these figures.

## How the checks run

| Runs | Covers | Command (the `dotnet` commands run from `src/`) |
|---|---|---|
| `Scenario` tests | C1-C11 and C13, plus a 90 s C14 that does not assess resources. The production host and settings run in process against the emulated plant. | `dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests --filter TestCategory=Scenario` |
| `Browser` tests | C9 step 4 and C15, plus the console on phones and tablets in Chromium | `dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests --filter TestCategory=Browser`, after installing Chromium once with `pwsh ../tests/HVO.RoofControllerV4.RPi.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium` (without `--with-deps` when its system libraries are installed) |
| [Container scenarios](emulator.md#container-scenarios) | C11 in a real container, C12, and the move between Compose and the deploy script, on real Docker | `tests/emulator/deploy-scenarios.sh` (from the repository root) |
| Nightly soak | C14 for two hours. Its invariant results go to the run summary and an artifact. | `HVO_SOAK_DURATION=02:00:00 HVO_SOAK_RESULTS_DIR="$PWD/soak" dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests --filter TestCategory=Soak` |

The unit test job leaves these categories out. `ScenarioCoverageTests` keeps this document and the scenarios in step:

- Each step row below names the scenarios that cover it.
- Each scenario names its check and step. An in-process scenario does this with `[CommissioningCheck("C3", "2")]`. A
  container scenario does it with a `# CommissioningCheck("C12", "3")` line in `deploy-scenarios.sh`.
- A row with the step `—` lists scenarios that go beyond the numbered steps.
- A step marked "Installation assumption" has no scenario of its own.

The test fails when any of the following is true:

- a check has no scenario;
- a named scenario does not exist or does not name the step;
- a scenario's step is missing here;
- a scenario is in a category CI does not run.

## Ground rules

- The software controls in the [README](../README.md#safety-behavior) do not replace an independent hardware stop path
  (C1).
- Never automate cycling of the real roof. Only the emulated roof is cycled.
- A change to the wiring, a drive parameter or a setting named below changes an assumption. Update the emulated plant or
  the scenario's settings in the same change, then this document.
- Keep API keys, passwords, host names and addresses out of this public repository. They belong in the private
  operations record.

## The installed configuration

The production `appsettings.json` matches the documented wiring:

- `UseNormallyClosedLimitSwitches = false` and `LimitSwitchDebounce = 25 ms` (C3)
- `FaultInputActiveHigh = false` (C4)
- `AtSpeedConfirmationTimeout = 3 s` (C6)
- `MaxConsecutiveInputReadFailures = 3` (C7)
- `SafetyWatchdogTimeout = 2 min 30 s` (C8)
- `DepartureReleaseTimeout` unset (C10)
- `IgnorePhysicalLimitSwitches = false` and `AllowIgnoringLimitSwitchesOnPhysicalHardware = false`

`ProductionConfigurationTests` fails if these drift. The scenarios start from these settings (`Scenario.Production`).
Where a scenario changes a setting, it does so through the configuration API or the host settings, and the step
says so.

## Checks

### C1. Independent hardware stop path

The controller can only stop the drive through the HAT. If the I2C bus, the HAT or the Pi fails while a direction relay
is energized, software cannot de-energize it: the stop has to come from the wiring.

| Step | Checked | Scenario |
|---|---|---|
| 1 | The site has a stop that works without the Pi | Installation assumption |
| 2 | An external stop opens the drive's STOP loop during travel. The drive stops and the controller latches `DriveNotRunning`. While the stop is open, an Open after ClearFault does not start the drive. Resetting the stop does not restart it. | `C1IndependentStopPathScenarios.TheExternalStop_StopsTheDrive_AndNothingTheControllerCommandsRestartsIt` |
| 3 | The HAT loses power while the roof opens. Every relay drops, the drive stops short of the limit and the controller latches `RelayVerificationFailed`. When power returns, the controller verifies the relays off and waits for ClearFault. | `C1IndependentStopPathScenarios.HatPowerLoss_WhileOpening_DropsEveryRelay_AndStopsTheDrive` |
| 4 | Both limit monitoring wires are broken. Each limit's contact in its run circuit stops the drive short of the hard stop, and the controller latches `DriveNotRunning`. | `C1IndependentStopPathScenarios.TheHardwiredLimitContacts_StopTheDrive_WithTheControllersLimitInputsDisconnected` |

**Installation assumptions**

- **An E-stop opens the VFD STOP loop (`TB-1`) or removes drive power, without the Pi** (hardware overview, section 8).
  No setting depends on it. Without it, only the controller and the limit contacts stop the roof. That is an open risk
  for the operations record, not a software fix.
- **Each relay is de-energized without HAT power, and RLY4 is the STOP permit in series with `TB-1`** (sections 8
  and 10). No setting depends on it. The emulated HAT drops every contact without power (step 3).
- **Each limit switch's normally closed contact is wired into its direction's run circuit** (section 8, terminals 1-2).
  No setting depends on it. Without these contacts, a broken monitoring wire (C3 step 4) lets the roof reach the hard
  stop. The drive's stall trip then stops it and the controller latches `DriveFault`, long before the watchdog (C8,
  2 min 30 s) would (`RoofPlantTests.WithoutHardwiredEndStops_TheRoofHitsTheHardStop_AndTheDriveTripsOnTheStall`,
  `PlantLimitSwitchTests.OpenLimitStuckReleased_ReachesTheHardStop_AndOnlyTheStallTripStopsIt`).

### C2. Relay register versus contacts

`relayRegisterState = Verified` means the HAT register read back as commanded. It does not prove that the contacts
moved. The emulated HAT reports both.

| Step | Checked | Scenario |
|---|---|---|
| 1 | For Open, Close, Stop and a ClearFault pulse, the register matches the contacts and the relay table (hardware overview, section 10) | `C2RelayRegisterScenarios.TheRelayRegister_MatchesTheContacts_InEveryCommandedState` |
| 2 | After Stop: `Verified`, mask 0, `commandedMotion = None`, and every contact open | `C2RelayRegisterScenarios.TheRelayRegister_MatchesTheContacts_InEveryCommandedState` |
| 3 | A replaced HAT or relay behaves as the documented part | Installation assumption |
| — | A welded forward contact is invisible to the register, and Stop still stops the drive through RLY4. On the next Close, the drive refuses both directions at once and the controller latches `DriveNotRunning`. A dead forward or STOP-permit contact: the register verifies, the drive never starts, and the 3 s at-speed window latches `DriveNotRunning`. | `C2RelayRegisterScenarios.AWeldedDirectionContact_IsInvisibleToTheRegister_AndTheStopPermitStillStopsTheDrive`, `C2RelayRegisterScenarios.ADeadContact_TheDriveNeverStarts_AndTheAtSpeedCheckStopsTheMove` |

**Installation assumptions**

- **The relay contacts follow their coils, and a replacement HAT is the same four-relay, four-input model.** The setting
  that depends on it is `AtSpeedConfirmationTimeout` (3 s). If a contact dies, the at-speed window stops the move and
  latches it. If a contact welds, Stop still stops the drive through the permit, and the next move latches. The
  controller cannot see a welded contact while the roof is at rest, and replacing the part is the only fix.

### C3. Limit input polarity (IN1/IN2)

| Step | Checked | Scenario |
|---|---|---|
| 1 | The production setting on the documented wiring, and each of the two mistakes the plant can show: the normally closed setting, and the input commons on `TB-4` | `C3LimitInputPolarityScenarios.EachLimitInput_IsActiveOnlyWhileItsSwitchIsActuated`, `C3LimitInputPolarityScenarios.TheNormallyClosedSetting_OnTheNormallyOpenWiring_ReadsTheLimitsInverted_AndTheRoofNeverMoves`, `C3LimitInputPolarityScenarios.InputCommonsOnTb4_ReadTheDriveAsFaulted_AndNothingMoves` |
| 2 | `isOpenLimitActive` (IN1) and `isClosedLimitActive` (IN2) are `true` only while the matching switch is actuated | `C3LimitInputPolarityScenarios.EachLimitInput_IsActiveOnlyWhileItsSwitchIsActuated` |
| 3 | With both limits actuated during a move, the controller stops, latches `ContradictoryLimitInputs`, and refuses Open, Close and ClearFault. `lastStopReason` may be `LimitSwitchReached`, because the open limit's edge is handled first. | `C3LimitInputPolarityScenarios.BothLimitsActuated_WhileMoving_StopsWithContradictoryLimitInputs_LatchesIt_AndRefusesMotion` |
| 4 | A broken monitoring wire reads "not at the limit". The limit's run-circuit contact (C1 step 4) stops the drive. | `C3LimitInputPolarityScenarios.ABrokenMonitorWire_ReadsNotAtTheLimit` |

**Installation assumptions**

- **IN1 and IN2 are wired to the ME-8108 normally open pairs, which read HIGH when actuated.** The IN1-IN3 commons
  return to `TB-2` (0 V), and `TB-4` is the +15 V reference (hardware overview, section 9). The setting that depends on
  it is `UseNormallyClosedLimitSwitches = false`.
  - If the setting is `true` on this wiring, both limits read inverted: the closed roof reads as open. A Close then
    latches `DriveNotRunning`, because the closed limit's contact holds the reverse run input open, and the roof never
    moves.
  - If the commons are on `TB-4`, IN3 never conducts. The drive reads as faulted from every position, `DriveFault`
    latches at start-up, and nothing moves.
- **The monitoring contacts are normally open.** The setting that depends on it is `UseNormallyClosedLimitSwitches`,
  as above. A broken wire therefore reads "not at the limit", which the controller cannot tell from a roof between its
  limits. The fail-safe result is that the limit's run-circuit contact stops the drive (step 4).
- **Electrical noise from the drive and the motor does not hold an input in the wrong state for longer than the
  debounce.** This assumes the separation rule in the hardware overview, section 3, is followed: the motor leads run
  apart from the Cat6 relay, limit and monitoring cables (section 12).
  The setting that depends on it is `LimitSwitchDebounce` (25 ms). The emulated plant cannot show noise, but every
  result it could have is a stop:
  - Chatter on the start limit inside the debounce is ignored (`RoofControllerLimitDepartureTests`), and so is an IN4
    dropout shorter than the 250 ms run-loss window (`RoofControllerDriveRunTests`).
  - A glitch on the destination limit stops the move early. A glitch on both limits during a move latches
    `ContradictoryLimitInputs` (step 3, `RoofControllerRelayBehaviorTests`), and a glitch on IN3 latches `DriveFault`
    (C4).
  - Noise that hides a limit does not keep the drive running, because the limit's run-circuit contact stops it (C1
    step 4).
  - The ME-8108 contact bounce is assumed to be 3 ms, since the datasheet gives none, and it settles inside the
    debounce (`PlantLimitSwitchTests`).

### C4. VFD fault input polarity (IN3)

| Step | Checked | Scenario |
|---|---|---|
| 1 | The production setting on the fail-safe wiring, and the code default on the same wiring | `C4FaultInputPolarityScenarios.AHealthyDrive_ReadsHealthy_AndATripStopsTheMove_WithDriveFault`, `C4FaultInputPolarityScenarios.TheCodeDefault_OnTheFailSafeWiring_LatchesDriveFaultAtStartup` |
| 2 | A healthy, powered drive reads `isDriveFaultActive = false`, and Open is accepted | `C4FaultInputPolarityScenarios.AHealthyDrive_ReadsHealthy_AndATripStopsTheMove_WithDriveFault` |
| 3 | A drive trip stops the move with `DriveFault`, latches it, and Open and Close are refused with `FaultLatched` (HTTP 409) | `C4FaultInputPolarityScenarios.AHealthyDrive_ReadsHealthy_AndATripStopsTheMove_WithDriveFault` |
| 4 | A broken IN3 wire and lost drive power each read as a fault and latch `DriveFault` | `C4FaultInputPolarityScenarios.WithTheFailSafeWiring_FailsSafe_AsAFault` |
| 5 | Resetting the drive and clearing the latch (C5) | `C5ClearFaultPulseScenarios.TheDefaultPulse_ClosesRly3ForItsLength_AndResetsTheTrippedDrive` |
| — | Cycling the drive's mains during travel latches `DriveFault`. A start within 2 s of power-up trips the drive with F_UF. | `LifecycleScenarios.TheDrivesMainsCycled_DuringTravel_LatchesDriveFault_AndAStartWithinTwoSecondsOfPowerUp_TripsF_UF` |

**Installation assumptions**

- **IN3 is on the fail-safe wiring:** `P140 = 3`, with `TB-16/17` closed while the drive is healthy, so IN3 reads HIGH
  when healthy (hardware overview, section 9.3). The setting that depends on it is `FaultInputActiveHigh = false`. With
  the code default (`true`) on this wiring, the controller latches `DriveFault` at start-up and nothing moves. On the
  fail-safe wiring, a broken wire and lost drive power both read as a fault (step 4). With HIGH-means-fault wiring they
  would read healthy, which is why the fail-safe wiring is the documented one.
- **The drive does not accept a run command within 2 s of power-up, and trips with F_UF** (the SMVector manual). No
  setting depends on it. The emulated drive trips as the manual documents, and the controller latches `DriveFault`.

### C5. ClearFault pulse length

The API accepts `POST .../ClearFault?pulseMs=N`, with N from 50 to 2000 (default 250). The hardware overview, section
8.3, recommends about 100-300 ms. The scenarios measure RLY3 on the emulated relay's contact.

| Step | Checked | Scenario |
|---|---|---|
| 1 | The default pulse closes RLY3 for its length and then opens it | `C5ClearFaultPulseScenarios.TheDefaultPulse_ClosesRly3ForItsLength_AndResetsTheTrippedDrive` |
| 2 | A pulse shorter than the drive needs leaves the latch, and a longer one resets the drive ten times in a row | `C5ClearFaultPulseScenarios.APulseShorterThanTheDriveNeeds_LeavesTheLatch_AndALongerOneResetsIt_TenTimesInARow` |
| 3 | If IN3 still reports a fault after the pulse, the latch remains | `C5ClearFaultPulseScenarios.APulseShorterThanTheDriveNeeds_LeavesTheLatch_AndALongerOneResetsIt_TenTimesInARow` |
| 4 | With healthy inputs, `isFaultLatched` becomes `false` and `lastStopReason` keeps the original cause | `C5ClearFaultPulseScenarios.TheDefaultPulse_ClosesRly3ForItsLength_AndResetsTheTrippedDrive` |
| 5 | ClearFault during a move is refused. A Stop during a 2000 ms pulse ends the pulse, and RLY3 opens. | `C5ClearFaultPulseScenarios.ClearFault_WhileMoving_IsRefused`, `C5ClearFaultPulseScenarios.Stop_DuringALongPulse_EndsThePulse_AndNeitherClearsTheLatch` |
| 6 | Stop alone does not clear the latch | `C5ClearFaultPulseScenarios.Stop_DuringALongPulse_EndsThePulse_AndNeitherClearsTheLatch` |

**Installation assumptions**

- **The drive resets on a pulse of at least 20 ms on its reset input.** The SMVector manual does not document the
  shortest pulse; `SmVectorAssumptions.MinimumClearFaultPulse` is the plant's figure. The setting that depends on it is
  the ClearFault `pulseMs`: the default 250 ms leaves a wide margin. If the drive needs a longer pulse than the one
  sent, the latch stays and motion stays refused (step 3). The operator then sends a longer pulse, up to 2000 ms.

### C6. Drive-running input (IN4) and `AtSpeedConfirmationTimeout`

Production uses `AtSpeedConfirmationTimeout = 3 s` with `P142 = 1` (Run). With it set:

- A start is refused with `InterlockActive` while IN4 still reports the drive running.
- IN4 must go HIGH within the window after a start. Otherwise the roof stops with `DriveNotRunning`.
- If IN4 goes LOW for 250 ms after it confirmed, without the destination limit, the roof stops with
  `DriveNotRunning`. Causes include an external stop, a drive trip the fault input missed, or a broken wire.
- If IN4 is still HIGH `DriveStopConfirmationTimeout` after a stop (by default the same window), a Critical entry is
  logged once per stop.

| Step | Checked | Scenario |
|---|---|---|
| 1 | IN4 confirms every start well inside the 3 s window, ten times in each direction | `C6DriveRunningInputScenarios.TheRunInput_ConfirmsEveryStart_WellInsideTheWindow_TenTimesEachWay` |
| 2 | With the IN4 wire broken before the start, the move stops with `DriveNotRunning` at the window | `C6DriveRunningInputScenarios.ABrokenRunInputWire_BeforeTheStart_StopsTheMove_WithDriveNotRunning_AtTheWindow` |
| 3 | After a stop, IN4 drops before `DriveStopConfirmationTimeout`. With the coast stop, it drops at once and a reversal proceeds. A continuous DC brake keeps IN4 HIGH: this is logged Critical once, and every move is refused. | `C6DriveRunningInputScenarios.AfterACoastStop_TheRunInputDropsAtOnce_AndAReversalProceedsAtOnce`, `C6DriveRunningInputScenarios.AContinuousDcBrake_ThatKeepsTheRunOutputOn_IsLoggedCriticalOnce_AndRefusesEveryMove` |
| — | Several other cases. A wire that breaks during travel stops the move after 250 ms. After a ramp stop, a reversal is refused until IN4 drops. With `P142 = 6` (At Speed) and 20 s acceleration, the window runs out before the drive reaches speed, and `DriveNotRunning` latches. | `C6DriveRunningInputScenarios.ABrokenRunInputWire_DuringTravel_StopsTheMove_WithDriveNotRunning_After250Ms`, `C6DriveRunningInputScenarios.AfterARampStop_AReversalIsRefused_UntilTheRunInputDrops`, `C6DriveRunningInputScenarios.TheAtSpeedOutput_WithATwentySecondAcceleration_OutlastsTheWindow_AndLatchesDriveNotRunning` |

**Installation assumptions**

- **`P142 = 1` (Run): `TB-14` follows the run command, and the drive coast-stops.** The settings that depend on it are
  `AtSpeedConfirmationTimeout` (3 s) and `DriveStopConfirmationTimeout`.
  - With `P142 = 6` (At Speed), the window must be longer than `P104`. With 20 s acceleration, a 3 s window latches
    `DriveNotRunning` on every move.
  - With a ramp stop (`P111` = 2 or 3), set `DriveStopConfirmationTimeout` longer than `P105`, and expect a reversal to
    be refused until IN4 drops.
  - With a DC brake (`P111` = 1 or 3), the Run output may stay on while braking; the plant tests cover both ways. Set
    `DriveStopConfirmationTimeout` longer than `P175`, plus `P105` with `P111` = 3.
  - Never use `P175` = 999.9 (continuous). It keeps IN4 HIGH until the next run, which refuses every move.

### C7. `MaxConsecutiveInputReadFailures` and I2C failures

The controller stops with `InputReadFailure` after this many consecutive failed input reads during a move (3 in
production, range 1-10). Detection takes roughly this number times `DigitalInputPollInterval`, plus one
`PeriodicVerificationInterval` if polling is slow. The scenarios inject the failures in the emulated register client,
in place of an interrupted bus.

| Step | Checked | Scenario |
|---|---|---|
| 1 | The bus fails while a move is commanded | `C7BusFailureScenarios.ABusOutage_WhileMoving_LatchesTheStop_TheHardwiredLimitStopsTheDrive_AndTheRelaysGoOffWhenTheBusReturns` |
| 2 | The controller reports `inputsHealthy = false` and `relayRegisterReadsHealthy = false`. It stops with `InputReadFailure` or `RelayVerificationFailed`, latches the fault, and reports `/health/ready` unhealthy. | `C7BusFailureScenarios.ABusOutage_WhileMoving_LatchesTheStop_TheHardwiredLimitStopsTheDrive_AndTheRelaysGoOffWhenTheBusReturns` |
| 3 | While the bus is down the relays cannot be commanded (`Unverified`), and the limit's run-circuit contact stops the drive | `C7BusFailureScenarios.ABusOutage_WhileMoving_LatchesTheStop_TheHardwiredLimitStopsTheDrive_AndTheRelaysGoOffWhenTheBusReturns` |
| 4 | When the bus returns, the relays go off, and Open stays refused until ClearFault succeeds | `C7BusFailureScenarios.ABusOutage_WhileMoving_LatchesTheStop_TheHardwiredLimitStopsTheDrive_AndTheRelaysGoOffWhenTheBusReturns` |
| 5 | An interruption shorter than the threshold does not stop the move | `C7BusFailureScenarios.AnInterruptionShorterThanTheThreshold_DoesNotStopTheMove` |
| 6 | With the roof idle, a failed relay register read reports unhealthy. After the second failure, the controller re-runs the all-off sequence: it latches `RelayVerificationFailed` if the reads keep failing, and clears without a latch if the re-run verifies. | `C7BusFailureScenarios.RelayRegisterReadsFailing_WhileIdle_ReportUnhealthy_ThenLatchRelayVerificationFailed`, `C7BusFailureScenarios.TwoFailedRelayRegisterReads_WhileIdle_RerunTheAllOffSequence_AndItVerifiesWithoutALatch` |

**Installation assumptions**

- **An I2C failure shows as failed reads and writes, not as plausible wrong values.** The settings that depend on it
  are `MaxConsecutiveInputReadFailures` (3) and `DigitalInputPollInterval`. The controller cannot command the relays
  while the bus is down. The fail-safe result is that the energized relays hold, the limit's run-circuit contact stops
  the drive (C1 step 4), and the latch waits for the operator.

### C8. Maximum-run watchdog cap

| Step | Checked | Scenario |
|---|---|---|
| 1 | The travel time metric measures the full travel in each direction (see [Motion timing](telemetry.md#motion-timing)) | `C8WatchdogScenarios.TheTravelTimeMetric_MeasuresTheFullTravelInEachDirection_AndTheDriveTimings` |
| 2 | `SafetyWatchdogTimeout` is set to its 5 s minimum through `POST .../Configuration`, with `ExpectedVersion` and `ConfirmSafetyCriticalChange = true` | `C8WatchdogScenarios.TheWatchdog_StopsTheMoveAtItsTime_FromTheFirstOpen_AndRepeatedOpensDoNotExtendIt` |
| 3 | The roof stops at the watchdog time, counted from the first Open; repeated Opens do not extend it. The stop is `SafetyWatchdogTimeout`, and the fault latches. | `C8WatchdogScenarios.TheWatchdog_StopsTheMoveAtItsTime_FromTheFirstOpen_AndRepeatedOpensDoNotExtendIt` |
| 4 | The production value is restored | `C8WatchdogScenarios.TheWatchdog_StopsTheMoveAtItsTime_FromTheFirstOpen_AndRepeatedOpensDoNotExtendIt` |

**Installation assumptions**

- **A full travel takes about 21 s in each direction:** 2 m at 0.1 m/s, with 2 s acceleration (`P104`). The setting
  that depends on it is `SafetyWatchdogTimeout`, which must be the full travel plus a margin, within 5-600 s
  (production: 2 min 30 s).
  - The installation's own figure is the maximum of `roof.controller.travel.duration` for full travels, and the
    telemetry records it on every move.
  - If the watchdog is shorter than the travel, every full move stops with `SafetyWatchdogTimeout` and latches (step 3).
  - If it is far longer, only the limits bound a move.

### C9. Operator lease expiry

The lease applies only when `OperatorLeaseTimeout` is set (2-120 s); production leaves it unset.

| Step | Checked | Scenario |
|---|---|---|
| 1 | With a 5 s lease, renewing every 2 s keeps the roof moving and refreshes `leaseSecondsRemaining` | `C9OperatorLeaseScenarios.RenewingKeepsTheRoofMoving_AndStoppingRenewals_StopsItWithOperatorLeaseExpired` |
| 2 | When renewals stop, the roof stops with `OperatorLeaseExpired` within the lease plus one `PeriodicVerificationInterval` | `C9OperatorLeaseScenarios.RenewingKeepsTheRoofMoving_AndStoppingRenewals_StopsItWithOperatorLeaseExpired` |
| 3 | `POST .../Lease` while idle returns 409 `LeaseNotActive` and starts nothing | `C9OperatorLeaseScenarios.ALeaseRenewal_WhileIdle_IsRefused_AndStartsNothing` |
| 4 | Client loss. The console renews the lease only while its connection is up. When the connection is lost, the lease runs out and the roof stops with `OperatorLeaseExpired`. After a reconnect inside the lease, renewal does not resume, and the console shows a "Lease" warning. | `C9ConsoleLeaseBrowserTests.TheConsoleRenewsTheLeaseDuringAMove_AndClosingIt_LetsTheLeaseRunOut_AndStopsTheRoof`, `C9ConsoleLeaseBrowserTests.WhenTheConsoleLosesItsConnection_TheLeaseRunsOut_AndAfterAReconnect_RenewalDoesNotResume_AndTheConsoleWarns` |

**Installation assumptions**

- **Operators drive the roof from the console, or from a client that renews the lease.** The setting that depends on it
  is `OperatorLeaseTimeout`.
  - A silently dropped mobile connection is noticed within about 30 s (the SignalR client timeout). The roof then stops
    about 30 s plus the lease after the connection was lost.
  - Without the lease, losing the client does not stop motion: only the watchdog (C8) and the limits bound a move, and
    the reconnect dialog's Stop (C15) remains.

### C10. Starting at a limit: departure from both limits

| Step | Checked | Scenario |
|---|---|---|
| 1 | Open from the closed limit. The closed limit releases without a false stop, and the roof stops at the open limit with `LimitSwitchReached`. | `C10DepartureScenarios.FromEitherLimit_TheStartLimitReleases_WithoutAFalseStop_AndTheRoofStopsAtTheOther` |
| 2 | Close from the open limit, with the same checks in reverse | `C10DepartureScenarios.FromEitherLimit_TheStartLimitReleases_WithoutAFalseStop_AndTheRoofStopsAtTheOther` |
| 3 | The start limit is actuated again after it released. The move stops with `StartLimitReasserted` and latches. | `C10DepartureScenarios.TheStartLimit_ActuatedAgainAfterItReleased_StopsTheMove_WithStartLimitReasserted` |
| 4 | The start limit never releases. With `DepartureReleaseTimeout` set, the move stops with `DepartureLimitNotReleased` and latches. Without it, the move runs until the watchdog or the other limit ends it. The scenario sets `SafetyWatchdogTimeout` to 5 s on a 1 m roof; with the production settings, the open limit ends it first. | `C10DepartureScenarios.AStartLimitThatNeverReleases_WithTheDepartureTimeout_StopsWithDepartureLimitNotReleased`, `C10DepartureScenarios.AStartLimitThatNeverReleases_WithoutTheDepartureTimeout_RunsUntilTheWatchdog` |
| — | Swapped motor leads from the closed limit. With the departure timeout, the roof stops before the hard stop. Without it, the roof reaches the hard stop and the drive trips (`DriveFault`). | `C10DepartureScenarios.SwappedMotorLeads_FromTheClosedLimit_TheDepartureTimeoutStopsTheRoof_BeforeTheHardStop`, `C10DepartureScenarios.SwappedMotorLeads_FromTheClosedLimit_WithoutTheDepartureTimeout_ReachTheHardStop` |

**Installation assumptions**

- **The roof's mechanics:** 2 m of travel at 0.1 m/s, with hard stops 60 mm past each limit's operating point. At
  `P104` = 2 s, the start limit releases about 1.0 s after the command, or about 2.8 s at 20 s acceleration. A
  wrong-way move reaches the stop behind the limit in about 1.5 s (`PlantDocumentedFiguresTests`).
  - The setting that depends on it is `DepartureReleaseTimeout`, which is off in production. When it is on, it must be
    longer than the release time plus `LimitSwitchDebounce`, and shorter than the wrong-way time.
  - With the motor leads swapped and no timeout, a move from a limit reaches the hard stop before the stall trip.
- **The drive coast-stops (`P111` = 0, the factory default), about 10 mm past a limit's operating point.** No
  controller setting depends on it. A ramp stop with `P105` = 2 s reaches the hard stop (`PlantDriveTests`; hardware
  overview, section 11), so keep the coast stop unless the installed ramp is known to stop well inside the ME-8108
  overtravel.

### C11. Container stop with an active camera stream

| Step | Checked | Scenario |
|---|---|---|
| 1 | A camera stream is open through the proxy while a move is commanded | `LifecycleScenarios.AHostShutdown_DuringTravel_WithACameraStreamOpen_StopsTheRoof_EndsTheStream_AndTheNextHostStartsIdle` |
| 2 | The host is stopped as `docker stop -t 30` stops it: in process, and as a real container | `LifecycleScenarios.AHostShutdown_DuringTravel_WithACameraStreamOpen_StopsTheRoof_EndsTheStream_AndTheNextHostStartsIdle`, `deploy-scenarios.sh lifecycle` |
| 3 | The roof stops with `HostShutdown` and verified relays, and the stream ends. The container exits within the grace period, and is not killed at its end (status 137). | `LifecycleScenarios.AHostShutdown_DuringTravel_WithACameraStreamOpen_StopsTheRoof_EndsTheStream_AndTheNextHostStartsIdle`, `deploy-scenarios.sh lifecycle` |
| — | A crash during travel leaves the relays held. The restarted controller turns them off; if the crash outlasts the travel, the open limit's contact stops the drive first. A killed container stays down until it is started again. | `LifecycleScenarios.ACrash_DuringTravel_LeavesTheRelaysHeld_AndTheRestartedControllerTurnsThemOff`, `LifecycleScenarios.ACrash_ThatOutlastsTheTravel_LeavesTheOpenLimitToStopTheDrive_AndTheRestartedControllerReportsOpen`, `deploy-scenarios.sh lifecycle` |

**Installation assumptions**

- **The HAT holds its relays when the controller process dies**, until something writes the register. The emulated HAT
  holds them, as the #29 plant assumes. The settings that depend on it are the deploy script's `docker stop -t 30` and
  the host's shutdown timeout. The fail-safe result is that the roof keeps moving until the limit's run-circuit contact
  stops it (C1 step 4), and the restarted controller turns the relays off.

If a shutdown log shows `Shutdown could not verify the relay register all-off state`, the controller re-runs the all-off
sequence every 500 ms. It stops when the register verifies, when the controller is disposed, or after 15 s. Disposal
usually comes first. The host waits up to 5 s for each of its shutdown calls (usually two, at most three), so the retry
ends about 10 s after the stop request, plus the time the web server takes to stop.

After `Shutdown could not verify the relay register all-off state`, one of these follows:

- `Shutdown stop retry N verified the relay register all-off` (Warning): a retry verified the relays off.
- `Shutdown stop retry gave up after N attempts` (Critical): the relays were never verified off.

A Critical `Roof controller shutdown stop did not complete within` entry means a HAT call was stuck and was abandoned.
Later triggers log `shutdown stop not attempted`, and disposal logs `Disposal could not acquire the controller lock`.

After either Critical entry, treat the relay state as unknown and use the independent stop (C1).

### C12. Deployment script stop gate, pre-flight, remote check and rollback

The container scenarios run `deploy-roofcontroller-rpi.sh` against a controller in HAT emulator mode on real Docker. The
controller serves HTTPS with a generated certificate and checks it with `REMOTE_CA_CERT`. A monitor samples the relay
register every 0.1 s.

| Step | Checked | Scenario |
|---|---|---|
| 1 | A deploy with the roof idle. The pre-flight passes, the Stop returns a verified all-off, and the deploy ends with `Deployment complete and verified`. The old controller is kept stopped as `roof-controller-previous`. | `deploy-scenarios.sh c12` |
| 2 | A deploy while the roof moves. The script stops the roof first (a verified all-off) and only then replaces the controller. | `deploy-scenarios.sh c12` |
| 3 | An operator key that is not configured fails the pre-flight, and the running controller is untouched | `deploy-scenarios.sh c12` |
| 4 | A renamed certificate file, a wrong certificate password and no `RoofOperator` key each fail the pre-flight, and the running controller is untouched | `deploy-scenarios.sh c12` |
| 5 | `ALLOWED_HOSTS=localhost` passes the pre-flight but fails the remote check. The script rolls back: the previous controller is running and ready under `roof-controller`, and the exit status is non-zero. The rollback time is in the results. | `deploy-scenarios.sh c12` |
| 6 | `--rollback` twice: the versions swap and swap back, and each is verified from the deploying machine | `deploy-scenarios.sh c12` |
| 7 | Throughout steps 3-6, every relay-register sample is 0 and the emulator records no violation | `deploy-scenarios.sh c12` |
| — | The move from the Compose `pi` profile to the deploy script and back ([deployment](deployment.md#moving-between-compose-and-the-deploy-script)). The script refuses a Compose container, and Compose refuses while the script's container exists. | `deploy-scenarios.sh migration` |

**Installation assumptions**

- **The deploying machine reaches the Pi over HTTPS and trusts its certificate** through `REMOTE_CA_CERT`. The settings
  that depend on it are `PI_HOST`, `REMOTE_CA_CERT` and `ALLOWED_HOSTS`. If the check from that machine fails, the
  script rolls back to the previous controller (step 5).
- **The Pi's `linux/arm64` image behaves as the runner's `linux/amd64` image does.** The container scenarios build for
  the runner (`BUILD_PLATFORM`), and the "Pi image" workflow builds the `linux/arm64` image from the same Dockerfile.
  No setting depends on it, and neither image is deployed.

### C13. RV-5: telemetry collector outage does not delay Stop

OTLP export runs whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set. A collector outage must not add latency to commands or
to the stop path.

| Step | Checked | Scenario |
|---|---|---|
| 1 | The baseline, with the collector reachable: Stop idle and during moves, timed from the request to all relays off | `C13TelemetryOutageScenarios.ACollectorThatTimesOutOrRefuses_AddsNoLatencyToStop_LimitStops_OrWatchdogStops` |
| 2 | The same with a collector that never answers (a timeout), and with a closed port (an immediate refusal) | `C13TelemetryOutageScenarios.ACollectorThatTimesOutOrRefuses_AddsNoLatencyToStop_LimitStops_OrWatchdogStops` |
| 3 | A limit stop and a watchdog stop during each outage, compared with the baseline | `C13TelemetryOutageScenarios.ACollectorThatTimesOutOrRefuses_AddsNoLatencyToStop_LimitStops_OrWatchdogStops` |
| 4 | An outage lasting the soak, with resources flat (assessed by the nightly soak) | `C14SoakScenarios.TheRoof_CyclesForTheSoakDuration_WithFlatResources_AndEveryInvariantHeld` |

The pass criteria for steps 1-3:

- The 99th percentile of Stop and limit-stop latency adds no more than 100 ms to the baseline.
- Every Stop completes in under 1 s.
- No command fails.

**Installation assumptions**

- **An unreachable collector either times out or refuses the connection.** The setting that depends on it is
  `OTEL_EXPORTER_OTLP_ENDPOINT`. The scenarios cover both, and the soak exports to a collector that never answers
  throughout.

### C14. RV-6: soak

The production settings run against the emulated plant with the roof shortened to 25 cm, in real time. The soak cycles
Open, Stop, Close and Stop through the API, and every fifth cycle stops in mid-travel first. It polls status at a
client's rate, opens and closes a camera stream through the proxy, and exports to a collector that never answers. A
cycle takes about 10 s where the installation moves about twice a night, so an hour of soak is about a year of moves.
The scenario job runs it for 90 s, which records resources but does not assess them: that needs at least 5 minutes after
the warm-up. Only the nightly soak checks that resources stay flat. It runs for two hours and publishes
`soak-summary.md` (the invariant table and the motion timings), `soak-summary.json`, `soak-samples.csv` and
`soak-log.txt` (every Warning or above, and the latest 1,000 log entries). The soak's host writes no console log: the
test framework keeps a test's console output in memory, which grows the heap the soak measures.

| Step | Checked | Scenario |
|---|---|---|
| — | The soak must meet every check below. | `C14SoakScenarios.TheRoof_CyclesForTheSoakDuration_WithFlatResources_AndEveryInvariantHeld` |

The soak's checks:

- Every cycle ends as commanded. Limit events match the cycles one for one.
- Only limit arrivals stop the roof for safety. There is no fault latch, and no Error or Critical log entry.
- No plant invariant breaks.
- Status always answers, and `statusVersion` only increases.
- The controller is ready at every sample, with healthy and fresh input and relay reads.
- Every Stop completes in under 1 s, and Stop latency does not drift through the outage: each quarter of the soak
  stays within 100 ms (the C13 margin) of the first quarter, by the median and, once every quarter has at least 100
  Stops (a soak of about half an hour or more), by the 99th percentile. C13 steps 1-3 compare Stops during an outage
  with a reachable collector.
- After the warm-up, the working set and the managed heap grow less than 10%, and threads and file descriptors stay
  flat.
- Every camera stream closes.
- The exporter keeps retrying the collector.

**Installation assumptions**

- **The soak's resources stand for the Pi's.** The soak measures the test process, which runs the emulator and the
  controller, on a CI runner. A leak in either fails it. No setting depends on it. The Pi's memory and SD card are not
  emulated.
- **Docker's `local` log driver rotates the controller's logs.** The settings that depend on it are the driver's
  `max-size` (10 MB) and `max-file` (5), which both Compose profiles and the deploy script set. The emulator does not
  show disk growth. Container logs are in the container's folder:
  `sudo du -sb /var/lib/docker/containers/$(docker inspect --format '{{.Id}}' roof-controller)/local-logs`.

### C15. Stop from the console's reconnect dialog

The reconnect dialog's **Stop roof** button sends `POST /console/stop` without the console's live connection. The
browser test runs the console in Chromium with phone emulation. The lease is unset and the watchdog is longer
than the test, so only the dialog's Stop can end the move.

| Step | Checked | Scenario |
|---|---|---|
| 1 | The console loses its connection during a move, and the reconnect dialog appears | `C15ReconnectDialogStopBrowserTests.WhileTheConsoleIsDisconnected_TheDialogsStop_SaysWhenItCannotReachTheController_AndStopsTheRoofWhenItCan` |
| 2 | With the network still down, **Stop roof** says within about 5 s that Stop could not reach the controller, and points to the stop control at the roof | `C15ReconnectDialogStopBrowserTests.WhileTheConsoleIsDisconnected_TheDialogsStop_SaysWhenItCannotReachTheController_AndStopsTheRoofWhenItCan` |
| 3 | With the network back but before the console reconnects, **Stop roof** reports the stop as acknowledged, and the roof stops with `NormalStop` | `C15ReconnectDialogStopBrowserTests.WhileTheConsoleIsDisconnected_TheDialogsStop_SaysWhenItCannotReachTheController_AndStopsTheRoofWhenItCan` |

**Installation assumptions**

- **Operators use the console from a phone or tablet browser that may lose its network.** No setting depends on it. If
  the controller cannot be reached, the dialog says so within about 5 s and points to the stop control at the roof
  (step 2). That control, and the independent stop (C1), do not depend on the network.

## Results

Each run of the "Scenarios" workflow is the record:

- the test results;
- the container scenarios' table, with the deploy and rollback times;
- the nightly soak's invariant table and motion timings.

The tables are in the run summary and the run's artifacts. Nothing is recorded by hand.
