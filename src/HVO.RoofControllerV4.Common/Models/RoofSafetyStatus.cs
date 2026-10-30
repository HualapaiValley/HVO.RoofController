namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Whether the observatory's conditions allow the roof to open: the telescopes parked and the weather safe, as the
/// controller's safety sources report them. This is not the controller's own safety supervision (limits, fault latch,
/// watchdog), which applies whatever this says.
/// </summary>
public enum RoofSafetyState
{
    /// <summary>
    /// The conditions cannot be confirmed: no source is configured, a source has not been read yet, cannot be read, or
    /// its last reading is too old. Never read as safe.
    /// </summary>
    Unknown = 0,

    /// <summary>Every configured source has a recent reading, and every one says safe (telescope parked, weather safe).</summary>
    Safe = 1,

    /// <summary>At least one source's recent reading says unsafe (a telescope not parked, unsafe weather).</summary>
    Unsafe = 2
}

/// <summary>What a safety source reports on.</summary>
public enum RoofSafetySourceKind
{
    /// <summary>A telescope mount's park state (for example ASCOM Alpaca <c>Telescope.AtPark</c>).</summary>
    TelescopePark = 0,

    /// <summary>Whether the weather is safe (for example ASCOM Alpaca <c>SafetyMonitor.IsSafe</c>).</summary>
    Weather = 1
}

/// <summary>What the controller does with the safety state when Open is requested. Close and Stop are never affected.</summary>
public enum RoofSafetyInterlockMode
{
    /// <summary>Advisory only: the state is reported and logged, and Open behaves as it does without it.</summary>
    Off = 0,

    /// <summary>
    /// Open is refused (<see cref="RoofControllerErrorCode.SafetyOverrideRequired"/>) unless the state is
    /// <see cref="RoofSafetyState.Safe"/> or the request carries an override from a caller allowed to give one. Every
    /// override is audit-logged.
    /// </summary>
    Warn = 1,

    /// <summary>Open is refused (<see cref="RoofControllerErrorCode.SafetyInterlockActive"/>) unless the state is <see cref="RoofSafetyState.Safe"/>. No override.</summary>
    Block = 2
}

/// <summary>One safety source as the controller last judged it.</summary>
public sealed record RoofSafetySourceStatus
{
    /// <summary>The source's configured name, as reasons quote it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What the source reports on.</summary>
    public RoofSafetySourceKind Kind { get; init; }

    /// <summary>
    /// What this source says now: <see cref="RoofSafetyState.Unknown"/> without a reading, or when its last reading is
    /// older than <see cref="MaxReadingAgeSeconds"/>, whatever that reading said.
    /// </summary>
    public RoofSafetyState State { get; init; }

    /// <summary>Why, in words every client shows as they are: "Telescope 'Main' is not parked".</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Extra words the source gave with its last reading (a sensor value, say), if any. Not part of the state.</summary>
    public string? Detail { get; init; }

    /// <summary>When the last reading the controller holds was observed (wall-clock UTC); null before the first.</summary>
    public DateTimeOffset? LastReadingUtc { get; init; }

    /// <summary>True when the controller holds a reading but it is older than <see cref="MaxReadingAgeSeconds"/>.</summary>
    public bool IsStale { get; init; }

    /// <summary>How old a reading may be and still count.</summary>
    public double MaxReadingAgeSeconds { get; init; }
}

/// <summary>
/// The safety state on a status snapshot (<see cref="RoofStatusResponse.Safety"/>) and from <c>GET Safety</c>: the
/// combined state, why it is not safe, each source, and how Open treats it.
/// </summary>
public sealed record RoofSafetyStatus
{
    /// <summary>The reason <see cref="Reasons"/> holds when no source is configured.</summary>
    public const string NoSourcesReason = "No safety sources configured";

    /// <summary>
    /// <see cref="RoofSafetyState.Unsafe"/> when any source is unsafe; otherwise <see cref="RoofSafetyState.Unknown"/>
    /// when there is no source or any source is unknown; otherwise <see cref="RoofSafetyState.Safe"/>.
    /// </summary>
    public RoofSafetyState State { get; init; }

    /// <summary>
    /// Why the state is not <see cref="RoofSafetyState.Safe"/>: the unsafe sources' reasons, then the unknown ones'
    /// (or <see cref="NoSourcesReason"/>). Empty when safe.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Every configured source, in the order they were configured.</summary>
    public IReadOnlyList<RoofSafetySourceStatus> Sources { get; init; } = [];

    /// <summary>When <see cref="State"/> last changed, by the controller's clock; null when it has not been judged yet.</summary>
    public DateTimeOffset? StateSinceUtc { get; init; }

    /// <summary>What Open does with <see cref="State"/>.</summary>
    public RoofSafetyInterlockMode InterlockMode { get; init; }

    /// <summary>
    /// The role an Open override needs in <see cref="RoofSafetyInterlockMode.Warn"/> (<see cref="RoofControllerApiContract.AdminRole"/>
    /// or <see cref="RoofControllerApiContract.OperatorRole"/>); null in the other modes, which take no override.
    /// </summary>
    public string? OverrideRole { get; init; }
}
