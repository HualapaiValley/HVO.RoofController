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
    /// Optional debounce window applied to limit switch edge events. Mechanical switches tend to chatter when toggling,
    /// so this window suppresses rapid consecutive transitions. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan LimitSwitchDebounce { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// When true, limit switch inputs are ignored and treated as inactive. This is useful for development environments
    /// where the physical limit circuits are not present or are intentionally disconnected. Never enable in production.
    /// </summary>
    public bool IgnorePhysicalLimitSwitches { get; set; }
}
