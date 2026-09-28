using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.Simulation;

/// <summary>
/// Roof mechanics. Positions are metres along the travel: 0 is the closed (REV) limit's operating point, positive is
/// toward open, and <see cref="TravelMeters"/> is the open (FWD) limit's operating point. None of these are documented
/// for the installation; they are round values that keep a full move at 20 s.
/// </summary>
public sealed record RoofMechanicsOptions
{
    /// <summary>Distance between the two limits' operating points.</summary>
    public double TravelMeters { get; init; } = 2.0;

    /// <summary>Roof speed when the drive runs at the base frequency (P167).</summary>
    public double SpeedAtBaseFrequency { get; init; } = 0.1;

    /// <summary>Deceleration while the motor coasts (output off).</summary>
    public double CoastDeceleration { get; init; } = 0.5;

    /// <summary>
    /// Deceleration while the DC brake is applied with a P174 voltage above 0. Assumption: one figure for any non-zero
    /// P174; the real braking torque rises with the voltage and depends on the motor.
    /// </summary>
    public double DcBrakeDeceleration { get; init; } = 2.0;

    /// <summary>
    /// Distance from each limit's operating point to the hard stop behind it. The 60 mm default puts the lever at
    /// about 89° at the hard stop, inside the ME-8108's 95° of pretravel plus overtravel.
    /// </summary>
    public double HardStopBeyondOperatePoint { get; init; } = 0.06;

    public void Validate()
    {
        if (TravelMeters <= 0 || SpeedAtBaseFrequency <= 0 || CoastDeceleration <= 0 || DcBrakeDeceleration <= 0 || HardStopBeyondOperatePoint <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TravelMeters), "Roof mechanics values must be positive.");
        }
    }
}

/// <summary>Everything that configures a <see cref="RoofPlant"/>.</summary>
public sealed record RoofPlantOptions
{
    public SmVectorSettings Drive { get; init; } = new();

    public SmVectorAssumptions DriveAssumptions { get; init; } = new();

    public RoofMechanicsOptions Mechanics { get; init; } = new();

    public Me8108Options OpenLimit { get; init; } = new();

    public Me8108Options ClosedLimit { get; init; } = new();

    public SmI010Options Hat { get; init; } = new();

    public WiringFault Wiring { get; init; }

    /// <summary>Starting position. The default rests 10 mm past the closed limit's operating point, as after a close.</summary>
    public double InitialPosition { get; init; } = -0.01;

    /// <summary>How long the drive has had mains power when the plant starts. Past the 2 s start lockout by default.</summary>
    public TimeSpan InitialDriveUptime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Fixed simulation step, at most 10 ms, and at most 1 ms while either limit switch bounces.</summary>
    public TimeSpan StepSize { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Slack in the stop invariant: after the direction coils or the RLY4 coil drop, the drive must stop driving within
    /// the relay release time, the drive's input response, any ramp time and this margin.
    /// </summary>
    public TimeSpan DriveStopMargin { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Oldest events are dropped beyond this many.</summary>
    public int MaxHistory { get; init; } = 100_000;

    public void Validate()
    {
        Drive.Validate();
        Mechanics.Validate();
        OpenLimit.Validate();
        ClosedLimit.Validate();
        if (StepSize <= TimeSpan.Zero || StepSize > TimeSpan.FromMilliseconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(StepSize), "The step must be positive and at most 10 ms.");
        }

        if (StepSize > TimeSpan.FromMilliseconds(1) && (OpenLimit.Bounces || ClosedLimit.Bounces))
        {
            throw new ArgumentOutOfRangeException(nameof(StepSize), "The ME-8108 bounce changes every millisecond: use a step of at most 1 ms, or no bounce time.");
        }

        if (MaxHistory < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHistory));
        }

        if (InitialPosition <= -Mechanics.HardStopBeyondOperatePoint || InitialPosition >= Mechanics.TravelMeters + Mechanics.HardStopBeyondOperatePoint)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialPosition), "The roof must start between the hard stops.");
        }
    }
}

public enum PlantEventKind
{
    Hat,
    Relay,
    Drive,
    LimitSwitch,
    Input,
    Roof,
    Injected,
    Violation
}

/// <summary>Something that changed in the plant.</summary>
public sealed record PlantEvent(TimeSpan At, PlantEventKind Kind, string Detail);

public enum PlantViolationKind
{
    /// <summary>RLY1 and RLY2 contacts were closed at the same time.</summary>
    BothDirectionContactsClosed,

    /// <summary>The roof reached a hard stop.</summary>
    HardStopContact,

    /// <summary>A limit switch lever went past pretravel plus overtravel.</summary>
    LimitSwitchOvertravel,

    /// <summary>The drive was still driving the motor too long after the controller removed the run command or the permit.</summary>
    DriveNotStoppedAfterStop
}

/// <summary>A broken invariant.</summary>
public sealed record PlantViolation(TimeSpan At, PlantViolationKind Kind, string Detail);
