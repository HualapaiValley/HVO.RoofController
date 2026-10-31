namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Whether the observatory's conditions allow the roof to open: every telescope that can block the roof clear of it, and
/// the weather safe, as the controller's safety sources report them. This is not the controller's own safety supervision
/// (limits, fault latch, watchdog), which applies whatever this says.
/// </summary>
public enum RoofSafetyState
{
    /// <summary>
    /// The conditions cannot be confirmed: no source is configured, no required weather source is, a source that counts
    /// has not been read yet, cannot be read, or its last reading is too old. Never read as safe.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// At least one required weather source is configured, and every source that counts has a recent reading that says
    /// safe (the weather safe, each telescope that can block the roof clear of it).
    /// </summary>
    Safe = 1,

    /// <summary>At least one counting source's recent reading says unsafe (a telescope not clear of the roof, unsafe weather).</summary>
    Unsafe = 2
}

/// <summary>What a safety source reports on.</summary>
public enum RoofSafetySourceKind
{
    /// <summary>
    /// A telescope mount's park state (ASCOM Alpaca <c>Telescope.AtPark</c>), which says whether it is clear of the roof:
    /// parked is the only proof that it is, for now.
    /// </summary>
    TelescopePark = 0,

    /// <summary>Whether the weather is safe (ASCOM Alpaca <c>SafetyMonitor.IsSafe</c>).</summary>
    Weather = 1
}

/// <summary>What the controller does with the safety state when Open is requested. Close and Stop are never affected.</summary>
public enum RoofSafetyInterlockMode
{
    /// <summary>Advisory only: the state is reported and logged, and Open behaves as it does without it.</summary>
    Off = 0,

    /// <summary>
    /// Open is refused (<see cref="RoofControllerErrorCode.SafetyOverrideRequired"/>) unless the state is
    /// <see cref="RoofSafetyState.Safe"/> or the request carries an override, which anyone who may open can give (unless
    /// the controller narrows it to admins). Every override is audit-logged. The owner's chosen policy (#207).
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

    /// <summary>
    /// True when this source decides the conditions: a weather source set <c>Required</c>, or a telescope that can block
    /// the roof. False for a weather source set <c>Ignored</c> or a telescope that cannot block the roof, which is shown
    /// with its state and reason but never makes the conditions Safe, Unsafe or Unknown. True from a controller that does
    /// not say, as every source counted before the setting existed.
    /// </summary>
    public bool Counts { get; init; } = true;
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
    /// The reason <see cref="Reasons"/> and <see cref="WeatherReasons"/> hold when no weather source is set
    /// <c>Required</c>: the roof may open only while at least one recent weather reading says safe.
    /// </summary>
    public const string NoRequiredWeatherSourceReason = "No required weather source configured";

    /// <summary>
    /// <see cref="RoofSafetyState.Unsafe"/> when any source that counts (<see cref="RoofSafetySourceStatus.Counts"/>) is
    /// unsafe; otherwise <see cref="RoofSafetyState.Unknown"/> when there is no source, no required weather source, or
    /// any counting source is unknown; otherwise <see cref="RoofSafetyState.Safe"/>.
    /// </summary>
    public RoofSafetyState State { get; init; }

    /// <summary>
    /// Why the state is not <see cref="RoofSafetyState.Safe"/>: the unsafe counting sources' reasons, then the unknown
    /// ones', then <see cref="NoRequiredWeatherSourceReason"/> when it applies (or only <see cref="NoSourcesReason"/>).
    /// Empty when safe.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>
    /// The weather alone, with no telescope: <see cref="RoofSafetyState.Safe"/> only when at least one weather source is
    /// required and every required one has a recent reading that says safe; <see cref="RoofSafetyState.Unsafe"/> when a
    /// required one says unsafe; otherwise <see cref="RoofSafetyState.Unknown"/>. What the controller's Alpaca
    /// <c>SafetyMonitor</c> device publishes, so an observing program that parks its mount and closes the roof on unsafe
    /// weather does not do so when a mount unparks. Unknown from a controller that does not say.
    /// </summary>
    public RoofSafetyState WeatherState { get; init; }

    /// <summary>
    /// Why <see cref="WeatherState"/> is not <see cref="RoofSafetyState.Safe"/>: the required weather sources' unsafe
    /// reasons, then their unknown ones, or <see cref="NoRequiredWeatherSourceReason"/>. Empty when safe.
    /// </summary>
    public IReadOnlyList<string> WeatherReasons { get; init; } = [];

    /// <summary>
    /// The UniqueIDs of this installation's own Alpaca devices that the controller refuses as weather sources
    /// (<c>RoofSafety:OwnAlpacaUniqueIds</c>), so it never reads its own weather back. The Alpaca server's SafetyMonitor
    /// says safe only while its UniqueID is here. Empty from a controller that does not say.
    /// </summary>
    public IReadOnlyList<string> OwnAlpacaUniqueIds { get; init; } = [];

    /// <summary>Every configured source, in the order they were configured.</summary>
    public IReadOnlyList<RoofSafetySourceStatus> Sources { get; init; } = [];

    /// <summary>When <see cref="State"/> last changed, by the controller's clock; null when it has not been judged yet.</summary>
    public DateTimeOffset? StateSinceUtc { get; init; }

    /// <summary>What Open does with <see cref="State"/>.</summary>
    public RoofSafetyInterlockMode InterlockMode { get; init; }

    /// <summary>
    /// The role an Open override needs in <see cref="RoofSafetyInterlockMode.Warn"/>: <see cref="RoofControllerApiContract.OperatorRole"/>
    /// (anyone who may open, the default) or <see cref="RoofControllerApiContract.AdminRole"/>; null in the other modes,
    /// which take no override.
    /// </summary>
    public string? OverrideRole { get; init; }
}
