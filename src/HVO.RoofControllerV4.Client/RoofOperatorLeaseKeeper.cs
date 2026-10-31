using System.Net;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Holds the operator lease for a person's UI while it is open (#220): a heartbeat every
/// <see cref="RoofAlpacaText.LeaseHeartbeatInterval"/>, carrying whether the person did anything since the last one
/// (<see cref="NoteInput"/>). While any person holds it, the controller refuses Alpaca commands. It lapses when the
/// heartbeats stop (the UI went away) or, while Alpaca commands are on, after the controller's idle limit without input;
/// heartbeats alone never keep it. Disposing releases it. A credential the controller refuses for it (an API key, a
/// viewer) ends the heartbeats: nothing is held for it.
/// </summary>
public sealed class RoofOperatorLeaseKeeper : IAsyncDisposable
{
    private readonly RoofControllerClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;
    private bool _input = true;
    private bool _held;
    private bool _refused;
    private int _sending;
    private RoofOperatorLeaseState? _state;

    public RoofOperatorLeaseKeeper(RoofControllerClient client, TimeProvider? time = null, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? client.Options.TimeProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The controller's last answer about the lease; null before the first.</summary>
    public RoofOperatorLeaseState? State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>True once the controller refused this credential the lease (it is not a person who may operate).</summary>
    public bool Refused
    {
        get { lock (_gate) { return _refused; } }
    }

    /// <summary>Raised after each answer that changed <see cref="State"/> (on a pool thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts the heartbeats; the first is sent at once and takes the lease.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null || _stopping.IsCancellationRequested)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => _ = BeatAsync(), null, TimeSpan.Zero, RoofAlpacaText.LeaseHeartbeatInterval);
        }
    }

    /// <summary>The person did something: the next heartbeat resets the idle clock.</summary>
    public void NoteInput()
    {
        lock (_gate)
        {
            _input = true;
        }
    }

    /// <summary>Sends one heartbeat now (the timer's; tests call it directly).</summary>
    public async Task BeatAsync()
    {
        bool input;
        lock (_gate)
        {
            if (_refused || _stopping.IsCancellationRequested)
            {
                return;
            }

            input = _input;
            _input = false;
        }

        // One heartbeat at a time: a slow controller does not pile them up.
        if (Interlocked.Exchange(ref _sending, 1) == 1)
        {
            if (input)
            {
                NoteInput();
            }

            return;
        }

        try
        {
            var state = await _client.Alpaca.HoldLeaseAsync(input, _stopping.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _state = state;
                _held = true;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (RoofApiException refusal) when (refusal.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            lock (_gate)
            {
                _refused = true;
                _timer?.Dispose();
                _timer = null;
            }

            _logger.LogInformation("The controller does not give this credential the operator lease ({Code}); none is held", refusal.CodeText ?? ((int)refusal.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or OperationCanceledException or RoofProtocolException)
        {
            if (input)
            {
                NoteInput();
            }

            _logger.LogDebug(ex, "Operator lease heartbeat failed; the next one tries again");
        }
        finally
        {
            Volatile.Write(ref _sending, 0);
        }
    }

    /// <summary>Stops the heartbeats and releases the lease (best effort: it lapses by itself otherwise).</summary>
    public async ValueTask DisposeAsync()
    {
        bool held;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            held = _held && !_refused;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (held)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _client.Alpaca.ReleaseLeaseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or OperationCanceledException or RoofProtocolException)
            {
                _logger.LogDebug(ex, "Releasing the operator lease failed; it lapses by itself");
            }
        }

        _stopping.Dispose();
    }
}
