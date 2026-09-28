namespace HVO.RoofControllerV4.Simulation.Drive;

/// <summary>Stop method, SMVector parameter P111 (SV01J p.27).</summary>
public enum SmVectorStopMethod
{
    /// <summary>0: the output shuts off immediately and the motor coasts (factory default).</summary>
    Coast = 0,

    /// <summary>1: the output shuts off, then the DC brake applies (P174, P175).</summary>
    CoastWithDcBrake = 1,

    /// <summary>2: the drive ramps to a stop per P105.</summary>
    Ramp = 2,

    /// <summary>3: the drive ramps to 0 Hz, then the DC brake applies.</summary>
    RampWithDcBrake = 3
}

/// <summary>Function of the relay output (P140) or the TB-14 output (P142) (SV01J p.31-32). Only the modelled subset.</summary>
public enum SmVectorOutputFunction
{
    /// <summary>0: none (never energized).</summary>
    None = 0,

    /// <summary>1: energized while the drive is running.</summary>
    Run = 1,

    /// <summary>3: energized while the drive is healthy; de-energizes on a trip or when power is removed.</summary>
    Fault = 3,

    /// <summary>4: energized on a trip.</summary>
    InverseFault = 4,

    /// <summary>6: energized while the output frequency equals the commanded frequency.</summary>
    AtSpeed = 6
}

/// <summary>
/// SMVector parameters the model uses, with the values the wiring doc sets (section 11). Where the wiring doc leaves a
/// value to the installer (P104, P105, P111), the default is the value noted on the property and tests vary it.
/// </summary>
public sealed record SmVectorSettings
{
    /// <summary>
    /// Mains-power-up start lockout: with P110 = 0 or 2, a start command applied within 2 s of power-up trips F.UF
    /// (SV01J p.27, "Start command must be applied at least 2 seconds after power-up").
    /// </summary>
    public static readonly TimeSpan PowerUpStartLockout = TimeSpan.FromSeconds(2);

    /// <summary>P175 = 999.9: a continuous DC brake after a stop, and a 15 s brake before a start (SV01J p.27, p.34).</summary>
    public static readonly TimeSpan ContinuousDcBrake = TimeSpan.FromMilliseconds(999_900);

    /// <summary>The brake before a start with P110 = 2 when P175 = 999.9.</summary>
    public static readonly TimeSpan ContinuousStartDcBrake = TimeSpan.FromSeconds(15);

    /// <summary>P103 maximum frequency, Hz. Factory default 60.</summary>
    public double MaxFrequencyHz { get; init; } = 60;

    /// <summary>P167 base frequency, Hz. P104 and P105 are measured from 0 Hz to this frequency.</summary>
    public double BaseFrequencyHz { get; init; } = 60;

    /// <summary>
    /// P104 acceleration time, 0 Hz to P167. Factory default 20 s; the installed value is not documented, so the model
    /// uses 2 s and tests vary it.
    /// </summary>
    public TimeSpan AccelerationTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>P105 deceleration time, P167 to 0 Hz. Used by the ramp stop methods. Installed value not documented.</summary>
    public TimeSpan DecelerationTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>P111 stop method. The wiring doc says "select and test"; the factory default is coast.</summary>
    public SmVectorStopMethod StopMethod { get; init; } = SmVectorStopMethod.Coast;

    /// <summary>
    /// P175 DC brake time, 0-999.9 s (SV01J p.34): after a stop with <see cref="SmVectorStopMethod.CoastWithDcBrake"/> or
    /// <see cref="SmVectorStopMethod.RampWithDcBrake"/>, and before a start with P110 = 2. <see cref="ContinuousDcBrake"/>
    /// (999.9 s) brakes after a stop until a run or a fault, and for 15 s before a start.
    /// </summary>
    public TimeSpan DcBrakeTime { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// P174 DC brake voltage, 0.0-30.0 % of the nominal DC bus voltage (SV01J p.34). Factory default 0.0, which applies
    /// no braking current. The installed value is not documented. The drive still goes through the P175 brake period
    /// with 0.0 (see <see cref="SmVectorAssumptions"/>); the plant brakes the roof only when this is above 0.
    /// </summary>
    public double DcBrakeVoltagePercent { get; init; }

    /// <summary>
    /// P110 start method. Only 0 (normal) and 2 (DC brake for P175 before the motor starts, SV01J p.27) are modelled;
    /// both have the power-up start lockout.
    /// </summary>
    public int StartMethod { get; init; }

    /// <summary>
    /// P100 start control source (SV01J p.25, p.30). The wiring doc sets 1 (terminal strip); the factory default is 0
    /// (local keypad). The manual lists 0-6, but 6 applies only to 15 HP and larger drives and the installed drive is
    /// 0.33-10 HP, so the model accepts 0-5. TB-1 is an active STOP input for any value but 0, and the run inputs
    /// (P121-P124 = 10-14) are valid only in terminal strip mode (1, 4 or 5). With 4 or 5 a TB-13 input set to 8 (Control
    /// Select) would switch to a keypad; none is. The model has no keypad or network, so with 0, 2 or 3 the drive never
    /// starts.
    /// </summary>
    public int StartControlSource { get; init; } = 1;

    /// <summary>
    /// P112 rotation: true is 1 (forward and reverse), which the wiring doc sets; false is the factory default 0
    /// (forward only), with which Run Reverse (P122 = 14) does not function (SV01J p.27, p.30). Assumption: the drive
    /// ignores Run Reverse; a drive that ran forward instead behaves like swapped motor leads, which the plant covers.
    /// </summary>
    public bool ReverseEnabled { get; init; } = true;

    /// <summary>P140 relay output (TB-16/TB-17). The wiring doc sets 3 (Fault).</summary>
    public SmVectorOutputFunction RelayOutput { get; init; } = SmVectorOutputFunction.Fault;

    /// <summary>P142 TB-14 output. The wiring doc sets 1 (Run).</summary>
    public SmVectorOutputFunction Tb14Output { get; init; } = SmVectorOutputFunction.Run;

    /// <summary>P144 output inversion: 0 none, 1 inverts P140, 2 inverts P142, 3 both (SV01J p.32). The wiring doc sets 0.</summary>
    public int OutputInversion { get; init; }

    /// <summary>TB-1 is an active STOP input (P100 != 0).</summary>
    internal bool StopInputActive => StartControlSource != 0;

    /// <summary>The run inputs are valid: terminal strip start control (P100 = 1, 4 or 5).</summary>
    internal bool TerminalRunInputsActive => StartControlSource is 1 or 4 or 5;

    /// <summary>The DC brake after a stop with P111 = 1 or 3; <see cref="Timeout.InfiniteTimeSpan"/> when continuous.</summary>
    internal TimeSpan StopDcBrakeTime => DcBrakeTime == ContinuousDcBrake ? Timeout.InfiniteTimeSpan : DcBrakeTime;

    /// <summary>The DC brake before a start: P175 with P110 = 2 (15 s for 999.9), otherwise none.</summary>
    internal TimeSpan StartDcBrakeTime
        => StartMethod != 2 ? TimeSpan.Zero : DcBrakeTime == ContinuousDcBrake ? ContinuousStartDcBrake : DcBrakeTime;

    internal double AccelerationRateHzPerSecond => Rate(AccelerationTime);

    internal double DecelerationRateHzPerSecond => Rate(DecelerationTime);

    private double Rate(TimeSpan time) => time <= TimeSpan.Zero ? double.PositiveInfinity : BaseFrequencyHz / time.TotalSeconds;

    /// <summary>Throws when a value is outside the range the manual allows or the model supports.</summary>
    public void Validate()
    {
        if (MaxFrequencyHz <= 0 || BaseFrequencyHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrequencyHz), "P103 and P167 must be positive.");
        }

        if (AccelerationTime < TimeSpan.Zero || AccelerationTime > TimeSpan.FromSeconds(3600)
            || DecelerationTime < TimeSpan.Zero || DecelerationTime > TimeSpan.FromSeconds(3600))
        {
            throw new ArgumentOutOfRangeException(nameof(AccelerationTime), "P104 and P105 are 0-3600 s.");
        }

        if (DcBrakeTime < TimeSpan.Zero || DcBrakeTime > ContinuousDcBrake)
        {
            throw new ArgumentOutOfRangeException(nameof(DcBrakeTime), "P175 is 0-999.9 s.");
        }

        if (DcBrakeVoltagePercent is < 0 or > 30 || double.IsNaN(DcBrakeVoltagePercent))
        {
            throw new ArgumentOutOfRangeException(nameof(DcBrakeVoltagePercent), "P174 is 0.0-30.0 %.");
        }

        if (StartMethod is not (0 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(StartMethod), "Only P110 = 0 or 2 (no automatic restart) is modelled.");
        }

        if (StartControlSource is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartControlSource), "P100 is 0-5; 6 is only for 15 HP and larger drives (SV01J p.25).");
        }

        if (OutputInversion is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputInversion), "P144 is 0-3.");
        }
    }
}

/// <summary>
/// SMVector behaviour the manual does not state. Each default is the variant that is harder for the controller, unless
/// noted, and tests run both ways.
/// </summary>
/// <remarks>
/// The model also fixes these readings of the manual, which have no switch here; each is covered by a drive test:
/// <list type="bullet">
/// <item>P110 = 2 brakes only when the output is off: a run input that arrives while the drive is running or ramping
/// down (a reversal) ramps through 0 Hz without a brake.</item>
/// <item>A run input during the brake after a stop (P111 = 1 or 3) with P110 = 2 ends that brake and starts the full
/// P175 brake before the start.</item>
/// <item>With P112 = 0 the drive ignores Run Reverse (<see cref="SmVectorSettings.ReverseEnabled"/>).</item>
/// <item>With P174 = 0.0 the drive still goes through the P175 brake period, including the Run output during it, but
/// applies no braking current (<see cref="SmVectorSettings.DcBrakeVoltagePercent"/>).</item>
/// </list>
/// </remarks>
public sealed record SmVectorAssumptions
{
    /// <summary>
    /// When TB-1 (STOP) opens while a run input is held and then closes again, the drive restarts without the run input
    /// being removed. Run Forward/Run Reverse (P121 = 13, P122 = 14) are maintained inputs; the manual does not say
    /// whether a STOP must be followed by a new run edge. Default true (the drive restarts).
    /// </summary>
    public bool RestartWhenStopReleasedWithRunHeld { get; init; } = true;

    /// <summary>
    /// TB-14 (P142 = Run) stays energized during a ramp stop until 0 Hz. The manual defines Run only as "drive is
    /// running". Default true.
    /// </summary>
    public bool RunOutputDuringDeceleration { get; init; } = true;

    /// <summary>
    /// TB-14 (P142 = Run) stays energized while the DC brake is applied: after a stop (P111 = 1 or 3) and before a start
    /// (P110 = 2). The manual defines Run only as "energizes when the drive is running". Default true: after a stop with
    /// a DC brake the controller still sees IN4 high, which its start interlock and drive-stop check must handle. With
    /// false, a brake before a start longer than the controller's at-speed window stops the roof.
    /// </summary>
    public bool RunOutputDuringDcBrake { get; init; } = true;

    /// <summary>Clear Fault (P123 = 20, "close to reset fault") must be held at least this long to reset.</summary>
    public TimeSpan MinimumClearFaultPulse { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// After a fault reset, a run input that is still held restarts the drive. Default false (the run input must be
    /// removed and applied again); the controller never holds a run input during Clear Fault, and tests cover both.
    /// </summary>
    public bool RestartAfterResetWithRunHeld { get; init; }

    /// <summary>
    /// How long the drive tolerates a stalled motor (roof against a hard stop or jammed) before it trips F.PF (motor
    /// overload). It depends on P108 and P171 and the motor; the manual gives no single time.
    /// </summary>
    public TimeSpan StallTripTime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Delay between a run input changing and the drive acting on it (input scan). Not stated.</summary>
    public TimeSpan InputResponseTime { get; init; } = TimeSpan.FromMilliseconds(4);
}
