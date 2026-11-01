namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level rules of the controller's Alpaca command surface (#220, phases 3 and 4): the routes the Alpaca server and the UIs
/// call under <c>api/v4.0/alpaca</c>, the headers the Alpaca server forwards, the two account roles, and the problem
/// extensions. The Alpaca server is the only caller of the shutter routes; the person-only routes (grants, the Stop
/// hold, the operator lease, alert acknowledgement) refuse every API key.
/// </summary>
public static class RoofAlpacaContract
{
    /// <summary>The routes' prefix.</summary>
    public const string Route = "api/v4.0/alpaca";

    /// <summary><c>POST</c> with <c>?kind=Close</c> or <c>?kind=Open</c>: a single-use ticket for one command (External Alpaca account only).</summary>
    public const string TicketRoute = "shutter/ticket";

    /// <summary><c>POST</c> with a <see cref="RoofAlpacaCommandRequest"/>: an Alpaca Close (External Alpaca account only).</summary>
    public const string CloseRoute = "shutter/close";

    /// <summary><c>POST</c> with a <see cref="RoofAlpacaCommandRequest"/>: an Alpaca Open (External Alpaca account only; phase 4).</summary>
    public const string OpenRoute = "shutter/open";

    /// <summary><c>POST</c> with a <see cref="RoofAlpacaEvent"/>: the Alpaca server's own refusals (AlpacaServer account only).</summary>
    public const string EventsRoute = "events";

    /// <summary><c>GET</c> the grants; <c>POST</c> a <see cref="RoofOverrideGrantRequest"/>; <c>DELETE grants/{kind}</c> revokes.</summary>
    public const string GrantsRoute = "grants";

    /// <summary><c>DELETE</c>: a person clears the human Stop hold.</summary>
    public const string HoldRoute = "hold";

    /// <summary><c>POST</c> (<c>?input=true</c> after the person's own input): take or keep the operator lease; <c>DELETE</c> releases it.</summary>
    public const string LeaseRoute = "lease";

    /// <summary><c>GET</c> the alerts; <c>POST alerts/{id}/acknowledge</c>.</summary>
    public const string AlertsRoute = "alerts";

    /// <summary>The External Alpaca account's role: the shutter routes and status reads, nothing else (deny by default).</summary>
    public const string CommanderRole = "RoofAlpacaCommander";

    /// <summary>The Alpaca server's own account's role: Viewer rights and the events route; its AbortSlew sets no Stop hold.</summary>
    public const string ServerRole = "RoofAlpacaServer";

    /// <summary>The header an Alpaca client puts its key in, at the Alpaca server (the other way is HTTP Basic, key as both parts).</summary>
    public const string ClientKeyHeader = "X-HVO-Api-Key";

    /// <summary>The Alpaca client's <c>ClientID</c>, as the Alpaca server reports it (client-reported).</summary>
    public const string ClientIdHeader = "X-HVO-Alpaca-ClientID";

    /// <summary>The Alpaca client's <c>ClientTransactionID</c>, as the Alpaca server reports it.</summary>
    public const string ClientTransactionIdHeader = "X-HVO-Alpaca-ClientTransactionID";

    /// <summary>The Alpaca client's address as the Alpaca server saw it.</summary>
    public const string ClientAddressHeader = "X-HVO-Alpaca-ClientAddress";

    /// <summary>How the Alpaca client sent its key: <c>header</c> or <c>basic</c>.</summary>
    public const string AuthMethodHeader = "X-HVO-Alpaca-AuthMethod";

    /// <summary>Problem extension: the Alpaca rule that refused a command (<c>CommandsOff</c>, <c>NotSafe</c>, ...).</summary>
    public const string RefusalExtension = "alpacaRefusal";

    /// <summary>Problem extension: the safety reasons' stable keys behind a policy refusal.</summary>
    public const string ReasonKeysExtension = "reasonKeys";

    /// <summary>How long a ticket may wait for its command, on the controller's own monotonic clock.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(5);

    /// <summary>How long a used or expired ticket is remembered past its deadline (then it is unknown).</summary>
    public static readonly TimeSpan TicketMemory = TimeSpan.FromMinutes(10);

    /// <summary>The most tickets remembered; the oldest go first.</summary>
    public const int MaximumTickets = 1000;

    /// <summary>What a start's answer may take beyond the re-read budget and the direction-change delay (ticket and admission).</summary>
    public static readonly TimeSpan AnswerMargin = TimeSpan.FromMilliseconds(400);

    /// <summary>The reason-key kind: a blocking telescope that is not clear of the roof.</summary>
    public const string ScopeNotClearKind = "scope-not-clear";

    /// <summary>The reason-key kind: a blocking telescope whose state is not known (unread, failing, stale, re-read timed out).</summary>
    public const string ScopeUnknownKind = "scope-unknown";

    /// <summary>The reason-key kind (Open only): a required weather source whose latest reading says unsafe.</summary>
    public const string WeatherUnsafeKind = "weather-unsafe";

    /// <summary>
    /// The reason-key kind (Open only): a required weather source whose state is not known (not read yet, its latest read
    /// failed, or its reading is too old).
    /// </summary>
    public const string WeatherUnknownKind = "weather-unknown";

    /// <summary>The reason key (Open only, no source): no weather source is set Required, so nothing says the weather is safe.</summary>
    public const string NoRequiredWeatherKey = "no-required-weather-source";

    /// <summary>
    /// The reason key (Open only, no source): no telescope is set to block the roof, and
    /// <c>RoofController:Alpaca:AllowOpenWithoutBlockingScopes</c> is off, so an Open on the weather alone needs a grant.
    /// </summary>
    public const string NoBlockingScopesKey = "no-blocking-scopes";

    /// <summary>A reason's stable key: its kind and the source's name. Keys are compared; text is only shown.</summary>
    public static string ReasonKey(string kind, string source) => $"{kind}:{source}";

    /// <summary>
    /// True when a grant of <paramref name="command"/> may name <paramref name="key"/>: a Close grant lifts only telescope
    /// reasons (the weather never gates a Close); an Open grant also lifts the weather's, and the two reasons that name no
    /// source (<see cref="NoRequiredWeatherKey"/>, <see cref="NoBlockingScopesKey"/>).
    /// </summary>
    public static bool IsGrantableReasonKey(RoofAlpacaCommandKind command, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length > 128)
        {
            return false;
        }

        if (command == RoofAlpacaCommandKind.Open && key is NoRequiredWeatherKey or NoBlockingScopesKey)
        {
            return true;
        }

        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == key.Length - 1)
        {
            return false;
        }

        return key[..colon] switch
        {
            ScopeNotClearKind or ScopeUnknownKind => true,
            WeatherUnsafeKind or WeatherUnknownKind => command == RoofAlpacaCommandKind.Open,
            _ => false
        };
    }
}

/// <summary>Which Alpaca commands the controller accepts (<c>RoofController:Alpaca:AllowCommands</c>, local-only).</summary>
public enum RoofAlpacaCommandMode
{
    /// <summary>None (the default): Alpaca reads and AbortSlew only.</summary>
    Off = 0,

    /// <summary>Close only: an Alpaca Open is refused.</summary>
    CloseOnly = 1,

    /// <summary>Open and Close (#220, phase 4).</summary>
    OpenAndClose = 2
}

/// <summary>An Alpaca command's kind, for tickets and override grants.</summary>
public enum RoofAlpacaCommandKind
{
    Close = 0,
    Open = 1
}

/// <summary>A ticket the controller issued for one Alpaca command.</summary>
/// <param name="TicketId">Random, single-use: the command's request id.</param>
/// <param name="Kind">The command it is for; it is usable for no other.</param>
/// <param name="InstanceId">The controller process that issued it.</param>
/// <param name="Generation">The controller's command generation when it was issued.</param>
/// <param name="AnswerDeadlineMilliseconds">
/// How long the command's answer may take from the ticket's issue (the re-read budget, any direction-change delay and
/// <see cref="RoofAlpacaContract.AnswerMargin"/>): the Alpaca server waits this plus 1 s for the controller.
/// </param>
public sealed record RoofAlpacaTicket(string TicketId, RoofAlpacaCommandKind Kind, string InstanceId, long Generation, double AnswerDeadlineMilliseconds);

/// <summary>An Alpaca command, carrying its ticket and the copies of the instance and generation the ticket recorded.</summary>
public sealed record RoofAlpacaCommandRequest(string? TicketId, string? InstanceId, long? Generation);

/// <summary>A refusal the Alpaca server made itself (no credential, a malformed one), for the UIs' alert. Never a credential.</summary>
/// <param name="Kind">The command: <c>Close</c> or <c>Open</c>.</param>
/// <param name="Reason">Why: <c>NotAuthorised</c>.</param>
/// <param name="ClientAddress">The Alpaca client's address as the server saw it.</param>
/// <param name="ClientId">The client's <c>ClientID</c> (client-reported).</param>
/// <param name="ClientTransactionId">The client's <c>ClientTransactionID</c>.</param>
public sealed record RoofAlpacaEvent(string Kind, string Reason, string? ClientAddress, uint ClientId, uint ClientTransactionId);

/// <summary>A person's override grant request: the kind, and the reasons' keys they were shown.</summary>
public sealed record RoofOverrideGrantRequest(RoofAlpacaCommandKind Kind, IReadOnlyList<string>? ReasonKeys);

/// <summary>A time-limited, single-use override grant (UI-only).</summary>
public sealed record RoofOverrideGrant
{
    public string Id { get; init; } = string.Empty;

    public RoofAlpacaCommandKind Kind { get; init; }

    /// <summary>The reasons' keys the person was shown when granting: only these are lifted.</summary>
    public IReadOnlyList<string> ReasonKeys { get; init; } = [];

    /// <summary>Who granted it, as audit lines name them.</summary>
    public string GrantedBy { get; init; } = string.Empty;

    /// <summary>Where: <c>kiosk</c> for a PIN session, <c>session</c> for a web, terminal or Mac session.</summary>
    public string Ui { get; init; } = string.Empty;

    public DateTimeOffset GrantedUtc { get; init; }

    /// <summary>Seconds left before it lapses, on the controller's monotonic clock.</summary>
    public double SecondsRemaining { get; init; }
}

/// <summary>An alert for the UIs (never sent anywhere else), de-duplicated by command kind and reasons.</summary>
public sealed record RoofAlpacaAlert
{
    public string Id { get; init; } = string.Empty;

    /// <summary>What it is: <c>Refused</c>, <c>ContinuationRefused</c>, <c>NotAuthorised</c>, <c>Warning</c>, <c>MidMotion</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The command it concerns (<c>Close</c> or <c>Open</c>), when it concerns one.</summary>
    public RoofAlpacaCommandKind? Command { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>The reasons' stable keys (a grant made from this alert lifts exactly these).</summary>
    public IReadOnlyList<string> ReasonKeys { get; init; } = [];

    /// <summary>The Alpaca client's <c>ClientID</c> (client-reported), when one sent the command.</summary>
    public uint? ClientId { get; init; }

    /// <summary>The Alpaca client's address as the Alpaca server reported it.</summary>
    public string? ClientAddress { get; init; }

    public int Count { get; init; } = 1;

    public DateTimeOffset FirstUtc { get; init; }

    public DateTimeOffset LastUtc { get; init; }

    public bool Acknowledged { get; init; }

    public string? AcknowledgedBy { get; init; }
}

/// <summary>The operator lease a person holds from a UI, which keeps Alpaca commands off while it is held.</summary>
public sealed record RoofOperatorLeaseState
{
    public bool Held { get; init; }

    /// <summary>Who holds it.</summary>
    public string? Holder { get; init; }

    /// <summary>Seconds of no input left before it lapses (only while Alpaca commands are on); null when it does not idle out.</summary>
    public double? IdleSecondsRemaining { get; init; }

    /// <summary>
    /// Whose lease last lapsed for want of input, until someone takes it again: their UI says "Alpaca may now move the
    /// roof".
    /// </summary>
    public string? IdleLapsedHolder { get; init; }
}

/// <summary>The human Stop hold: after a person's Stop, Alpaca commands wait until a person clears it.</summary>
public sealed record RoofStopHoldState
{
    public bool Held { get; init; }

    /// <summary>What set it: a person's Stop (named), or the controller's start.</summary>
    public string? SetBy { get; init; }

    public DateTimeOffset? SinceUtc { get; init; }
}

/// <summary>A blocking telescope's read latency, from the background polls and re-reads: every GET a re-read sends.</summary>
public sealed record RoofScopeLatency(string Scope, double? P95Milliseconds, double? P99Milliseconds, int Samples);

/// <summary>The Alpaca command state as the status shows it (<see cref="RoofStatusResponse.Alpaca"/>).</summary>
public sealed record RoofAlpacaStatus
{
    public RoofAlpacaCommandMode Mode { get; init; }

    public RoofStopHoldState Hold { get; init; } = new();

    public RoofOperatorLeaseState OperatorLease { get; init; } = new();

    public IReadOnlyList<RoofOverrideGrant> Grants { get; init; } = [];

    /// <summary>The alerts, newest first, at most 20.</summary>
    public IReadOnlyList<RoofAlpacaAlert> Alerts { get; init; } = [];

    /// <summary>
    /// Persistent warnings ("Alpaca Close checks no telescope", Alpaca Open allowed on the weather alone, a re-read budget
    /// too short for the measured latency).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// <c>RoofController:Alpaca:AllowOpenWithoutBlockingScopes</c>: with no telescope set to block the roof, an Alpaca Open
    /// may go on the weather alone (otherwise it needs a grant).
    /// </summary>
    public bool AllowOpenWithoutBlockingScopes { get; init; }

    /// <summary>
    /// Why no Open grant can be made now, for the UIs to show beside a disabled Open grant control (the safety interlock is
    /// Block: no grant opens the roof); null while Open grants may be made.
    /// </summary>
    public string? OpenGrantUnavailable { get; init; }

    /// <summary>How many telescopes block the roof (the configuration's).</summary>
    public int BlockingScopes { get; init; }

    public double RereadBudgetMilliseconds { get; init; }

    public double OverrideWindowMinutes { get; init; }

    public IReadOnlyList<RoofScopeLatency> ScopeLatencies { get; init; } = [];
}
