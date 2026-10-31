using System.Net;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Roof;

/// <summary>
/// The roof as the Alpaca server knows it: the controller's status hub, through the client library, and Stop. Every
/// Alpaca read is served from the last status published, never by asking the controller, and never from a status that
/// is not current: while the controller cannot be reached, before its first status, or once its hub has been silent
/// for 3 s (<see cref="RoofStatusFeedOptions.StaleAfter"/>), a read is an Alpaca error
/// (<see cref="AlpacaErrors.ControllerUnavailable"/>). The 3 s are checked on every read against when the last message
/// arrived, so a read never depends on the feed's timer having run.
/// </summary>
/// <remarks>
/// A command's answer carries the controller's status straight after it. It is served once it is newer than the hub's
/// status from the same controller instance, so a read straight after the command reflects it and an older hub
/// message cannot undo it. An answer from another instance (a controller that has since restarted) is never served:
/// only the hub moves the server to a new instance. Thread-safe.
/// </remarks>
public sealed class AlpacaRoof : IHostedService, IAsyncDisposable
{
    private readonly RoofControllerClient _client;
    private readonly ILogger<AlpacaRoof> _logger;
    private readonly object _gate = new();
    private readonly RoofStatusFeed _feed;
    private readonly TimeProvider _time;
    private readonly TimeSpan _staleAfter;
    private RoofStatusResponse? _latest;
    private int _keyChecked;
    private long _aborts;

    public AlpacaRoof(RoofControllerClient client, ILogger<AlpacaRoof> logger)
    {
        _client = client;
        _logger = logger;
        _time = client.Options.TimeProvider;
        _staleAfter = client.Options.StatusFeed.StaleAfter;
        _feed = client.CreateStatusFeed();
        _feed.StatusReceived += OnStatus;
        _feed.StateChanged += OnStateChanged;
    }

    /// <summary>The status hub's connection, for the setup page.</summary>
    public RoofStatusFeedState FeedState => _feed.State;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _feed.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _feed.StopAsync();

    /// <summary>The roof's current status.</summary>
    /// <exception cref="AlpacaException">It is not known: the controller cannot be reached, refused the key, or is silent.</exception>
    public RoofStatusResponse GetCurrent()
    {
        if (_feed.State == RoofStatusFeedState.Unauthorized)
        {
            throw new AlpacaException(
                AlpacaErrors.ControllerRefusedKey,
                "The roof controller refused the Alpaca server's API key, so the roof's state is unknown. Check Alpaca:ControllerApiKeyFile.");
        }

        // When the last message arrived is read before the message, so a message arriving in between is judged by the
        // earlier time: never fresher than it is.
        var now = _time.GetUtcNow();
        var lastMessage = _feed.LastMessageUtc;
        var status = _feed.Status;
        if (status is null)
        {
            var why = _feed.LastError is { } error ? $" ({RoofText.DescribeFailure(error)})" : string.Empty;
            throw new AlpacaException(AlpacaErrors.ControllerUnavailable, $"No status from the roof controller yet{why}; the roof's state is unknown.");
        }

        if (_feed.StaleSince is { } since)
        {
            throw new AlpacaException(
                AlpacaErrors.ControllerUnavailable,
                $"The roof controller's status is not current (nothing from it since {since.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC); the roof's state is unknown.");
        }

        if (lastMessage is not { } last || now - last >= _staleAfter)
        {
            throw new AlpacaException(
                AlpacaErrors.ControllerUnavailable,
                $"The roof controller's status is not current (nothing from it for {_staleAfter.TotalSeconds:0.#} s or more); the roof's state is unknown.");
        }

        lock (_gate)
        {
            return IsNewerFromTheSameInstance(status, _latest) ? _latest! : status;
        }
    }

    /// <summary>The current status, or null when it is not known (for the setup page).</summary>
    public RoofStatusResponse? TryGetCurrent()
    {
        try
        {
            return GetCurrent();
        }
        catch (AlpacaException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stops the roof: the controller's Stop, which every role may send and a latched fault never blocks. Returns the
    /// controller's status after it. It takes no cancellation: a client that goes away does not cancel a Stop, which the
    /// client library's Stop timeout bounds.
    /// </summary>
    /// <exception cref="AlpacaException">
    /// The stop was not confirmed: the relays could not be verified off (<see cref="AlpacaErrors.RelayStateUnverified"/>),
    /// the controller could not be reached, or it refused.
    /// </exception>
    public async Task<RoofStatusResponse?> StopRoofAsync(IPAddress client, uint clientId, uint clientTransactionId)
    {
        // Counted first: an Open or Close that arrived before this AbortSlew and has not yet got its ticket is refused.
        Interlocked.Increment(ref _aborts);
        var result = await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Offer(result.Status);
        var verified = result.Outcome == RoofStopOutcome.Acknowledged && ShowsRelaysVerifiedOff(result.Status);
        _logger.LogInformation(
            "Alpaca AbortSlew from {RemoteAddress} (ClientID {ClientId}, ClientTransactionID {ClientTransactionId}): Stop {Outcome}, relays verified off: {Verified}",
            client, clientId, clientTransactionId, result.Outcome, verified);
        return result.Outcome switch
        {
            RoofStopOutcome.Acknowledged when verified => result.Status,
            RoofStopOutcome.Acknowledged or RoofStopOutcome.RelayUnverified => throw new AlpacaException(AlpacaErrors.RelayStateUnverified, UnconfirmedStop),
            _ => throw new AlpacaException(ErrorNumberFor(result.Error), result.Message),
        };
    }

    /// <summary>How many AbortSlews this server has sent on so far (#220, AA3).</summary>
    public long AbortCount => Interlocked.Read(ref _aborts);

    /// <summary>How long the server waits for a ticket: the controller answers at once.</summary>
    internal static readonly TimeSpan TicketTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Asks the controller for a ticket for one command, with the Alpaca client's own key passed on for this call only.
    /// </summary>
    public Task<RoofAlpacaTicket> RequestTicketAsync(RoofAlpacaCommandKind kind, string clientKey, RoofAlpacaForwarding client)
        => _client.Alpaca.RequestTicketAsync(kind, new RoofApiKeyCredential(clientKey), client, TicketTimeout);

    /// <summary>
    /// Sends the Alpaca client's Close with its ticket and key. The wait is the ticket's answer deadline plus 1 s; a
    /// timeout is an error the client sees, never retried (#220, R12).
    /// </summary>
    public async Task<RoofStatusResponse> CloseAsync(RoofAlpacaTicket ticket, string clientKey, RoofAlpacaForwarding client)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var timeout = TimeSpan.FromMilliseconds(ticket.AnswerDeadlineMilliseconds) + TimeSpan.FromSeconds(1);
        try
        {
            var status = await _client.Alpaca.CloseAsync(new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation),
                new RoofApiKeyCredential(clientKey), client, timeout).ConfigureAwait(false);
            Offer(status);
            return status;
        }
        catch (RoofApiException refusal)
        {
            // A refused start leaves the status as it was; the refusal's snapshot is newer still, and as true.
            Offer(refusal.RoofStatus);
            throw;
        }
    }

    /// <summary>Reports this server's own refusal of a command to the controller, for the UIs' alert. Best effort; never a credential.</summary>
    public async Task ReportRefusalAsync(RoofAlpacaEvent report)
    {
        try
        {
            await _client.Alpaca.ReportEventAsync(report).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or OperationCanceledException or RoofProtocolException)
        {
            _logger.LogInformation("Could not report a refused Alpaca {Kind} to the controller: {Reason}", report.Kind, RoofText.DescribeFailure(ex));
        }
    }

    /// <summary>What AbortSlew answers when the controller's answer to Stop does not show the relays verified off.</summary>
    public const string UnconfirmedStop =
        "The controller answered Stop, but its answer does not show the relay register verified all off: confirm at the roof that the motor has stopped.";

    /// <summary>
    /// True when <paramref name="status"/>, a Stop's answer, shows the stop done: the relay register read back and verified
    /// all off (zero), and no motion commanded. Anything less (no status, one that could not be read, a register not read
    /// back, a relay on, motion still commanded) is not a verified stop.
    /// </summary>
    public static bool ShowsRelaysVerifiedOff(RoofStatusResponse? status)
        => status is { RelayRegisterState: RoofRelayRegisterState.Verified, RelayRegisterMask: 0, IsMoving: false, CommandedMotion: RoofMotionDirection.None };

    /// <summary>
    /// Takes a status a command returned, when it is newer than the one held from the same controller instance. An answer
    /// from another instance (one that has since restarted) is dropped.
    /// </summary>
    public void Offer(RoofStatusResponse? status)
    {
        if (status is null)
        {
            return;
        }

        lock (_gate)
        {
            if (IsNewerFromTheSameInstance(_latest, status))
            {
                _latest = status;
            }
        }
    }

    // Statuses compare only within one controller instance; equal versions count as newer, as RoofStatusRules has it for
    // controllers that report 0 for every snapshot.
    private static bool IsNewerFromTheSameInstance(RoofStatusResponse? current, RoofStatusResponse? candidate)
        => current is not null && candidate is not null
            && string.Equals(current.ControllerInstanceId, candidate.ControllerInstanceId, StringComparison.Ordinal)
            && candidate.StatusVersion >= current.StatusVersion;

    /// <summary>The Alpaca error number for a failed request to the controller.</summary>
    public static int ErrorNumberFor(Exception? error) => error switch
    {
        RoofApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => AlpacaErrors.ControllerRefusedKey,
        RoofApiException { Code: { } code } => AlpacaErrors.For(code),
        RoofApiException => AlpacaErrors.ControllerError,
        HttpRequestException or TimeoutException or OperationCanceledException => AlpacaErrors.ControllerUnavailable,
        _ => AlpacaErrors.ControllerError,
    };

    public async ValueTask DisposeAsync()
    {
        _feed.StatusReceived -= OnStatus;
        _feed.StateChanged -= OnStateChanged;
        await _feed.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStatus(object? sender, RoofStatusReceivedEventArgs e)
    {
        // The hub decides which controller instance is current: its status replaces one from another instance.
        lock (_gate)
        {
            if (RoofStatusRules.ShouldApply(_latest, e.Status))
            {
                _latest = e.Status;
            }
        }

        if (Interlocked.Exchange(ref _keyChecked, 1) == 0)
        {
            _ = CheckKeyAsync();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_feed.State == RoofStatusFeedState.Unauthorized)
        {
            _logger.LogError("The roof controller refused the Alpaca server's API key: every read answers {ErrorNumber} until it is fixed", AlpacaErrors.ControllerRefusedKey);
        }
    }

    // Says which key the server uses, and warns unless it is the Alpaca server's own account (#220): with a plain Viewer key
    // (phase 1's) its AbortSlew sets the controller's human Stop hold and its refusal reports are refused; with more, it
    // could do more than it needs.
    private async Task CheckKeyAsync()
    {
        try
        {
            var caller = await _client.Auth.GetCallerAsync().ConfigureAwait(false);
            if (string.Equals(caller.AlpacaAccount, RoofAlpacaContract.ServerRole, StringComparison.Ordinal))
            {
                _logger.LogInformation("The Alpaca server reaches the roof controller as {KeyName}, its own account ({Account})", caller.Name, caller.AlpacaAccount);
            }
            else if (string.Equals(caller.Role, RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase) && caller.AlpacaAccount is null)
            {
                _logger.LogWarning(
                    "The Alpaca server reaches the roof controller as {KeyName}, a plain {Role} key: its AbortSlew sets the controller's human Stop hold, and its refusal reports are refused. Give it a {Account} key",
                    caller.Name, caller.Role, RoofAlpacaContract.ServerRole);
            }
            else
            {
                _logger.LogWarning(
                    "The Alpaca server reaches the roof controller as {KeyName} with the {Role} role{Account}; it needs only the {ServerRole} account. Give it that key",
                    caller.Name, caller.Role, caller.AlpacaAccount is null ? string.Empty : $" ({caller.AlpacaAccount})", RoofAlpacaContract.ServerRole);
            }
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or RoofProtocolException or OperationCanceledException)
        {
            _logger.LogInformation("Could not ask the roof controller which key the Alpaca server uses: {Reason}", RoofText.DescribeFailure(ex));
        }
    }
}
