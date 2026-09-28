namespace HVO.RoofControllerV4.Simulation.LimitSwitches;

/// <summary>
/// Moujen ME-8108 limit switch specification (Form Z, double break: NC pair 1-2 and NO pair 3-4 moved by one
/// actuator). Values are from the manufacturer's product specification.
/// </summary>
public sealed record Me8108Specification
{
    /// <summary>Lever travel from rest to the operating point.</summary>
    public double PretravelDegrees { get; init; } = 20;

    /// <summary>Lever travel between the operating point and the release point.</summary>
    public double DifferentialTravelDegrees { get; init; } = 10;

    /// <summary>Lever travel allowed beyond the operating point.</summary>
    public double OvertravelDegrees { get; init; } = 75;

    /// <summary>Slowest actuator speed the switch is rated for, m/s (0.5 mm/s).</summary>
    public double MinimumOperatingSpeed { get; init; } = 0.0005;

    /// <summary>Fastest actuator speed the switch is rated for, m/s (50 cm/s).</summary>
    public double MaximumOperatingSpeed { get; init; } = 0.5;

    /// <summary>Electrical operating frequency, operations per minute.</summary>
    public int MaximumOperationsPerMinute { get; init; } = 30;

    /// <summary>Contact rating at 115 VDC, amperes.</summary>
    public double RatedCurrentAt115Vdc { get; init; } = 0.4;

    /// <summary>Initial contact resistance, ohms (15 mΩ maximum).</summary>
    public double MaximumContactResistanceOhms { get; init; } = 0.015;
}

/// <summary>How the contacts move with the lever. The specification does not say; both are modelled.</summary>
public enum Me8108ContactAction
{
    /// <summary>
    /// Snap action: at the operating point the NC contact opens at once and the NO contact closes after
    /// <see cref="Me8108Options.TransferTime"/>, then bounces for <see cref="Me8108Options.BounceTime"/>. Release
    /// mirrors it at the release point. Default.
    /// </summary>
    Snap,

    /// <summary>
    /// Slow action: each contact follows the lever angle. The NC contact opens at the operating point and the NO
    /// contact closes <see cref="Me8108Options.SlowActionGapDegrees"/> later, with no hysteresis.
    /// </summary>
    SlowAction
}

/// <summary>Faults injected into one switch or its wiring.</summary>
[Flags]
public enum LimitSwitchFault
{
    None = 0,

    /// <summary>The mechanism is stuck in the actuated position: NC open, NO closed.</summary>
    StuckActuated = 1,

    /// <summary>The mechanism is stuck at rest: NC closed, NO open, whatever the lever does.</summary>
    StuckReleased = 2,

    /// <summary>The wire of the NC pair (the run path) is broken: the pair reads open.</summary>
    BrokenNcWire = 4,

    /// <summary>The wire of the NO pair (the monitor input) is broken: the pair reads open.</summary>
    BrokenMonitorWire = 8
}

/// <summary>Mounting and the behaviour the specification leaves open.</summary>
public sealed record Me8108Options
{
    public Me8108Specification Specification { get; init; } = new();

    /// <summary>
    /// Effective lever arm, metres: roof travel against the lever divided by the lever angle in radians. With 0.05 m the
    /// 20° pretravel is 17.5 mm of roof travel and the 75° overtravel is 65.4 mm.
    /// </summary>
    public double LeverArmMeters { get; init; } = 0.05;

    public Me8108ContactAction ContactAction { get; init; } = Me8108ContactAction.Snap;

    /// <summary>Snap action: time from the NC contact opening to the NO contact closing (break before make).</summary>
    public TimeSpan TransferTime { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Snap action: after the closing contact first touches it bounces for this long, open in each odd whole millisecond
    /// since the touch. The pattern follows elapsed time, so the plant step must be 1 ms or less to show it
    /// (<see cref="RoofPlantOptions.Validate"/>).
    /// </summary>
    public TimeSpan BounceTime { get; init; } = TimeSpan.FromMilliseconds(3);

    /// <summary>Slow action: lever travel between the NC contact opening and the NO contact closing.</summary>
    public double SlowActionGapDegrees { get; init; } = 5;

    /// <summary>The closing contact bounces: snap action with a bounce time.</summary>
    internal bool Bounces => ContactAction == Me8108ContactAction.Snap && BounceTime > TimeSpan.Zero;

    /// <summary>Roof travel, metres, that turns the lever by <paramref name="degrees"/>.</summary>
    public double TravelForDegrees(double degrees) => degrees * Math.PI / 180 * LeverArmMeters;

    public void Validate()
    {
        if (LeverArmMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LeverArmMeters));
        }

        if (TransferTime < TimeSpan.Zero || BounceTime < TimeSpan.Zero || SlowActionGapDegrees < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TransferTime), "Timings and the slow-action gap cannot be negative.");
        }

        var spec = Specification;
        if (spec.PretravelDegrees <= 0 || spec.DifferentialTravelDegrees <= 0 || spec.OvertravelDegrees <= 0
            || spec.DifferentialTravelDegrees >= spec.PretravelDegrees)
        {
            throw new ArgumentOutOfRangeException(nameof(Specification), "Pretravel, differential travel and overtravel must be positive, with differential travel below pretravel.");
        }
    }
}

/// <summary>
/// One ME-8108 switch. The lever angle comes from how far the roof's cam has pushed into the lever; the contacts
/// follow <see cref="Me8108Options.ContactAction"/>. Not thread-safe; <see cref="RoofPlant"/> serializes access.
/// </summary>
public sealed class Me8108LimitSwitch
{
    private readonly Me8108Options _options;
    private bool _actuated;
    private bool _mechanicalNcClosed;
    private bool _mechanicalNoClosed;
    private bool _transferring;
    private TimeSpan _transferRemaining;
    private bool _bouncing;
    private TimeSpan _bounceElapsed;
    private bool _bounceTargetIsNo;

    public Me8108LimitSwitch(string name, Me8108Options options, double initialAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Name = name;
        _options = options;
        AngleDegrees = Math.Max(0, initialAngleDegrees);
        _actuated = AngleDegrees >= options.Specification.PretravelDegrees;
        SettleContacts();
    }

    public string Name { get; }

    public LimitSwitchFault Fault { get; set; }

    /// <summary>Lever angle from rest, degrees.</summary>
    public double AngleDegrees { get; private set; }

    /// <summary>Largest lever angle seen, degrees.</summary>
    public double MaximumAngleDegrees { get; private set; }

    /// <summary>The lever has gone past pretravel plus overtravel at least once.</summary>
    public bool OvertravelExceeded { get; private set; }

    /// <summary>The mechanism is in the actuated position (after pretravel, until the release point).</summary>
    public bool Actuated => Fault.HasFlag(LimitSwitchFault.StuckActuated) || (!Fault.HasFlag(LimitSwitchFault.StuckReleased) && _actuated);

    /// <summary>Terminals 1-2 conduct, including the effect of a broken NC wire.</summary>
    public bool NcPathClosed => !Fault.HasFlag(LimitSwitchFault.BrokenNcWire) && ContactNcClosed;

    /// <summary>Terminals 3-4 conduct, including the effect of a broken monitor wire.</summary>
    public bool NoPathClosed => !Fault.HasFlag(LimitSwitchFault.BrokenMonitorWire) && ContactNoClosed;

    /// <summary>The NC contact itself is closed (no wiring faults).</summary>
    public bool ContactNcClosed
        => Fault.HasFlag(LimitSwitchFault.StuckActuated) ? false
            : Fault.HasFlag(LimitSwitchFault.StuckReleased) ? true
            : _mechanicalNcClosed;

    /// <summary>The NO contact itself is closed (no wiring faults).</summary>
    public bool ContactNoClosed
        => Fault.HasFlag(LimitSwitchFault.StuckActuated) ? true
            : Fault.HasFlag(LimitSwitchFault.StuckReleased) ? false
            : _mechanicalNoClosed;

    internal bool IsSettled => !_transferring && !_bouncing;

    internal event Action<string>? Changed;

    /// <summary>Moves the lever to <paramref name="angleDegrees"/> and advances the contacts by <paramref name="dt"/>.</summary>
    internal void Step(TimeSpan dt, double angleDegrees)
    {
        AngleDegrees = Math.Max(0, angleDegrees);
        MaximumAngleDegrees = Math.Max(MaximumAngleDegrees, AngleDegrees);
        var spec = _options.Specification;
        if (!OvertravelExceeded && AngleDegrees > spec.PretravelDegrees + spec.OvertravelDegrees)
        {
            OvertravelExceeded = true;
            Changed?.Invoke($"{Name} lever past the {spec.PretravelDegrees + spec.OvertravelDegrees:0}° overtravel limit ({AngleDegrees:0.0}°)");
        }

        if (_options.ContactAction == Me8108ContactAction.SlowAction)
        {
            StepSlowAction(spec);
            return;
        }

        if (!_actuated && AngleDegrees >= spec.PretravelDegrees)
        {
            _actuated = true;
            BeginTransfer(towardNo: true);
            Changed?.Invoke($"{Name} operated");
            return;
        }

        if (_actuated && AngleDegrees <= spec.PretravelDegrees - spec.DifferentialTravelDegrees)
        {
            _actuated = false;
            BeginTransfer(towardNo: false);
            Changed?.Invoke($"{Name} released");
            return;
        }

        StepSnapTransfer(dt);
    }

    /// <summary>The opening contact breaks in this step; the other makes <see cref="Me8108Options.TransferTime"/> later.</summary>
    private void BeginTransfer(bool towardNo)
    {
        SetContacts(nc: false, no: false);
        _bounceTargetIsNo = towardNo;
        _bouncing = false;
        _transferRemaining = _options.TransferTime;
        _transferring = true;
        if (_transferRemaining <= TimeSpan.Zero)
        {
            FinishTransfer(TimeSpan.Zero);
        }
    }

    /// <summary>The closing contact first touched <paramref name="sinceTouch"/> ago (within this step).</summary>
    private void FinishTransfer(TimeSpan sinceTouch)
    {
        _transferring = false;
        _transferRemaining = TimeSpan.Zero;
        _bouncing = true;
        _bounceElapsed = sinceTouch;
        StepBounce(TimeSpan.Zero);
    }

    private void StepSnapTransfer(TimeSpan dt)
    {
        if (_transferring)
        {
            _transferRemaining -= dt;
            if (_transferRemaining <= TimeSpan.Zero)
            {
                FinishTransfer(-_transferRemaining);
            }

            return;
        }

        if (_bouncing)
        {
            StepBounce(dt);
        }
    }

    /// <summary>
    /// Deterministic bounce from the time since the first touch: open in odd whole milliseconds, closed from
    /// <see cref="Me8108Options.BounceTime"/> on.
    /// </summary>
    private void StepBounce(TimeSpan dt)
    {
        _bounceElapsed += dt;
        _bouncing = _bounceElapsed < _options.BounceTime;
        CloseTarget(!_bouncing || (long)_bounceElapsed.TotalMilliseconds % 2 == 0);
    }

    private void CloseTarget(bool closed)
    {
        if (_bounceTargetIsNo)
        {
            SetContacts(nc: false, no: closed);
        }
        else
        {
            SetContacts(nc: closed, no: false);
        }
    }

    private void StepSlowAction(Me8108Specification spec)
    {
        _actuated = AngleDegrees >= spec.PretravelDegrees;
        SetContacts(nc: !_actuated, no: AngleDegrees >= spec.PretravelDegrees + _options.SlowActionGapDegrees);
    }

    private void SetContacts(bool nc, bool no)
    {
        _mechanicalNcClosed = nc;
        _mechanicalNoClosed = no;
    }

    private void SettleContacts()
    {
        if (_options.ContactAction == Me8108ContactAction.SlowAction)
        {
            StepSlowAction(_options.Specification);
        }
        else
        {
            SetContacts(nc: !_actuated, no: _actuated);
        }
    }
}
