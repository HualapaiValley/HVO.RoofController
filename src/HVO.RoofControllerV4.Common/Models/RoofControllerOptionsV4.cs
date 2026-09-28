namespace HVO.RoofControllerV4.Common.Models;

public record class RoofControllerOptionsV4
{
    // DEFAULT RELAY MAPPING (aligned with HARDWARE_OVERVIEW.md section 6)
    // 1 = Open (Forward)  RLY1 -> RunFwd (TB-13A)
    // 2 = Close (Reverse) RLY2 -> RunRev (TB-13B)
    // 3 = ClearFault pulse RLY3 -> ClearFault (TB-13C)
    // 4 = Stop/Enable     RLY4 -> Fail-safe stop/enable path
    // If boards are re-wired, override these in configuration.
    /// <summary>
    /// Maximum time the roof can run continuously in either direction before the safety watchdog stops it.
    /// This prevents runaway operations that could damage the roof or motors.
    /// </summary>
    public TimeSpan SafetyWatchdogTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public int OpenRelayId { get; set; } = 1; // FWD
    public int CloseRelayId { get; set; } = 2; // REV
    /// <summary>
    /// Relay index used to pulse the Clear-Fault function.
    /// Prefer <see cref="ClearFaultRelayId"/>; <see cref="ClearFault"/> retained for backward compatibility.
    /// </summary>
    public int ClearFaultRelayId { get; set; } = 3;

    /// <summary>
    /// Backward compatible alias (deprecated). Use <see cref="ClearFaultRelayId"/> instead.
    /// </summary>
    [Obsolete("Use ClearFaultRelayId instead.")]
    public int ClearFault
    {
        get => ClearFaultRelayId;
        set => ClearFaultRelayId = value;
    }
    public int StopRelayId { get; set; } = 4;

    /// <summary>
    /// Enables background polling of the 4 digital inputs on the FourRelayFourInput HAT.
    /// When enabled, input edge-change events will be raised.
    /// </summary>
    public bool EnableDigitalInputPolling { get; set; } = true;

    /// <summary>
    /// Interval between input polls. Keep small for responsive edge notifications.
    /// </summary>
    public TimeSpan DigitalInputPollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Enables periodic hardware verification while the roof is moving. When enabled, the service will perform
    /// scheduled direct input reads at <see cref="PeriodicVerificationInterval"/> to detect missed edge events.
    /// </summary>
    public bool EnablePeriodicVerificationWhileMoving { get; set; } = true;

    /// <summary>
    /// Interval for periodic verification reads while moving. Keep coarse enough to avoid excess I2C traffic.
    /// </summary>
    public TimeSpan PeriodicVerificationInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When true (default), limit switches are treated as Normally Closed (NC) so that a RAW LOW electrical level
    /// indicates the limit switch has been actuated (circuit opened). When false, switches are treated as Normally Open (NO)
    /// and a RAW HIGH level indicates the limit is reached. All public surface area (status snapshots, APIs, LEDs) exposes
    /// a logical view where <c>true</c> means "limit reached" regardless of polarity. This option only affects how raw
    /// inputs are translated into that logical view.
    /// </summary>
    public bool UseNormallyClosedLimitSwitches { get; set; } = true;

    /// <summary>
    /// Departure-release confirmation window. When motion starts on a limit (for example opening from Closed), that
    /// limit's release must be observed continuously for at least this long before a later reassertion is treated as
    /// <see cref="RoofControllerStopReason.StartLimitReasserted"/>; reassertion inside the window is treated as switch
    /// chatter and restarts the window. It never delays a stop: the destination limit, contradictory limits and the drive
    /// fault input act immediately. Set to <see cref="TimeSpan.Zero"/> to verify the release on the first reading.
    /// </summary>
    public TimeSpan LimitSwitchDebounce { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// When true, limit switch inputs are ignored and treated as inactive. This is useful for development environments
    /// where the physical limit circuits are not present or are intentionally disconnected. Never enable in production.
    /// </summary>
    public bool IgnorePhysicalLimitSwitches { get; set; }

    /// <summary>
    /// Local-configuration-only safeguard. <see cref="IgnorePhysicalLimitSwitches"/> is refused on physical hardware
    /// unless this is true. The remote configuration API never changes this value.
    /// </summary>
    public bool AllowIgnoringLimitSwitchesOnPhysicalHardware { get; set; }

    /// <summary>
    /// Electrical polarity of the drive fault input (IN3). True (default, current wiring) means a RAW HIGH level is a fault.
    /// Set false for fail-safe wiring where the drive holds IN3 HIGH while healthy. Confirm during commissioning.
    /// </summary>
    public bool FaultInputActiveHigh { get; set; } = true;

    /// <summary>
    /// Number of consecutive failed safety-input reads tolerated while moving before a safety stop.
    /// Startup and motion commands never tolerate a failed read. Must be validated on the bench.
    /// </summary>
    public int MaxConsecutiveInputReadFailures { get; set; } = 3;

    /// <summary>
    /// Optional renewable operator lease. When set, motion stops unless a client renews the lease within this window.
    /// The absolute movement cap (<see cref="SafetyWatchdogTimeout"/>) applies regardless. Null (default) disables the lease.
    /// </summary>
    public TimeSpan? OperatorLeaseTimeout { get; set; }

    /// <summary>
    /// Optional drive run interlock on IN4 (the drive's run output: SMVector TB-14 with P142 = 1). When set:
    /// <list type="bullet">
    /// <item>a start is refused with <see cref="RoofControllerErrorCode.InterlockActive"/> while IN4 already reports the
    /// drive running, so confirmation is always a fresh LOW to HIGH transition (after a ramp stop an immediate reversal
    /// is refused until the drive has stopped; with the coast stop IN4 drops within milliseconds and the reversal
    /// proceeds while the roof coasts);</item>
    /// <item>motion stops and latches <see cref="RoofControllerStopReason.DriveNotRunning"/> when IN4 does not report
    /// running within this window of the motion command;</item>
    /// <item>after IN4 has confirmed, motion stops and latches <see cref="RoofControllerStopReason.DriveNotRunning"/> when
    /// IN4 drops and stays low without the destination limit (for example an external stop opened TB-1).</item>
    /// </list>
    /// Null (default) disables the interlock. Production sets 3 s: with P142 = 1 (Run) IN4 rises about 0.1 s after the
    /// command in the emulated plant. With P142 = 6 (At Speed) it must exceed P104, and with P110 = 2 it must exceed P175
    /// unless the run output stays on during the DC brake (commissioning C6).
    /// </summary>
    public TimeSpan? AtSpeedConfirmationTimeout { get; set; }

    /// <summary>
    /// Optional window for the drive to stop reporting running (IN4) after the relays are released. A drive that still
    /// reports running after it is logged as Critical once per stop; the relays are already off, so only the independent
    /// hardware stop can act on it. With a ramped stop (SMVector P111 = 2 or 3) the run output stays on while the drive
    /// decelerates, so set this longer than the P105 deceleration time, plus P175 with a DC brake (P111 = 1 or 3), since
    /// the run output may stay on while braking. Null (default) uses
    /// <see cref="AtSpeedConfirmationTimeout"/>; with both null the check is off. Local configuration only: the remote
    /// configuration API reports it but never changes it.
    /// </summary>
    public TimeSpan? DriveStopConfirmationTimeout { get; set; }

    /// <summary>
    /// Optional departure-release timeout. When motion starts on a limit, that limit must release and stay released for
    /// <see cref="LimitSwitchDebounce"/> within this window, otherwise motion stops and latches
    /// <see cref="RoofControllerStopReason.DepartureLimitNotReleased"/>: the roof is jammed, stalled or moving the wrong
    /// way (for example swapped motor leads pushing it into the stop behind the limit). Null (default) disables it. It
    /// must be at least <see cref="LimitSwitchDebounce"/> plus 0.5 s. Set it from the release time with the installed
    /// acceleration (P104) plus the debounce. Local configuration only: the remote configuration API reports it but
    /// never changes it.
    /// </summary>
    public TimeSpan? DepartureReleaseTimeout { get; set; }
}
