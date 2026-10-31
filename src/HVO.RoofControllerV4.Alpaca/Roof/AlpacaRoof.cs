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
/// (<see cref="AlpacaErrors.ControllerUnavailable"/>).
/// </summary>
/// <remarks>
/// A command's answer carries the controller's status straight after it. It is merged with the hub's in the
/// controller's order (<see cref="RoofStatusRules.ShouldApply"/>), so an older hub message cannot overwrite it, and a
/// read straight after the command reflects it. Thread-safe.
/// </remarks>
public sealed class AlpacaRoof : IHostedService, IAsyncDisposable
{
    private readonly RoofControllerClient _client;
    private readonly ILogger<AlpacaRoof> _logger;
    private readonly object _gate = new();
    private readonly RoofStatusFeed _feed;
    private RoofStatusResponse? _latest;
    private int _keyChecked;

    public AlpacaRoof(RoofControllerClient client, ILogger<AlpacaRoof> logger)
    {
        _client = client;
        _logger = logger;
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

        lock (_gate)
        {
            return _latest is not null && RoofStatusRules.ShouldApply(status, _latest) ? _latest : status;
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
    /// controller's status after it.
    /// </summary>
    /// <exception cref="AlpacaException">
    /// The stop was not confirmed: the relays could not be verified off (<see cref="AlpacaErrors.RelayStateUnverified"/>),
    /// the controller could not be reached, or it refused.
    /// </exception>
    public async Task<RoofStatusResponse?> StopAsync(IPAddress client, uint clientId, uint clientTransactionId, CancellationToken cancellationToken)
    {
        var result = await _client.StopAsync(cancellationToken).ConfigureAwait(false);
        Offer(result.Status);
        _logger.LogInformation(
            "Alpaca AbortSlew from {RemoteAddress} (ClientID {ClientId}, ClientTransactionID {ClientTransactionId}): Stop {Outcome}",
            client, clientId, clientTransactionId, result.Outcome);
        return result.Outcome switch
        {
            RoofStopOutcome.Acknowledged => result.Status,
            RoofStopOutcome.RelayUnverified => throw new AlpacaException(AlpacaErrors.RelayStateUnverified, result.Message),
            _ => throw new AlpacaException(ErrorNumberFor(result.Error), result.Message),
        };
    }

    /// <summary>Takes a status a command returned, when it is newer than the one held.</summary>
    public void Offer(RoofStatusResponse? status)
    {
        if (status is null)
        {
            return;
        }

        lock (_gate)
        {
            if (RoofStatusRules.ShouldApply(_latest, status))
            {
                _latest = status;
            }
        }
    }

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
        Offer(e.Status);
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
