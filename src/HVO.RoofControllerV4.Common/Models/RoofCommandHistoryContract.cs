namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level rules of the roof command history (<c>GET api/v4.0/RoofControl/History</c>, #206): the roof commands the
/// controller was sent, accepted or refused, with who sent them, from where, and the roof's status afterwards.
/// </summary>
public static class RoofCommandHistoryContract
{
    /// <summary>The history's route, below <c>api/v4.0/RoofControl</c>.</summary>
    public const string Route = "History";

    /// <summary>Entries in a page when the request does not say.</summary>
    public const int DefaultTake = 50;

    /// <summary>The most entries one page holds.</summary>
    public const int MaximumTake = 500;

    /// <summary>How long the controller keeps an entry unless configured otherwise: 90 days.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>How many entries the controller keeps unless configured otherwise; the oldest go first.</summary>
    public const int DefaultMaximumEntries = 10_000;

    /// <summary>
    /// How many anonymous entries the controller keeps, apart from <see cref="DefaultMaximumEntries"/>: anonymous Stops
    /// (<c>AllowAnonymousStop</c>) never push anyone's commands out.
    /// </summary>
    public const int MaximumAnonymousEntries = 500;

    /// <summary>
    /// The header in which the web UI names the address of the person it sends a command for. The controller believes it
    /// only from an address it trusts as a proxy (<c>RoofCommandHistory:TrustedProxies</c>, its own loopback by default).
    /// </summary>
    public const string ForwardedForHeaderName = "X-Forwarded-For";
}

/// <summary>A roof command the history records.</summary>
public enum RoofHistoryCommand
{
    Open = 0,
    Close = 1,
    Stop = 2,
    ClearFault = 3,

    /// <summary>An operator lease renewal. Only refused renewals are recorded: an accepted one only keeps a recorded motion going.</summary>
    Lease = 4
}

/// <summary>Who sent a recorded command.</summary>
public enum RoofHistoryPrincipalKind
{
    /// <summary>Nobody signed in: a Stop the controller accepts anonymously (<c>AllowAnonymousStop</c>).</summary>
    Anonymous = 0,

    /// <summary>A person: a password or PIN session, or a person's own API key.</summary>
    Person = 1,

    /// <summary>An API key that is not a person's own, such as a kiosk's device key or a script's key.</summary>
    ApiKey = 2
}

/// <summary>One recorded roof command, as the controller answered it.</summary>
public sealed record RoofCommandHistoryEntry
{
    /// <summary>Increases with each entry, across restarts.</summary>
    public long Id { get; init; }

    /// <summary>When the controller answered the command.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    public RoofHistoryCommand Command { get; init; }

    /// <summary>The person or the API key's name, as it was then; null for an anonymous Stop. Shown, never matched.</summary>
    public string? User { get; init; }

    /// <summary>
    /// Who sent it, as the history matches it: <c>principal:&lt;id&gt;</c> or <c>person:&lt;name&gt;@&lt;created&gt;</c> for a
    /// person (a person removed and added again is a new one), <c>key:&lt;key id&gt;</c> for an API key (a rotated key is
    /// a new one); null for an anonymous Stop.
    /// </summary>
    public string? PrincipalId { get; init; }

    public RoofHistoryPrincipalKind PrincipalKind { get; init; }

    /// <summary>How the caller proved who it is; null for an anonymous Stop.</summary>
    public RoofCredentialKind? CredentialKind { get; init; }

    /// <summary>The person an API key's holder said it acted for (<c>X-On-Behalf-Of</c>). It granted nothing.</summary>
    public string? OnBehalfOf { get; init; }

    /// <summary>For a PIN session, the kiosk key it was opened at.</summary>
    public string? Device { get; init; }

    /// <summary>
    /// The address the command came from: the person's browser for a command the web UI forwarded
    /// (<see cref="ForwardedBy"/>), otherwise the address that connected.
    /// </summary>
    public string? RemoteAddress { get; init; }

    /// <summary>For a command the web UI forwarded, the web UI's address; null otherwise.</summary>
    public string? ForwardedBy { get; init; }

    /// <summary>The program that sent it, as its <c>User-Agent</c> said (for example <c>hvo-roof/4.2.0</c>); not verified.</summary>
    public string? Client { get; init; }

    /// <summary>True when the controller carried the command out.</summary>
    public bool Accepted { get; init; }

    /// <summary>For a refused command, the controller's <see cref="RoofControllerErrorCode"/> name.</summary>
    public string? Code { get; init; }

    /// <summary>For a refused command, the controller's explanation.</summary>
    public string? Detail { get; init; }

    /// <summary>The roof's status after the command, when the controller had one.</summary>
    public RoofControllerStatus? Status { get; init; }

    /// <summary>Why the roof last stopped, after the command.</summary>
    public RoofControllerStopReason? StopReason { get; init; }

    /// <summary>Whether a fault was latched after the command.</summary>
    public bool? IsFaultLatched { get; init; }

    /// <summary>
    /// For an Open sent while the observatory's safety conditions (#207) were not Safe: what the safety interlock found
    /// them to be (for example <c>Unsafe</c> or <c>Unknown</c>); null otherwise, and for every other command.
    /// </summary>
    public string? SafetyState { get; init; }

    /// <summary>
    /// True for an Open the controller accepted only because the caller overrode the safety conditions (the interlock's
    /// Warn mode, audit-logged as well); null otherwise.
    /// </summary>
    public bool? SafetyOverride { get; init; }

    /// <summary>
    /// How many commands the entry stands for: 1, or more for anonymous Stops from one source with the same answer, each
    /// within 10 minutes of the last, which are kept as one entry.
    /// </summary>
    public int Count { get; init; } = 1;

    /// <summary>When the last of <see cref="Count"/> commands came, when there was more than one.</summary>
    public DateTimeOffset? LastTimestampUtc { get; init; }
}

/// <summary>A page of the command history, newest first.</summary>
public sealed record RoofCommandHistoryPage
{
    public IReadOnlyList<RoofCommandHistoryEntry> Entries { get; init; } = [];

    /// <summary>How many entries match the request, over every page.</summary>
    public int TotalCount { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; }

    /// <summary>
    /// True when the page may hold everyone's commands (an admin's request); false when it holds only the caller's own.
    /// </summary>
    public bool AllUsers { get; init; }

    /// <summary>How long the controller keeps an entry.</summary>
    public double RetentionDays { get; init; }

    /// <summary>How many entries the controller keeps.</summary>
    public int MaximumEntries { get; init; }

    /// <summary>False when the controller keeps the history in memory only, so a restart loses it.</summary>
    public bool IsPersistent { get; init; }
}
