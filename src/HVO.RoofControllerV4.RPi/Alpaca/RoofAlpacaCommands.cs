using System.Globalization;
using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Alpaca;

/// <summary>What the Alpaca server reports about the Alpaca client behind a command (client-reported, never trusted).</summary>
/// <param name="ClientId">The client's <c>ClientID</c>.</param>
/// <param name="ClientTransactionId">The client's <c>ClientTransactionID</c>.</param>
/// <param name="Address">The client's address as the Alpaca server saw it.</param>
/// <param name="AuthMethod">How the client sent its key: <c>header</c> or <c>basic</c>.</param>
public sealed record RoofAlpacaClient(uint? ClientId = null, uint? ClientTransactionId = null, string? Address = null, string? AuthMethod = null)
{
    /// <summary>No client metadata (a request not from a trusted Alpaca server address).</summary>
    public static RoofAlpacaClient None { get; } = new();
}

/// <summary>
/// The controller's Alpaca command state (#220, phase 3): which commands are on, the authorization epoch, the human Stop
/// hold, the operator lease people hold from the UIs, override grants, alerts, and the tickets and admission of Alpaca
/// commands (<c>RoofAlpacaCommands.Tickets.cs</c>). The controller is the only authority: the Alpaca server refuses on
/// its own only for local reasons and forwards everything else here. Everything is in memory: a restart drops every
/// grant, lease, alert and ticket, and sets the Stop hold again.
/// </summary>
/// <remarks>
/// Lock order: the controller's lock, then this class's gate. Nothing here calls into the controller while holding the
/// gate; the admission's methods run under the controller's lock and take the gate briefly.
/// </remarks>
public sealed partial class RoofAlpacaCommands : IHostedService, IDisposable
{
    /// <summary>A UI's lease lapses when its heartbeats stop for this long (the page or session went away).</summary>
    public static readonly TimeSpan LeaseHeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often the leases, grants and tickets are checked, and the warnings judged.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>The most alerts kept; the oldest go first.</summary>
    internal const int MaximumAlerts = 100;

    /// <summary>The alerts the status carries, newest first.</summary>
    internal const int AlertsInStatus = 20;

    /// <summary>The warning shown while Alpaca commands are on and no telescope blocks the roof (#220, P4).</summary>
    public const string NoBlockingScopeWarning = "Alpaca Close checks no telescope: no telescope is set to block the roof.";

    private readonly object _gate = new();
    private readonly IRoofControllerServiceV4 _controller;
    private readonly RoofSafetyService _safety;
    private readonly IOptionsMonitor<RoofAlpacaCommandOptions> _options;
    private readonly IOptionsMonitor<RoofOperatorLeaseOptions> _leaseOptions;
    private readonly IOptionsMonitor<RoofSafetyOptions> _safetyOptions;
    private readonly RoofApiKeyStore _keys;
    private readonly IReadOnlyList<ITelescopeParkSource> _registeredTelescopes;
    private readonly TimeProvider _time;
    private readonly ILogger<RoofAlpacaCommands> _logger;
    private readonly IDisposable? _optionsChange;
    private readonly IDisposable? _leaseOptionsChange;
    private readonly IDisposable? _safetyOptionsChange;
    private ITimer? _ticker;

    // Guarded by _gate.
    private RoofAlpacaCommandMode _mode;
    private string _serverAddresses;
    private int _window;
    private RoofSafetyInterlockMode _interlockMode;
    private string _commanderKeys;
    private long _epoch = 1;
    private long _commandsOnSince;
    private RoofStopHoldState _hold;
    private readonly Dictionary<string, OperatorLease> _leases = new(StringComparer.Ordinal);
    private string? _idleLapsedHolder;

    // Sessions whose lease lapsed for want of input, or whose person stepped away by granting: their heartbeats take it
    // again only with input.
    private readonly HashSet<string> _idleLapsedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<RoofAlpacaCommandKind, GrantEntry> _grants = [];
    private readonly List<RoofAlpacaAlert> _alerts = [];
    private long _alertSequence;
    private long _lastAlertNotice;

    public RoofAlpacaCommands(
        IRoofControllerServiceV4 controller,
        RoofSafetyService safety,
        IOptionsMonitor<RoofAlpacaCommandOptions> options,
        IOptionsMonitor<RoofOperatorLeaseOptions> leaseOptions,
        IOptionsMonitor<RoofSafetyOptions> safetyOptions,
        RoofApiKeyStore keys,
        IEnumerable<ITelescopeParkSource>? registeredTelescopes = null,
        TimeProvider? time = null,
        ILogger<RoofAlpacaCommands>? logger = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _leaseOptions = leaseOptions ?? throw new ArgumentNullException(nameof(leaseOptions));
        _safetyOptions = safetyOptions ?? throw new ArgumentNullException(nameof(safetyOptions));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _registeredTelescopes = registeredTelescopes?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RoofAlpacaCommands>.Instance;

        var current = _options.CurrentValue;
        _mode = current.AllowCommands;
        _serverAddresses = DescribeAddresses(current);
        _window = current.OverrideWindowMinutes;
        _interlockMode = _safetyOptions.CurrentValue.InterlockMode;
        _commanderKeys = CommanderKeys();
        _commandsOnSince = _time.GetTimestamp();
        _hold = new RoofStopHoldState { Held = true, SetBy = "the controller's start", SinceUtc = _time.GetUtcNow() };
        _optionsChange = _options.OnChange(OnOptionsChanged);
        _leaseOptionsChange = _leaseOptions.OnChange(_ => NotifyChanged());
        _safetyOptionsChange = _safetyOptions.OnChange(OnSafetyOptionsChanged);
    }

    /// <summary>Raised (outside any lock) when the state the status shows changed: the broadcaster republishes.</summary>
    public event Action? Changed;

    /// <summary>The commands on now.</summary>
    public RoofAlpacaCommandMode Mode
    {
        get { lock (_gate) { return _mode; } }
    }

    /// <summary>The authorization epoch (#220, R8-5, L5): moves when the External account, its keys or role, AllowCommands or ServerAddresses change.</summary>
    public long Epoch
    {
        get
        {
            lock (_gate)
            {
                RefreshEpoch_NoLock();
                return _epoch;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (options.AllowCommands != RoofAlpacaCommandMode.Off)
        {
            _logger.LogWarning("Alpaca commands are on ({Mode}). The human Stop hold is set until a person clears it in the web UI, the terminal or the kiosk.", options.AllowCommands);
            if (ConfiguredBlockingScopes().Count == 0)
            {
                _logger.LogWarning("{Warning} An Alpaca Close is then judged on the controller's own protections alone.", NoBlockingScopeWarning);
            }
        }

        _ticker = _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ticker?.Dispose();
        _ticker = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        _optionsChange?.Dispose();
        _leaseOptionsChange?.Dispose();
        _safetyOptionsChange?.Dispose();
    }

    #region Status

    /// <summary>The Alpaca command state as the status shows it.</summary>
    public RoofAlpacaStatus GetStatus()
    {
        var options = _options.CurrentValue;
        var latencies = _safety.BlockingScopeLatencies();
        var blocking = ConfiguredBlockingScopes();
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            var warnings = new List<string>();
            if (_mode != RoofAlpacaCommandMode.Off)
            {
                if (blocking.Count == 0)
                {
                    warnings.Add(NoBlockingScopeWarning);
                }

                foreach (var latency in latencies)
                {
                    if (latency.P99Milliseconds is { } p99 && RoofSafetyService.RereadSpacing.TotalMilliseconds + p99 + 100 > options.ScopeRereadBudgetMs)
                    {
                        warnings.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Telescope '{latency.Scope}' answers in {p99:0} ms (p99): the re-read budget of {options.ScopeRereadBudgetMs} ms needs at least 250 + {p99:0} + 100 ms. Raise {RoofAlpacaCommandOptions.SectionName}:ScopeRereadBudgetMs."));
                    }
                }
            }

            return new RoofAlpacaStatus
            {
                Mode = _mode,
                Hold = _hold,
                OperatorLease = LeaseState_NoLock(now),
                Grants = [.. _grants.Values.Where(grant => grant.ExpiresAt > now).Select(grant => grant.ToContract(_time, now))],
                Alerts = [.. _alerts.OrderByDescending(alert => alert.LastUtc).Take(AlertsInStatus)],
                Warnings = warnings,
                BlockingScopes = blocking.Count,
                RereadBudgetMilliseconds = options.ScopeRereadBudgetMs,
                OverrideWindowMinutes = options.OverrideWindowMinutes,
                ScopeLatencies = latencies
            };
        }
    }

    /// <summary><paramref name="status"/> with the Alpaca command state now.</summary>
    public RoofStatusResponse WithAlpaca(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status with { Alpaca = GetStatus() };
    }

    private RoofOperatorLeaseState LeaseState_NoLock(long now)
    {
        var lease = _leases.Values.OrderBy(entry => entry.TakenAt).FirstOrDefault();
        if (lease is null)
        {
            return new RoofOperatorLeaseState { Held = false, Holder = null, IdleSecondsRemaining = null, IdleLapsedHolder = _idleLapsedHolder };
        }

        double? idle = null;
        if (_mode != RoofAlpacaCommandMode.Off)
        {
            var remaining = _time.GetElapsedTime(now, IdleDeadline_NoLock(lease)).TotalSeconds;
            idle = remaining > 0 ? remaining : 0;
        }

        return new RoofOperatorLeaseState { Held = true, Holder = lease.Name, IdleSecondsRemaining = idle };
    }

    #endregion

    #region Stop hold

    /// <summary>
    /// Records a Stop: every Stop sets the human Stop hold and drops every override grant, except the Alpaca server's own
    /// AbortSlew (its own account from a trusted address), which does neither. Called whether or not the stop verified.
    /// </summary>
    /// <param name="user">Who sent the Stop.</param>
    /// <param name="peerTrusted">True when the TCP peer is a trusted Alpaca server address.</param>
    public void NoteStop(ClaimsPrincipal? user, bool peerTrusted)
    {
        if (user is not null && peerTrusted && user.IsInRole(RoofAlpacaContract.ServerRole))
        {
            _logger.LogInformation("Stop from the Alpaca server's own account (AbortSlew): the human Stop hold is not set");
            return;
        }

        var who = RoofPrincipalFactory.DescribeCaller(user);
        lock (_gate)
        {
            var wasHeld = _hold.Held;
            _hold = new RoofStopHoldState { Held = true, SetBy = who, SinceUtc = _time.GetUtcNow() };
            DropGrants_NoLock($"a Stop by {who}");
            if (!wasHeld)
            {
                _logger.LogWarning("Human Stop hold set by a Stop from {Caller}: Alpaca commands are refused until a person clears it", who);
            }
        }

        NotifyChanged();
    }

    /// <summary>A person clears the human Stop hold (UI sessions only; audit-logged).</summary>
    public RoofStopHoldState ClearHold(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var who = RoofPrincipalFactory.DescribeCaller(user);
        RoofStopHoldState state;
        lock (_gate)
        {
            var was = _hold;
            _hold = new RoofStopHoldState { Held = false, SetBy = null, SinceUtc = null };
            state = _hold;
            _logger.LogWarning("AUDIT human Stop hold cleared by {Caller} (it was set by {SetBy}); Alpaca commands may run again when allowed",
                who, was.SetBy ?? "nobody");
        }

        NotifyChanged();
        return state;
    }

    #endregion

    #region Operator lease

    /// <summary>
    /// A person's UI takes or keeps the operator lease (a heartbeat). <paramref name="input"/> is true when the person has
    /// done something since the last heartbeat: it resets the idle clock; a heartbeat alone never does. Taking the lease
    /// drops every override grant and never stops a motion.
    /// </summary>
    public RoofOperatorLeaseState Heartbeat(ClaimsPrincipal user, bool input)
    {
        ArgumentNullException.ThrowIfNull(user);
        var session = SessionId(user);
        var name = user.Identity?.Name ?? "a person";
        var now = _time.GetTimestamp();
        bool taken;
        RoofOperatorLeaseState state;
        lock (_gate)
        {
            taken = !_leases.TryGetValue(session, out var lease);
            if (taken && !input && _idleLapsedSessions.Contains(session))
            {
                // Its lease lapsed for want of input, or its person granted: a heartbeat alone does not take it back.
                return LeaseState_NoLock(now);
            }

            if (taken)
            {
                _idleLapsedSessions.Remove(session);
                lease = new OperatorLease(name, RoofPrincipalFactory.DescribeCaller(user), now);
                _leases[session] = lease;
                _idleLapsedHolder = null;
                DropGrants_NoLock($"{lease.Caller} taking the operator lease");
                _logger.LogInformation("Operator lease taken by {Caller}: Alpaca commands are refused while it is held", lease.Caller);
            }

            lease!.LastHeartbeat = now;
            if (input || taken)
            {
                lease.LastInput = now;
            }

            state = LeaseState_NoLock(now);
        }

        if (taken)
        {
            NotifyChanged();
        }

        return state;
    }

    /// <summary>A person's UI gives the operator lease up (its page closing, the person signing out).</summary>
    public void ReleaseLease(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        bool released;
        lock (_gate)
        {
            released = _leases.Remove(SessionId(user), out var lease);
            if (released)
            {
                _logger.LogInformation("Operator lease released by {Caller}", lease!.Caller);
            }
        }

        if (released)
        {
            NotifyChanged();
        }
    }

    private static string SessionId(ClaimsPrincipal user)
        => user.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value ?? $"user:{user.Identity?.Name}";

    /// <summary>When the lease lapses for want of input (controller time): the idle limit after the later of the last input and commands going on.</summary>
    private long IdleDeadline_NoLock(OperatorLease lease)
    {
        var minutes = _leaseOptions.CurrentValue.OperatorLeaseIdleMinutes;
        var from = Math.Max(lease.LastInput, _commandsOnSince);
        return from + (long)(TimeSpan.FromMinutes(minutes).TotalSeconds * _time.TimestampFrequency);
    }

    #endregion

    #region Override grants

    /// <summary>
    /// A person grants an override for one command of <paramref name="request"/>'s kind (UI sessions only): it lifts only
    /// the reasons they were shown, lasts the override window, replaces any grant of that kind, and is used up by the next
    /// command of that kind that gets past the controller's own checks, whatever happens to it. Never through Alpaca. The
    /// person steps away with it: their operator lease ends, since a grant never lifts a person's lease.
    /// </summary>
    public RoofGrantOutcome Grant(ClaimsPrincipal user, RoofOverrideGrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != RoofAlpacaCommandKind.Close)
        {
            return RoofGrantOutcome.Forbidden("Open grants are not available: Alpaca Open is refused in this version.");
        }

        var keys = (request.ReasonKeys ?? []).Select(key => key?.Trim()).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length is 0 or > 32 || keys.Any(key => !IsCloseReasonKey(key)))
        {
            return RoofGrantOutcome.Invalid(
                "A Close grant names the reasons it lifts: 1 to 32 keys, each scope-not-clear:<telescope> or scope-unknown:<telescope>, as the refusal or alert gave them.");
        }

        var who = RoofPrincipalFactory.DescribeCaller(user);
        var ui = RoofPrincipalFactory.GetCredentialKind(user) == RoofCredentialKind.Pin ? "kiosk" : "session";
        GrantEntry grant;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            grant = new GrantEntry(
                Guid.NewGuid().ToString("N")[..16],
                request.Kind,
                keys,
                who,
                ui,
                _time.GetUtcNow(),
                now + (long)(TimeSpan.FromMinutes(_window).TotalSeconds * _time.TimestampFrequency));
            var replaced = _grants.Remove(request.Kind, out var previous);
            _grants[request.Kind] = grant;
            var steppedAway = StepAway_NoLock(user);
            _logger.LogWarning(
                "AUDIT override grant {GrantId} for one Alpaca {Kind} by {Caller} ({Ui}), {Window} min, lifting {Reasons}{Replaced}; {Leases} operator lease(s) of theirs ended with it",
                grant.Id, grant.Kind, who, ui, _window, string.Join(", ", keys), replaced ? $"; it replaces grant {previous!.Id}" : string.Empty, steppedAway);
        }

        NotifyChanged();
        return RoofGrantOutcome.Granted(grant.ToContract(_time, _time.GetTimestamp()));
    }

    /// <summary>
    /// The person who grants steps away: a grant never lifts a person's operator lease, so every lease of theirs ends with
    /// it (each of their sessions), and each of those sessions, and the one granting, takes it again only with input,
    /// which drops the grant as any person taking the lease does. Another person's lease is untouched and still refuses.
    /// </summary>
    private int StepAway_NoLock(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        var sessions = _leases.Where(entry => string.Equals(entry.Value.Name, name, StringComparison.Ordinal)).Select(entry => entry.Key).ToArray();
        foreach (var session in sessions)
        {
            _leases.Remove(session);
            NoteAway_NoLock(session);
        }

        NoteAway_NoLock(SessionId(user));
        return sessions.Length;
    }

    private void NoteAway_NoLock(string session)
    {
        if (_idleLapsedSessions.Count >= 256)
        {
            _idleLapsedSessions.Clear();
        }

        _idleLapsedSessions.Add(session);
    }

    /// <summary>A person revokes the grant of <paramref name="kind"/> (UI sessions only; audit-logged). False when there was none.</summary>
    public bool Revoke(RoofAlpacaCommandKind kind, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        bool revoked;
        lock (_gate)
        {
            revoked = _grants.Remove(kind, out var grant);
            if (revoked)
            {
                _logger.LogWarning("AUDIT override grant {GrantId} for one Alpaca {Kind} revoked by {Caller}", grant!.Id, kind, RoofPrincipalFactory.DescribeCaller(user));
            }
        }

        if (revoked)
        {
            NotifyChanged();
        }

        return revoked;
    }

    /// <summary>The grants in force.</summary>
    public IReadOnlyList<RoofOverrideGrant> Grants()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            return [.. _grants.Values.Where(grant => grant.ExpiresAt > now).Select(grant => grant.ToContract(_time, now))];
        }
    }

    private static bool IsCloseReasonKey(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == key.Length - 1 || key.Length > 128)
        {
            return false;
        }

        var kind = key[..colon];
        return kind is RoofAlpacaContract.ScopeNotClearKind or RoofAlpacaContract.ScopeUnknownKind;
    }

    private void DropGrants_NoLock(string why)
    {
        if (_grants.Count == 0)
        {
            return;
        }

        foreach (var grant in _grants.Values)
        {
            _logger.LogWarning("Override grant {GrantId} for one Alpaca {Kind} dropped: {Why}", grant.Id, grant.Kind, why);
        }

        _grants.Clear();
    }

    #endregion

    #region Alerts

    /// <summary>The alerts, newest first.</summary>
    public IReadOnlyList<RoofAlpacaAlert> Alerts()
    {
        lock (_gate)
        {
            return [.. _alerts.OrderByDescending(alert => alert.LastUtc)];
        }
    }

    /// <summary>A person acknowledges an alert (UI sessions only; audit-logged). Null when there is no such alert.</summary>
    public RoofAlpacaAlert? Acknowledge(string id, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        RoofAlpacaAlert? acknowledged = null;
        var who = RoofPrincipalFactory.DescribeCaller(user);
        lock (_gate)
        {
            var index = _alerts.FindIndex(alert => string.Equals(alert.Id, id, StringComparison.Ordinal));
            if (index >= 0)
            {
                acknowledged = _alerts[index] with { Acknowledged = true, AcknowledgedBy = who };
                _alerts[index] = acknowledged;
                _logger.LogInformation("AUDIT Alpaca alert {AlertId} ({Kind}: {Message}) acknowledged by {Caller}", id, acknowledged.Kind, acknowledged.Message, who);
            }
        }

        if (acknowledged is not null)
        {
            NotifyChanged();
        }

        return acknowledged;
    }

    /// <summary>
    /// The Alpaca server's own refusal of a Close (no credential, a malformed one): one de-duplicated alert naming the
    /// client and its address, never the key. An Open's is only logged.
    /// </summary>
    public void ReportServerEvent(RoofAlpacaEvent report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var client = new RoofAlpacaClient(report.ClientId, report.ClientTransactionId, report.ClientAddress);
        if (string.Equals(report.Kind, nameof(RoofAlpacaCommandKind.Close), StringComparison.OrdinalIgnoreCase))
        {
            RaiseNotAuthorised(RoofAlpacaCommandKind.Close, client, "the Alpaca server refused it before it reached the controller");
        }
        else
        {
            _logger.LogInformation("The Alpaca server refused an Alpaca {Kind} as not authorised ({Reason}) from {ClientAddress}", Shorten(report.Kind), Shorten(report.Reason), Shorten(report.ClientAddress));
        }
    }

    /// <summary>A Close the controller refused as not authorised (a wrong or missing key at the ticket or the command).</summary>
    public void ReportAuthFailure(RoofAlpacaCommandKind kind, RoofAlpacaClient client)
    {
        if (kind == RoofAlpacaCommandKind.Close)
        {
            RaiseNotAuthorised(kind, client, "the controller did not accept its key");
        }
        else
        {
            _logger.LogInformation("An Alpaca {Kind} was refused as not authorised from {ClientAddress} (reported)", kind, client.Address);
        }
    }

    private void RaiseNotAuthorised(RoofAlpacaCommandKind kind, RoofAlpacaClient client, string why)
        => RaiseAlert("NotAuthorised", kind, $"An Alpaca {kind} was refused as not authorised: {why}.", [], client, rateLimited: true);

    /// <summary>
    /// Raises an alert, or counts it again: one per kind, command and reasons (and, for a refusal as not authorised, the
    /// client's address) until a person acknowledges it.
    /// </summary>
    internal void RaiseAlert(string kind, RoofAlpacaCommandKind? command, string message, IReadOnlyList<string> reasons, RoofAlpacaClient? client, bool rateLimited = false)
    {
        var now = _time.GetUtcNow();
        var sorted = reasons.Order(StringComparer.Ordinal).ToArray();
        bool notify;
        lock (_gate)
        {
            var index = _alerts.FindIndex(alert => !alert.Acknowledged && alert.Kind == kind && alert.Command == command
                && alert.ReasonKeys.SequenceEqual(sorted)
                && (kind != "NotAuthorised" || alert.ClientAddress == client?.Address));
            if (index >= 0)
            {
                var existing = _alerts[index];
                _alerts[index] = existing with
                {
                    Count = existing.Count + 1,
                    LastUtc = now,
                    Message = message,
                    ClientId = client?.ClientId ?? existing.ClientId,
                    ClientAddress = client?.Address ?? existing.ClientAddress
                };
            }
            else
            {
                _alerts.Add(new RoofAlpacaAlert
                {
                    Id = (++_alertSequence).ToString(CultureInfo.InvariantCulture),
                    Kind = kind,
                    Command = command,
                    Message = message,
                    ReasonKeys = sorted,
                    ClientId = client?.ClientId,
                    ClientAddress = client?.Address,
                    FirstUtc = now,
                    LastUtc = now
                });
                while (_alerts.Count > MaximumAlerts)
                {
                    _alerts.RemoveAt(0);
                }

                _logger.LogWarning("Alpaca alert ({Kind}): {Message} Client {ClientId} at {ClientAddress} (reported)", kind, message, client?.ClientId, client?.Address);
            }

            // A refusal as not authorised can come fast; its count still moves, but the UIs hear of it at most once a second.
            var stamp = _time.GetTimestamp();
            notify = !rateLimited || index < 0 || _time.GetElapsedTime(_lastAlertNotice, stamp) >= TimeSpan.FromSeconds(1);
            if (notify)
            {
                _lastAlertNotice = stamp;
            }
        }

        if (notify)
        {
            NotifyChanged();
        }
    }

    private static string Shorten(string? text) => text is null ? "(none)" : text.Length <= 64 ? text : text[..64];

    #endregion

    #region Epoch, options and the tick

    private string CommanderKeys()
        => string.Join(";", _keys.Keys
            .Where(key => key.Role == RoofAlpacaContract.CommanderRole)
            .Select(key => $"{key.KeyId}|{key.Name}")
            .Order(StringComparer.Ordinal));

    /// <summary>Moves the epoch when the External account's keys or role changed (a key added, rotated, re-roled or removed).</summary>
    private void RefreshEpoch_NoLock()
    {
        var keys = CommanderKeys();
        if (string.Equals(keys, _commanderKeys, StringComparison.Ordinal))
        {
            return;
        }

        _commanderKeys = keys;
        BumpEpoch_NoLock("the External Alpaca account's keys or role changed");
    }

    private void BumpEpoch_NoLock(string why)
    {
        _epoch++;
        _logger.LogWarning("Alpaca authorization epoch {Epoch}: {Why}. Commands already admitted keep running; later ones are judged anew, and every override grant is dropped", _epoch, why);
        DropGrants_NoLock(why);
    }

    private void OnOptionsChanged(RoofAlpacaCommandOptions options)
    {
        lock (_gate)
        {
            if (options.AllowCommands != _mode)
            {
                var was = _mode;
                _mode = options.AllowCommands;
                if (was == RoofAlpacaCommandMode.Off)
                {
                    // The idle clock starts when commands go on (#220, §14.4, L7).
                    _commandsOnSince = _time.GetTimestamp();
                }

                BumpEpoch_NoLock($"AllowCommands changed from {was} to {_mode}");
            }

            var addresses = DescribeAddresses(options);
            if (!string.Equals(addresses, _serverAddresses, StringComparison.Ordinal))
            {
                _serverAddresses = addresses;
                BumpEpoch_NoLock("ServerAddresses changed");
            }

            if (options.OverrideWindowMinutes != _window)
            {
                _window = options.OverrideWindowMinutes;
                DropGrants_NoLock("the override window changed");
            }
        }

        NotifyChanged();
    }

    private void OnSafetyOptionsChanged(RoofSafetyOptions options)
    {
        lock (_gate)
        {
            if (options.InterlockMode == _interlockMode)
            {
                return;
            }

            _interlockMode = options.InterlockMode;
            DropGrants_NoLock($"the safety interlock mode changed to {options.InterlockMode}");
        }

        NotifyChanged();
    }

    private static string DescribeAddresses(RoofAlpacaCommandOptions options)
        => string.Join(",", options.ParsedServerAddresses.Select(address => address.ToString()).Order(StringComparer.Ordinal));

    /// <summary>
    /// The configured telescopes that block the roof, by name: the <c>RoofSafety:Telescopes</c> the validated configuration
    /// gives with <c>BlocksRoof</c>, and any registered telescope source that blocks it.
    /// </summary>
    public IReadOnlyList<string> ConfiguredBlockingScopes()
    {
        var configured = (_safetyOptions.CurrentValue.Telescopes ?? [])
            .Where(telescope => telescope.BlocksRoof)
            .Select(telescope => telescope.Name.Trim());
        var registered = _registeredTelescopes.Where(source => source.BlocksRoof).Select(source => string.IsNullOrWhiteSpace(source.Name) ? "unnamed" : source.Name);
        return [.. registered.Concat(configured).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Once a second: leases, grants and tickets that ran out, the External account's keys, and mid-motion alerts.</summary>
    internal void Tick()
    {
        try
        {
            var status = _controller.GetCurrentStatusSnapshot();
            var changed = false;
            var now = _time.GetTimestamp();
            lock (_gate)
            {
                var epoch = _epoch;
                RefreshEpoch_NoLock();
                changed |= epoch != _epoch;
                changed |= ExpireLeases_NoLock(now, status);
                changed |= ExpireGrants_NoLock(now);
                PruneTickets_NoLock(now);
            }

            CheckMidMotion(status);
            if (changed)
            {
                NotifyChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Alpaca command state's periodic check failed");
        }
    }

    private bool ExpireLeases_NoLock(long now, RoofStatusResponse status)
    {
        var changed = false;
        foreach (var (session, lease) in _leases.ToArray())
        {
            if (_time.GetElapsedTime(lease.LastHeartbeat, now) >= LeaseHeartbeatTimeout)
            {
                _leases.Remove(session);
                _logger.LogInformation("Operator lease of {Caller} lapsed: its UI stopped renewing it", lease.Caller);
                changed = true;
                continue;
            }

            if (_mode == RoofAlpacaCommandMode.Off || now < IdleDeadline_NoLock(lease))
            {
                continue;
            }

            // An idle lapse never stops a motion: it waits until the person's own motion (or waiting start) ends.
            if (status.MotionOwner is { Kind: RoofMotionOwnerKind.Person } owner && owner.Name == lease.Name && (status.IsMoving || status.IsStartPending))
            {
                continue;
            }

            _leases.Remove(session);
            _idleLapsedHolder = lease.Name;
            NoteAway_NoLock(session);

            _logger.LogWarning("Operator lease of {Caller} lapsed after {Minutes} min without input: Alpaca commands may now move the roof",
                lease.Caller, _leaseOptions.CurrentValue.OperatorLeaseIdleMinutes);
            changed = true;
        }

        return changed;
    }

    private bool ExpireGrants_NoLock(long now)
    {
        var expired = _grants.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray();
        foreach (var kind in expired)
        {
            _grants.Remove(kind, out var grant);
            _logger.LogInformation("Override grant {GrantId} for one Alpaca {Kind} lapsed unused", grant!.Id, kind);
        }

        return expired.Length > 0;
    }

    /// <summary>
    /// While the Alpaca account's Close runs, a blocking telescope that stops being clear (or known) does not stop it: the
    /// roof continues and the UIs are alerted. The hard stops are the controller's, unchanged.
    /// </summary>
    private void CheckMidMotion(RoofStatusResponse status)
    {
        if (status is not { IsMoving: true, CommandedMotion: RoofMotionDirection.Closing, MotionOwner.Kind: RoofMotionOwnerKind.Alpaca })
        {
            return;
        }

        var notClear = _safety.GetCurrent().Sources
            .Where(source => source.Kind == RoofSafetySourceKind.TelescopePark && source.Counts && source.State != RoofSafetyState.Safe)
            .ToArray();
        if (notClear.Length == 0)
        {
            return;
        }

        var keys = notClear.Select(source => RoofAlpacaContract.ReasonKey(
            source.State == RoofSafetyState.Unsafe ? RoofAlpacaContract.ScopeNotClearKind : RoofAlpacaContract.ScopeUnknownKind, source.Name)).ToArray();
        RaiseAlert("MidMotion", RoofAlpacaCommandKind.Close,
            $"While the Alpaca Close runs, {string.Join("; ", notClear.Select(source => source.Reason))}. The roof continues; Stop it if it must not.",
            keys, null);
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An Alpaca state change handler failed");
        }
    }

    #endregion

    /// <summary>A person's operator lease, held from one UI session (guarded by the gate).</summary>
    private sealed class OperatorLease(string name, string caller, long takenAt)
    {
        public string Name { get; } = name;

        public string Caller { get; } = caller;

        public long TakenAt { get; } = takenAt;

        public long LastHeartbeat { get; set; } = takenAt;

        public long LastInput { get; set; } = takenAt;
    }

    /// <summary>An override grant (immutable; replaced, used up or dropped as a whole).</summary>
    private sealed record GrantEntry(string Id, RoofAlpacaCommandKind Kind, IReadOnlyList<string> ReasonKeys, string GrantedBy, string Ui, DateTimeOffset GrantedUtc, long ExpiresAt)
    {
        public RoofOverrideGrant ToContract(TimeProvider time, long now) => new()
        {
            Id = Id,
            Kind = Kind,
            ReasonKeys = ReasonKeys,
            GrantedBy = GrantedBy,
            Ui = Ui,
            GrantedUtc = GrantedUtc,
            SecondsRemaining = Math.Max(0, time.GetElapsedTime(now, ExpiresAt).TotalSeconds)
        };
    }
}

/// <summary>What a grant request came to.</summary>
public sealed record RoofGrantOutcome(RoofOverrideGrant? Grant, int StatusCode, string? Detail)
{
    public static RoofGrantOutcome Granted(RoofOverrideGrant grant) => new(grant, StatusCodes.Status201Created, null);

    public static RoofGrantOutcome Forbidden(string detail) => new(null, StatusCodes.Status403Forbidden, detail);

    public static RoofGrantOutcome Invalid(string detail) => new(null, StatusCodes.Status400BadRequest, detail);
}
