using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.Simulation;

/// <summary>
/// Deterministic model of the roof plant: the SM-I-010 HAT and its relays, the SMVector drive, the roof and the two
/// ME-8108 limit switches, wired as the wiring doc describes (or with a <see cref="WiringFault"/>). Time comes only
/// from the <see cref="TimeProvider"/>: every access first calls <see cref="Sync"/>, which advances the plant in fixed
/// steps to the provider's time. With a manual time provider the plant is exactly reproducible.
/// </summary>
/// <remarks>
/// Every public member locks <see cref="SyncRoot"/>. One step runs, in order: due scheduled actions, the relays, the
/// drive's control inputs from the circuit, the drive, the roof, the limit switches, then the HAT inputs and the
/// invariants.
/// </remarks>
public sealed class RoofPlant
{
    private readonly TimeProvider _timeProvider;
    private readonly long _originTimestamp;
    private readonly long _stepTicks;
    private readonly List<PlantEvent> _history = new();
    private readonly List<PlantViolation> _violations = new();
    private readonly List<(TimeSpan At, long Sequence, Action<RoofPlant> Action)> _scheduled = new();
    private long _steps;
    private long _scheduleSequence;
    private WiringFault _wiring;
    private bool _externalStopOpen;
    private bool _jammed;
    private SmVectorInputs _driveInputs;
    private bool _bothDirectionContacts;
    private bool _atHardStop;
    private bool _runCommanded;
    private TimeSpan? _driveStopDeadline;
    private bool _driveStopViolated;
    private bool _stalled;
    private int _historyDropped;

    public RoofPlant(RoofPlantOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        Options = options;
        _timeProvider = timeProvider;
        _originTimestamp = timeProvider.GetTimestamp();
        _stepTicks = options.StepSize.Ticks;
        _wiring = options.Wiring;

        Drive = new SmVectorDrive(options.Drive, options.DriveAssumptions, powered: true, options.InitialDriveUptime);
        Hat = new SmI010Board(options.Hat);
        Position = options.InitialPosition;
        OpenLimit = new Me8108LimitSwitch("FWD (open) limit", options.OpenLimit, OpenLimitAngle(Position));
        ClosedLimit = new Me8108LimitSwitch("REV (closed) limit", options.ClosedLimit, ClosedLimitAngle(Position));

        Drive.Changed += detail => Record(PlantEventKind.Drive, detail);
        Hat.Changed += detail => Record(detail.StartsWith("RLY", StringComparison.Ordinal) ? PlantEventKind.Relay : PlantEventKind.Hat, detail);
        OpenLimit.Changed += OnLimitChanged;
        ClosedLimit.Changed += OnLimitChanged;

        UpdateHatInputs();
    }

    public RoofPlantOptions Options { get; }

    /// <summary>Lock that serializes the plant. The emulated HAT client holds it for each register access.</summary>
    public object SyncRoot { get; } = new();

    public SmVectorDrive Drive { get; }

    public SmI010Board Hat { get; }

    public Me8108LimitSwitch OpenLimit { get; }

    public Me8108LimitSwitch ClosedLimit { get; }

    /// <summary>Simulated time since the plant was created.</summary>
    public TimeSpan Elapsed
    {
        get { lock (SyncRoot) { return ElapsedCore; } }
    }

    /// <summary>Roof position, metres (see <see cref="RoofMechanicsOptions"/>).</summary>
    public double Position { get; private set; }

    /// <summary>Roof velocity, metres per second, positive toward open.</summary>
    public double Velocity { get; private set; }

    /// <summary>Position of the closed-side hard stop.</summary>
    public double ClosedHardStop => -Options.Mechanics.HardStopBeyondOperatePoint;

    /// <summary>Position of the open-side hard stop.</summary>
    public double OpenHardStop => Options.Mechanics.TravelMeters + Options.Mechanics.HardStopBeyondOperatePoint;

    /// <summary>Largest distance the roof went past the open limit's operating point.</summary>
    public double MaximumOpenOvertravel { get; private set; }

    /// <summary>Largest distance the roof went past the closed limit's operating point.</summary>
    public double MaximumClosedOvertravel { get; private set; }

    /// <summary>Levels at TB-1, TB-13A, TB-13B and TB-13C in the last step.</summary>
    public SmVectorInputs DriveInputs
    {
        get { lock (SyncRoot) { Sync(); return _driveInputs; } }
    }

    /// <summary>Opto input levels the HAT reports in register 3, bit 0 = IN1.</summary>
    public byte InputBits
    {
        get { lock (SyncRoot) { Sync(); return Hat.InputBits; } }
    }

    /// <summary>The wiring. Can change during a run to model a wire breaking.</summary>
    public WiringFault Wiring
    {
        get { lock (SyncRoot) { return _wiring; } }
        set
        {
            lock (SyncRoot)
            {
                Sync();
                if (_wiring != value)
                {
                    Record(PlantEventKind.Injected, $"wiring {_wiring} -> {value}");
                    _wiring = value;
                    UpdateHatInputs();
                }
            }
        }
    }

    /// <summary>
    /// An external contact in series with TB-1 is open (an emergency stop or the drive's STOP input opened some other
    /// way). The wiring doc has no such contact; tests use it to remove the permit independently of RLY4.
    /// </summary>
    public bool ExternalStopOpen
    {
        get { lock (SyncRoot) { return _externalStopOpen; } }
        set { lock (SyncRoot) { Sync(); SetInjected(ref _externalStopOpen, value, "external STOP"); } }
    }

    /// <summary>The roof cannot move (jammed). A driven motor then stalls.</summary>
    public bool Jammed
    {
        get { lock (SyncRoot) { return _jammed; } }
        set { lock (SyncRoot) { Sync(); SetInjected(ref _jammed, value, "jam"); } }
    }

    public IReadOnlyList<PlantEvent> History
    {
        get { lock (SyncRoot) { Sync(); return _history.ToArray(); } }
    }

    public IReadOnlyList<PlantViolation> Violations
    {
        get { lock (SyncRoot) { Sync(); return _violations.ToArray(); } }
    }

    public int HistoryDropped
    {
        get { lock (SyncRoot) { return _historyDropped; } }
    }

    private TimeSpan ElapsedCore => TimeSpan.FromTicks(_steps * _stepTicks);

    /// <summary>
    /// Nothing can change until an outside action or a scheduled one: the roof is at rest, no relay, contact, input
    /// filter or trip timer is pending, and the drive's inputs are what it last saw.
    /// </summary>
    private bool IsQuiescent
        => Velocity == 0 && !_stalled && Drive.IsSettled && Hat.IsSettled && OpenLimit.IsSettled && ClosedLimit.IsSettled
           && EvaluateDriveInputs() == _driveInputs;

    /// <summary>Advances the plant to the time provider's time.</summary>
    public void Sync()
    {
        lock (SyncRoot)
        {
            var target = _timeProvider.GetElapsedTime(_originTimestamp).Ticks / _stepTicks;
            while (_steps < target)
            {
                RunDueActions();
                StepOnce();
                if (_steps < target && IsQuiescent)
                {
                    var next = target;
                    if (_scheduled.Count > 0)
                    {
                        next = Math.Min(next, Math.Max(_steps, (_scheduled[0].At.Ticks + _stepTicks - 1) / _stepTicks));
                    }

                    if (next > _steps)
                    {
                        Drive.AdvanceIdle(TimeSpan.FromTicks((next - _steps) * _stepTicks));
                        _steps = next;
                    }
                }
            }

            RunDueActions();
        }
    }

    /// <summary>Runs <paramref name="action"/> under the plant lock when simulated time reaches <paramref name="at"/>.</summary>
    public void Schedule(TimeSpan at, Action<RoofPlant> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (SyncRoot)
        {
            _scheduled.Add((at, _scheduleSequence++, action));
            _scheduled.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Sequence.CompareTo(b.Sequence));
        }
    }

    /// <summary>Runs <paramref name="action"/> <paramref name="delay"/> after the current simulated time.</summary>
    public void ScheduleAfter(TimeSpan delay, Action<RoofPlant> action)
    {
        lock (SyncRoot)
        {
            Sync();
            Schedule(ElapsedCore + delay, action);
        }
    }

    /// <summary>Applies or removes the drive's mains power (and with it TB-11).</summary>
    public void SetDrivePower(bool powered)
    {
        lock (SyncRoot)
        {
            Sync();
            Drive.SetPower(powered);
            UpdateHatInputs();
        }
    }

    /// <summary>Applies or removes the HAT's power. Losing it drops every relay and resets the registers.</summary>
    public void SetHatPower(bool powered)
    {
        lock (SyncRoot)
        {
            Sync();
            Hat.SetPower(powered);
        }
    }

    /// <summary>Trips the drive.</summary>
    public void TripDrive(SmVectorTrip trip = SmVectorTrip.External)
    {
        lock (SyncRoot)
        {
            Sync();
            Drive.InjectTrip(trip);
            UpdateHatInputs();
        }
    }

    public void SetLimitFault(bool openLimit, LimitSwitchFault fault)
    {
        lock (SyncRoot)
        {
            Sync();
            var limit = openLimit ? OpenLimit : ClosedLimit;
            if (limit.Fault != fault)
            {
                Record(PlantEventKind.Injected, $"{limit.Name} fault {limit.Fault} -> {fault}");
                limit.Fault = fault;
                UpdateHatInputs();
            }
        }
    }

    public void SetRelayFault(int relay, RelayContactFault fault)
    {
        lock (SyncRoot)
        {
            Sync();
            Record(PlantEventKind.Injected, $"RLY{relay} fault {fault}");
            Hat.SetRelayFault(relay, fault);
        }
    }

    /// <summary>Adds an event to the history, for tests that want their own actions in the record.</summary>
    public void Note(string detail)
    {
        lock (SyncRoot)
        {
            Sync();
            Record(PlantEventKind.Injected, detail);
        }
    }

    private void RunDueActions()
    {
        while (_scheduled.Count > 0 && _scheduled[0].At <= ElapsedCore)
        {
            var action = _scheduled[0].Action;
            _scheduled.RemoveAt(0);
            action(this);
        }
    }

    private void StepOnce()
    {
        var dt = Options.StepSize;
        _steps++;

        Hat.StepRelays(dt);

        var inputs = EvaluateDriveInputs();
        RecordInputChanges(inputs);
        _driveInputs = inputs;

        Drive.Step(dt, inputs, _stalled);
        StepRoof(dt);
        OpenLimit.Step(dt, OpenLimitAngle(Position));
        ClosedLimit.Step(dt, ClosedLimitAngle(Position));

        UpdateHatInputs();
        CheckInvariants();
    }

    private SmVectorInputs EvaluateDriveInputs()
    {
        var tb11 = Drive.Tb11Energized;
        var swapRelays = _wiring.HasFlag(WiringFault.SwappedDirectionRelays);
        var forwardRelay = Hat.IsContactClosed(swapRelays ? 2 : 1);
        var reverseRelay = Hat.IsContactClosed(swapRelays ? 1 : 2);
        var endStopsBypassed = _wiring.HasFlag(WiringFault.NoHardwiredEndStops);
        var forwardNc = endStopsBypassed || OpenLimit.NcPathClosed;
        var reverseNc = endStopsBypassed || ClosedLimit.NcPathClosed;

        // TB-1 needs TB-4 (+15 V with P120 = 2), which exists only while the drive is powered.
        var permit = _wiring.HasFlag(WiringFault.StopPermitBypassed)
            || (Hat.IsContactClosed(4) && !_wiring.HasFlag(WiringFault.StopPermitOnTb2));
        return new SmVectorInputs(
            Tb1StopPermit: tb11 && permit && !_externalStopOpen,
            Tb13ARunForward: tb11 && forwardRelay && forwardNc,
            Tb13BRunReverse: tb11 && reverseRelay && reverseNc,
            Tb13CClearFault: tb11 && Hat.IsContactClosed(3));
    }

    private void StepRoof(TimeSpan dt)
    {
        var mechanics = Options.Mechanics;
        var seconds = dt.TotalSeconds;
        var leads = _wiring.HasFlag(WiringFault.SwappedMotorLeads) ? -1 : 1;
        var driven = Drive.IsDriving && Drive.OutputDirection != 0;
        var commanded = driven
            ? Drive.OutputDirection * leads * Drive.OutputFrequencyHz / Drive.Settings.BaseFrequencyHz * mechanics.SpeedAtBaseFrequency
            : 0;

        if (_jammed)
        {
            Velocity = 0;
        }
        else if (driven)
        {
            Velocity = commanded;
        }
        else
        {
            var braking = Drive.IsDcBraking && Drive.Settings.DcBrakeVoltagePercent > 0;
            var decel = (braking ? mechanics.DcBrakeDeceleration : mechanics.CoastDeceleration) * seconds;
            Velocity = Math.Abs(Velocity) <= decel ? 0 : Velocity - Math.Sign(Velocity) * decel;
        }

        Position += Velocity * seconds;
        var atHardStop = false;
        if (Position <= ClosedHardStop)
        {
            Position = ClosedHardStop;
            Velocity = Math.Max(0, Velocity);
            atHardStop = true;
        }
        else if (Position >= OpenHardStop)
        {
            Position = OpenHardStop;
            Velocity = Math.Min(0, Velocity);
            atHardStop = true;
        }

        if (atHardStop != _atHardStop)
        {
            _atHardStop = atHardStop;
            if (atHardStop)
            {
                AddViolation(PlantViolationKind.HardStopContact, $"roof reached the {(Position <= ClosedHardStop ? "closed" : "open")} hard stop");
            }
        }

        MaximumOpenOvertravel = Math.Max(MaximumOpenOvertravel, Position - mechanics.TravelMeters);
        MaximumClosedOvertravel = Math.Max(MaximumClosedOvertravel, -Position);

        var pushingIntoStop = (Position <= ClosedHardStop && commanded < 0) || (Position >= OpenHardStop && commanded > 0);
        _stalled = driven && Drive.OutputFrequencyHz > 0 && (_jammed || pushingIntoStop);
    }

    private double OpenLimitAngle(double position)
        => Options.OpenLimit.Specification.PretravelDegrees + (position - Options.Mechanics.TravelMeters) / Options.OpenLimit.LeverArmMeters * 180 / Math.PI;

    private double ClosedLimitAngle(double position)
        => Options.ClosedLimit.Specification.PretravelDegrees - position / Options.ClosedLimit.LeverArmMeters * 180 / Math.PI;

    private void UpdateHatInputs()
    {
        var tb11 = Drive.Tb11Energized;
        var commonsOk = !_wiring.HasFlag(WiringFault.InputCommonsOnTb4);
        var ncMonitor = _wiring.HasFlag(WiringFault.MonitorOnNcContacts);

        bool Monitor(Me8108LimitSwitch limit)
            => tb11 && commonsOk && (ncMonitor
                ? !limit.Fault.HasFlag(LimitSwitchFault.BrokenMonitorWire) && limit.ContactNcClosed
                : limit.NoPathClosed);

        var forward = Monitor(OpenLimit);
        var reverse = Monitor(ClosedLimit);
        if (_wiring.HasFlag(WiringFault.SwappedLimitInputs))
        {
            (forward, reverse) = (reverse, forward);
        }

        var in3 = tb11 && commonsOk && Drive.RelayOutputClosed && !_wiring.HasFlag(WiringFault.FaultMonitorWireBroken);
        var in4 = tb11 && Drive.Tb14Sinking && !_wiring.HasFlag(WiringFault.RunMonitorWireBroken);
        var bits = (byte)((forward ? 1 : 0) | (reverse ? 2 : 0) | (in3 ? 4 : 0) | (in4 ? 8 : 0));
        var changed = (byte)(bits ^ Hat.InputBits);
        for (var i = 0; i < 4; i++)
        {
            if ((changed & (1 << i)) != 0)
            {
                Record(PlantEventKind.Input, $"IN{i + 1} {(((bits >> i) & 1) != 0 ? "HIGH" : "LOW")}");
            }
        }

        Hat.InputBits = bits;
    }

    private void RecordInputChanges(SmVectorInputs inputs)
    {
        if (inputs.Tb1StopPermit != _driveInputs.Tb1StopPermit)
        {
            Record(PlantEventKind.Input, inputs.Tb1StopPermit ? "TB-1 permit closed" : "TB-1 open (STOP)");
        }

        if (inputs.Tb13ARunForward != _driveInputs.Tb13ARunForward)
        {
            Record(PlantEventKind.Input, $"TB-13A (run forward) {(inputs.Tb13ARunForward ? "HIGH" : "LOW")}");
        }

        if (inputs.Tb13BRunReverse != _driveInputs.Tb13BRunReverse)
        {
            Record(PlantEventKind.Input, $"TB-13B (run reverse) {(inputs.Tb13BRunReverse ? "HIGH" : "LOW")}");
        }

        if (inputs.Tb13CClearFault != _driveInputs.Tb13CClearFault)
        {
            Record(PlantEventKind.Input, $"TB-13C (clear fault) {(inputs.Tb13CClearFault ? "HIGH" : "LOW")}");
        }
    }

    private void CheckInvariants()
    {
        var both = Hat.IsContactClosed(1) && Hat.IsContactClosed(2);
        if (both && !_bothDirectionContacts)
        {
            AddViolation(PlantViolationKind.BothDirectionContactsClosed, "RLY1 and RLY2 contacts closed together");
        }

        _bothDirectionContacts = both;

        // The controller's stop is the direction coils or the RLY4 coil dropping. The drive must stop driving within
        // the relay release time, the drive input response, the ramp (P111 = 2 or 3) and a margin.
        var runCommanded = (Hat.IsCoilEnergized(1) || Hat.IsCoilEnergized(2)) && Hat.IsCoilEnergized(4);
        if (_runCommanded && !runCommanded)
        {
            var bound = Hat.Options.RelayReleaseTime + Options.DriveAssumptions.InputResponseTime + Options.DriveStopMargin;
            if (Drive.Settings.StopMethod is SmVectorStopMethod.Ramp or SmVectorStopMethod.RampWithDcBrake)
            {
                bound += TimeSpan.FromSeconds(Drive.OutputFrequencyHz / Drive.Settings.DecelerationRateHzPerSecond);
            }

            _driveStopDeadline = ElapsedCore + bound;
            _driveStopViolated = false;
        }

        _runCommanded = runCommanded;
        if (_driveStopDeadline is { } deadline)
        {
            if (runCommanded || !Drive.IsDriving)
            {
                _driveStopDeadline = null;
            }
            else if (ElapsedCore > deadline && !_driveStopViolated)
            {
                _driveStopViolated = true;
                AddViolation(PlantViolationKind.DriveNotStoppedAfterStop, $"drive still {Drive.Mode} {(ElapsedCore - deadline).TotalMilliseconds:0} ms after the stop bound");
            }
        }
    }

    private void OnLimitChanged(string detail)
    {
        if (detail.Contains("overtravel", StringComparison.Ordinal))
        {
            AddViolation(PlantViolationKind.LimitSwitchOvertravel, detail);
        }
        else
        {
            Record(PlantEventKind.LimitSwitch, detail);
        }
    }

    private void SetInjected(ref bool field, bool value, string name)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Record(PlantEventKind.Injected, $"{name} {(value ? "on" : "off")}");
        UpdateHatInputs();
    }

    private void AddViolation(PlantViolationKind kind, string detail)
    {
        _violations.Add(new PlantViolation(ElapsedCore, kind, detail));
        Record(PlantEventKind.Violation, $"{kind}: {detail}");
    }

    private void Record(PlantEventKind kind, string detail)
    {
        if (_history.Count >= Options.MaxHistory)
        {
            var drop = Options.MaxHistory / 2;
            _history.RemoveRange(0, drop);
            _historyDropped += drop;
        }

        _history.Add(new PlantEvent(ElapsedCore, kind, detail));
    }
}
