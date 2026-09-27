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

    /// <summary>P175 DC brake time, used by <see cref="SmVectorStopMethod.CoastWithDcBrake"/> and <see cref="SmVectorStopMethod.RampWithDcBrake"/>.</summary>
    public TimeSpan DcBrakeTime { get; init; } = TimeSpan.Zero;

    /// <summary>P110 start method. Only 0 (normal, with the power-up lockout) and 2 are modelled; both have the lockout.</summary>
    public int StartMethod { get; init; }

    /// <summary>P100 != 0: TB-1 is an active STOP input. The wiring doc sets P100 = 1.</summary>
    public bool StopInputEnabled { get; init; } = true;

    /// <summary>P140 relay output (TB-16/TB-17). The wiring doc sets 3 (Fault).</summary>
    public SmVectorOutputFunction RelayOutput { get; init; } = SmVectorOutputFunction.Fault;

    /// <summary>P142 TB-14 output. The wiring doc sets 1 (Run).</summary>
    public SmVectorOutputFunction Tb14Output { get; init; } = SmVectorOutputFunction.Run;

    /// <summary>P144 output inversion: 0 none, 1 inverts P140, 2 inverts P142, 3 both (SV01J p.32). The wiring doc sets 0.</summary>
    public int OutputInversion { get; init; }

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

        if (StartMethod is not (0 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(StartMethod), "Only P110 = 0 or 2 (no automatic restart) is modelled.");
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
