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

    // Says which key the server uses, and warns when it may do more than this phase needs (a Viewer key reads and stops).
    private async Task CheckKeyAsync()
    {
        try
        {
            var caller = await _client.Auth.GetCallerAsync().ConfigureAwait(false);
            if (string.Equals(caller.Role, RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("The Alpaca server reaches the roof controller as {KeyName} ({Role})", caller.Name, caller.Role);
            }
            else
            {
                _logger.LogWarning(
                    "The Alpaca server reaches the roof controller as {KeyName} with the {Role} role; it needs only {ViewerRole}. Give it a Viewer key",
                    caller.Name, caller.Role, RoofControllerApiContract.ViewerRole);
            }
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or RoofProtocolException or OperationCanceledException)
        {
            _logger.LogInformation("Could not ask the roof controller which key the Alpaca server uses: {Reason}", RoofText.DescribeFailure(ex));
        }
    }
}
