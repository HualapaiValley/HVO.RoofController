namespace HVO.RoofControllerV4.Simulation.Drive;

/// <summary>Why the modelled drive tripped.</summary>
public enum SmVectorTrip
{
    None = 0,

    /// <summary>F_UF: a start command within 2 s of power-up with P110 = 0 or 2 (SV01J p.27, p.46).</summary>
    StartTooSoonAfterPowerUp,

    /// <summary>F_PF: motor overload, here from a stalled motor (roof against a hard stop or jammed).</summary>
    MotorOverload,

    /// <summary>A trip injected by a test, standing for any other drive fault (for example over-current).</summary>
    External
}

/// <summary>What the modelled drive's output stage is doing.</summary>
public enum SmVectorMode
{
    /// <summary>Mains power is off. Every output is off, including TB-11.</summary>
    Unpowered,

    /// <summary>Powered, output off, no fault.</summary>
    Stopped,

    /// <summary>Output on: accelerating, at speed, or ramping through 0 Hz on a direction change.</summary>
    Running,

    /// <summary>Ramp stop in progress (P111 = 2 or 3).</summary>
    Decelerating,

    /// <summary>DC brake after a stop (P111 = 1 or 3, P175 &gt; 0).</summary>
    DcBraking,

    /// <summary>DC brake before the motor starts (P110 = 2, P175 &gt; 0), with a run input applied.</summary>
    StartDcBraking,

    /// <summary>Tripped. The output is off and the motor coasts until the fault is reset.</summary>
    Faulted
}

/// <summary>Levels at the drive's control terminals for one step. <c>true</c> means the input sees +12 V or more.</summary>
public readonly record struct SmVectorInputs(bool Tb1StopPermit, bool Tb13ARunForward, bool Tb13BRunReverse, bool Tb13CClearFault);

/// <summary>
/// Lenze AC Tech SMVector model, reduced to the terminal-strip behaviour the roof controller uses: TB-1 STOP, Run
/// Forward and Run Reverse (P121 = 13, P122 = 14), Clear Fault (P123 = 20), the relay output (P140) and the TB-14
/// output (P142) with P144 inversion, the start control source (P100), rotation (P112), the ramps (P104, P105), the
/// stop method (P111) and DC brake time (P175), the start method with its power-up lockout (P110), and a stall trip. Not thread-safe; <see cref="RoofPlant"/> serializes access.
/// </summary>
public sealed class SmVectorDrive
{
    private const double AtSpeedToleranceHz = 0.01;

    private readonly InputFilter _stopPermit = new();
    private readonly InputFilter _runForward = new();
    private readonly InputFilter _runReverse = new();
    private readonly InputFilter _clearFault = new();
    private bool _runArmed = true;
    private TimeSpan _clearFaultHeld;
    private bool _clearFaultConsumed;
    private TimeSpan _stallTime;
    private TimeSpan _dcBrakeRemaining;
    private TimeSpan _startBrakeRemaining;

    public SmVectorDrive(SmVectorSettings settings, SmVectorAssumptions assumptions, bool powered, TimeSpan uptime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assumptions);
        settings.Validate();
        Settings = settings;
        Assumptions = assumptions;
        Powered = powered;
        SincePowerUp = powered ? uptime : TimeSpan.Zero;
        Mode = powered ? SmVectorMode.Stopped : SmVectorMode.Unpowered;
    }

    public SmVectorSettings Settings { get; }

    public SmVectorAssumptions Assumptions { get; }

    /// <summary>Mains power.</summary>
    public bool Powered { get; private set; }

    /// <summary>Time since mains power was last applied.</summary>
    public TimeSpan SincePowerUp { get; private set; }

    public SmVectorMode Mode { get; private set; }

    public SmVectorTrip Trip { get; private set; }

    /// <summary>Output frequency, Hz, never negative. The direction is <see cref="OutputDirection"/>.</summary>
    public double OutputFrequencyHz { get; private set; }

    /// <summary>+1 forward, -1 reverse, 0 when the output is off.</summary>
    public int OutputDirection { get; private set; }

    /// <summary>The frequency the drive runs at (the speed reference). The model uses P103.</summary>
    public double ReferenceFrequencyHz => Settings.MaxFrequencyHz;

    /// <summary>TB-11 supplies +12 V only while the drive has mains power.</summary>
    public bool Tb11Energized => Powered;

    /// <summary>The TB-16/TB-17 relay contact is closed.</summary>
    public bool RelayOutputClosed => OutputState(Settings.RelayOutput, (Settings.OutputInversion & 1) != 0);

    /// <summary>The TB-14 NPN output is sinking current.</summary>
    public bool Tb14Sinking => OutputState(Settings.Tb14Output, (Settings.OutputInversion & 2) != 0);

    /// <summary>The motor is being driven: the output is on and not in DC brake.</summary>
    public bool IsDriving => Mode is SmVectorMode.Running or SmVectorMode.Decelerating;

    /// <summary>The DC brake is applied, after a stop or before a start.</summary>
    public bool IsDcBraking => Mode is SmVectorMode.DcBraking or SmVectorMode.StartDcBraking;

    /// <summary>
    /// True when nothing will change unless an input, power or time-dependent trip changes. A held Clear Fault that has
    /// not yet been held for <see cref="SmVectorAssumptions.MinimumClearFaultPulse"/> is not settled even with no trip:
    /// the closure is consumed once it has been held that long, so a later trip stays latched.
    /// </summary>
    internal bool IsSettled
        => (Mode is SmVectorMode.Unpowered or SmVectorMode.Stopped or SmVectorMode.Faulted
            || (Mode == SmVectorMode.DcBraking && _dcBrakeRemaining == Timeout.InfiniteTimeSpan))
           && _stopPermit.IsSettled && _runForward.IsSettled && _runReverse.IsSettled && _clearFault.IsSettled
           && _stallTime == TimeSpan.Zero
           && !(_clearFault.Value && !_clearFaultConsumed);

    internal event Action<string>? Changed;

    /// <summary>Applies or removes mains power. Removing power clears any fault and every input.</summary>
    public void SetPower(bool powered)
    {
        if (powered == Powered)
        {
            return;
        }

        Powered = powered;
        SincePowerUp = TimeSpan.Zero;
        Trip = SmVectorTrip.None;
        OutputFrequencyHz = 0;
        OutputDirection = 0;
        _dcBrakeRemaining = TimeSpan.Zero;
        _startBrakeRemaining = TimeSpan.Zero;
        _stallTime = TimeSpan.Zero;
        _clearFaultHeld = TimeSpan.Zero;
        _clearFaultConsumed = false;
        _runArmed = true;
        _stopPermit.Reset();
        _runForward.Reset();
        _runReverse.Reset();
        _clearFault.Reset();
        SetMode(powered ? SmVectorMode.Stopped : SmVectorMode.Unpowered, powered ? "mains power applied" : "mains power removed");
    }

    /// <summary>Trips the drive as if an internal fault occurred.</summary>
    public void InjectTrip(SmVectorTrip trip = SmVectorTrip.External)
    {
        if (trip == SmVectorTrip.None)
        {
            throw new ArgumentOutOfRangeException(nameof(trip));
        }

        if (Powered)
        {
            TripNow(trip);
        }
    }

    /// <summary>Advances time while the drive is settled (see <see cref="IsSettled"/>): only the power-up clock moves.</summary>
    internal void AdvanceIdle(TimeSpan dt)
    {
        if (Powered)
        {
            SincePowerUp += dt;
        }
    }

    /// <summary>Advances the drive by <paramref name="dt"/>.</summary>
    /// <param name="dt">Step length.</param>
    /// <param name="raw">Terminal levels during the step.</param>
    /// <param name="motorStalled">The load cannot turn (hard stop or jam) while the output is on.</param>
    internal void Step(TimeSpan dt, SmVectorInputs raw, bool motorStalled)
    {
        if (!Powered)
        {
            return;
        }

        SincePowerUp += dt;
        var response = Assumptions.InputResponseTime;
        var stopPermit = _stopPermit.Update(raw.Tb1StopPermit, dt, response);
        var runForward = _runForward.Update(raw.Tb13ARunForward, dt, response);
        var runReverse = _runReverse.Update(raw.Tb13BRunReverse, dt, response);
        var clearFault = _clearFault.Update(raw.Tb13CClearFault, dt, response);

        StepClearFault(dt, clearFault);

        // Both run inputs stop the drive (SV01J p.30); with P112 = 0 Run Reverse alone does nothing, and outside terminal
        // strip control (P100) neither input is valid.
        var stopActive = Settings.StopInputActive && !stopPermit;
        var requested = !Settings.TerminalRunInputsActive || runForward == runReverse ? 0
            : runForward ? 1
            : Settings.ReverseEnabled ? -1
            : 0;

        if (stopActive && !Assumptions.RestartWhenStopReleasedWithRunHeld)
        {
            _runArmed = false;
        }

        if (requested == 0)
        {
            _runArmed = true;
        }

        if (Trip != SmVectorTrip.None)
        {
            return;
        }

        var start = requested != 0 && !stopActive && _runArmed;
        if (start && Mode is not SmVectorMode.Running && SincePowerUp < SmVectorSettings.PowerUpStartLockout)
        {
            TripNow(SmVectorTrip.StartTooSoonAfterPowerUp);
            return;
        }

        if (start)
        {
            StartStep(dt, requested);
        }
        else
        {
            StopStep(dt, runForward && runReverse ? "both run inputs active" : stopActive ? "TB-1 STOP" : "run input removed");
        }

        StepStall(dt, motorStalled);
    }

    private void StepClearFault(TimeSpan dt, bool clearFault)
    {
        if (!clearFault)
        {
            _clearFaultHeld = TimeSpan.Zero;
            _clearFaultConsumed = false;
            return;
        }

        _clearFaultHeld += dt;
        if (_clearFaultConsumed || _clearFaultHeld < Assumptions.MinimumClearFaultPulse)
        {
            return;
        }

        // Clear Fault acts once per closure: a fault that occurs while the input is held stays latched.
        _clearFaultConsumed = true;
        if (Trip != SmVectorTrip.None)
        {
            Trip = SmVectorTrip.None;
            _runArmed = Assumptions.RestartAfterResetWithRunHeld;
            SetMode(SmVectorMode.Stopped, "fault reset by Clear Fault");
        }
    }

    /// <summary>
    /// A start with the output off first applies the DC brake before start (P110 = 2), then runs. A start while the
    /// output is on (running or ramping down) runs at once.
    /// </summary>
    private void StartStep(TimeSpan dt, int requested)
    {
        if (Mode is SmVectorMode.Stopped or SmVectorMode.DcBraking && Settings.StartDcBrakeTime > TimeSpan.Zero)
        {
            _dcBrakeRemaining = TimeSpan.Zero;
            _startBrakeRemaining = Settings.StartDcBrakeTime;
            SetMode(SmVectorMode.StartDcBraking, "DC brake before start");
            return;
        }

        if (Mode == SmVectorMode.StartDcBraking)
        {
            _startBrakeRemaining -= dt;
            if (_startBrakeRemaining > TimeSpan.Zero)
            {
                return;
            }

            _startBrakeRemaining = TimeSpan.Zero;
        }

        Run(dt, requested);
    }

    private void Run(TimeSpan dt, int requested)
    {
        _dcBrakeRemaining = TimeSpan.Zero;
        var seconds = dt.TotalSeconds;
        if (OutputDirection != 0 && OutputDirection != requested && OutputFrequencyHz > 0)
        {
            // A direction change ramps through 0 Hz at the deceleration rate.
            OutputFrequencyHz = Math.Max(0, OutputFrequencyHz - Settings.DecelerationRateHzPerSecond * seconds);
        }
        else
        {
            OutputDirection = requested;
            OutputFrequencyHz = Math.Min(ReferenceFrequencyHz, OutputFrequencyHz + Settings.AccelerationRateHzPerSecond * seconds);
        }

        if (OutputFrequencyHz == 0)
        {
            OutputDirection = requested;
        }

        SetMode(SmVectorMode.Running, requested > 0 ? "run forward" : "run reverse");
    }

    private void StopStep(TimeSpan dt, string reason)
    {
        switch (Mode)
        {
            case SmVectorMode.Running:
                if (Settings.StopMethod is SmVectorStopMethod.Ramp or SmVectorStopMethod.RampWithDcBrake)
                {
                    SetMode(SmVectorMode.Decelerating, "ramp stop: " + reason);
                    Decelerate(dt);
                }
                else
                {
                    OutputOff();
                    BeginDcBrakeOrStop(Settings.StopMethod == SmVectorStopMethod.CoastWithDcBrake, "coast stop: " + reason);
                }

                break;
            case SmVectorMode.Decelerating:
                Decelerate(dt);
                break;
            case SmVectorMode.DcBraking:
                if (_dcBrakeRemaining == Timeout.InfiniteTimeSpan)
                {
                    // P175 = 999.9: until a run or a fault.
                    break;
                }

                _dcBrakeRemaining -= dt;
                if (_dcBrakeRemaining <= TimeSpan.Zero)
                {
                    _dcBrakeRemaining = TimeSpan.Zero;
                    SetMode(SmVectorMode.Stopped, "DC brake finished");
                }

                break;
            case SmVectorMode.StartDcBraking:
                // The motor never started, so the stop method does not apply.
                _startBrakeRemaining = TimeSpan.Zero;
                SetMode(SmVectorMode.Stopped, reason + " during the DC brake before start");
                break;
        }
    }

    private void Decelerate(TimeSpan dt)
    {
        OutputFrequencyHz = Math.Max(0, OutputFrequencyHz - Settings.DecelerationRateHzPerSecond * dt.TotalSeconds);
        if (OutputFrequencyHz == 0)
        {
            OutputOff();
            BeginDcBrakeOrStop(Settings.StopMethod == SmVectorStopMethod.RampWithDcBrake, "ramp reached 0 Hz");
        }
    }

    private void BeginDcBrakeOrStop(bool dcBrake, string reason)
    {
        if (dcBrake && Settings.StopDcBrakeTime != TimeSpan.Zero)
        {
            _dcBrakeRemaining = Settings.StopDcBrakeTime;
            SetMode(SmVectorMode.DcBraking, reason + ", DC brake");
        }
        else
        {
            SetMode(SmVectorMode.Stopped, reason);
        }
    }

    private void StepStall(TimeSpan dt, bool motorStalled)
    {
        if (IsDriving && OutputFrequencyHz > 0 && motorStalled)
        {
            _stallTime += dt;
            if (_stallTime >= Assumptions.StallTripTime)
            {
                TripNow(SmVectorTrip.MotorOverload);
            }
        }
        else
        {
            _stallTime = TimeSpan.Zero;
        }
    }

    private void TripNow(SmVectorTrip trip)
    {
        OutputOff();
        _dcBrakeRemaining = TimeSpan.Zero;
        _startBrakeRemaining = TimeSpan.Zero;
        _stallTime = TimeSpan.Zero;
        _runArmed = false;
        Trip = trip;
        SetMode(SmVectorMode.Faulted, "trip " + trip);
    }

    private void OutputOff()
    {
        OutputFrequencyHz = 0;
        OutputDirection = 0;
    }

    private bool OutputState(SmVectorOutputFunction function, bool inverted)
    {
        if (!Powered)
        {
            // Without power the relay and the TB-14 transistor cannot be energized, inverted or not.
            return false;
        }

        var active = function switch
        {
            SmVectorOutputFunction.Run => Mode == SmVectorMode.Running
                || (Mode == SmVectorMode.Decelerating && Assumptions.RunOutputDuringDeceleration)
                || (IsDcBraking && Assumptions.RunOutputDuringDcBrake),
            SmVectorOutputFunction.Fault => Trip == SmVectorTrip.None,
            SmVectorOutputFunction.InverseFault => Trip != SmVectorTrip.None,
            SmVectorOutputFunction.AtSpeed => Mode == SmVectorMode.Running
                && OutputFrequencyHz >= ReferenceFrequencyHz - AtSpeedToleranceHz,
            _ => false
        };
        return active != inverted;
    }

    private void SetMode(SmVectorMode mode, string reason)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Changed?.Invoke($"drive {mode}: {reason}");
    }

    /// <summary>An input that changes only after the raw level has been stable for the response time.</summary>
    private sealed class InputFilter
    {
        private bool _pending;
        private TimeSpan _stableFor;

        public bool Value { get; private set; }

        public bool IsSettled => _pending == Value;

        public bool Update(bool raw, TimeSpan dt, TimeSpan response)
        {
            if (raw != _pending)
            {
                _pending = raw;
                _stableFor = TimeSpan.Zero;
            }

            if (_pending != Value)
            {
                _stableFor += dt;
                if (_stableFor >= response)
                {
                    Value = _pending;
                }
            }

            return Value;
        }

        public void Reset()
        {
            Value = false;
            _pending = false;
            _stableFor = TimeSpan.Zero;
        }
    }
}
