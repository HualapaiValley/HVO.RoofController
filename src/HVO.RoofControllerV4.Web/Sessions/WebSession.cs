using System.Net;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// A person signed in to the web UI: their controller session and a client of the controller that uses it. Every page
/// the person has open shares it. The controller decides what they may do; the web UI only mirrors their role to choose
/// what to offer. Their pages share one status feed (the controller allows a few hub connections per session).
/// </summary>
public sealed class WebSession : IDisposable
{
    private readonly TimeProvider _time;
    private readonly RoofControllerConnector _connector;
    private readonly Lock _feedGate = new();
    private RoofStatusFeed? _feed;
    private int _feedHolds;
    private long _endedAtTicks;
    private long _stopsSent;
    private int _disposed;

    internal WebSession(RoofSessionCredential session, WebStopKey stopKey, RoofControllerConnector connector, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = session.SessionId ?? throw new ArgumentException("The session has no identifier.", nameof(session));
        Name = session.Name ?? throw new ArgumentException("The session names nobody.", nameof(session));
        Role = WebRoles.Normalize(session.Role) ?? throw new ArgumentException("The session's role is not one the web UI knows.", nameof(session));
        ExpiresUtc = session.ExpiresUtc ?? throw new ArgumentException("The session has no expiry.", nameof(session));
        _time = time;
        _connector = connector;
        Credential = new RoofWebCredential(session, stopKey.Value);
        Client = connector.Create(Credential, RequestTimeout);
        session.Ended += OnEnded;
    }

    /// <summary>How long a person's request may take before the page says the controller did not answer.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>The controller's identifier of the session (not a secret).</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>The highest role, one of <see cref="Common.Models.RoofControllerApiContract"/>'s role names.</summary>
    public string Role { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public RoofWebCredential Credential { get; }

    /// <summary>The person's client of the controller.</summary>
    public RoofControllerClient Client { get; }

    /// <summary>True once the controller refused the session or the person signed out.</summary>
    public bool IsEnded => Credential.Session.IsEnded;

    /// <summary>When the session ended, or null while it is open.</summary>
    public DateTimeOffset? EndedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _endedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Raised once, when the session ends.</summary>
    public event EventHandler? Ended;

    /// <summary>How many Stops were sent for the person, from any of their pages.</summary>
    public long StopsSent => Interlocked.Read(ref _stopsSent);

    /// <summary>Raised as each Stop for the person is sent, before its answer (on the sender's thread).</summary>
    public event EventHandler? StopSent;

    /// <summary>Raised with the answer to each Stop for the person.</summary>
    public event EventHandler<RoofStopResult>? StopAnswered;

    /// <summary>True while the session has neither ended nor expired.</summary>
    public bool IsOpen => !IsEnded && _time.GetUtcNow() < ExpiresUtc;

    /// <summary>Marks the session ended (after sign-out).</summary>
    public void End() => Credential.Session.End();

    /// <summary>
    /// Sends Stop for the person: with their session, and the web UI's Stop key when the controller refuses it, so Stop
    /// still works after the session ended. Every page of the person hears of it (<see cref="StopSent"/>), so a lease
    /// is dropped and an Open or Close that crossed it is stopped again. Never throws for a refusal or a network error.
    /// </summary>
    public async Task<RoofStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopsSent);
        StopSent?.Invoke(this, EventArgs.Empty);
        RoofStopResult result;
        try
        {
            result = await Client.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The session's client was closed a while after the session ended; Stop is sent on a client of its own.
            using var client = _connector.Create(Credential, RequestTimeout);
            result = await client.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        // The controller refused the session and the Stop key (or there is none): the person has to sign in again.
        if (result.Error is RoofApiException { StatusCode: HttpStatusCode.Unauthorized })
        {
            result = result with { Message = RoofStopText.PageSignedOut };
        }

        StopAnswered?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// The person's status feed, started with the first hold and closed when the last hold is released (or the session is
    /// closed).
    /// </summary>
    public WebStatusFeedHold HoldStatusFeed()
    {
        lock (_feedGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_feed is null)
            {
                _feed = Client.CreateStatusFeed();
                _feed.Start();
            }

            _feedHolds++;
            return new WebStatusFeedHold(this, _feed);
        }
    }

    internal void Release(RoofStatusFeed feed)
    {
        lock (_feedGate)
        {
            if (!ReferenceEquals(feed, _feed) || --_feedHolds > 0)
            {
                return;
            }

            _feed = null;
        }

        CloseInBackground(feed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Credential.Session.Ended -= OnEnded;
            RoofStatusFeed? feed;
            lock (_feedGate)
            {
                feed = _feed;
                _feed = null;
                _feedHolds = 0;
            }

            if (feed is not null)
            {
                CloseInBackground(feed);
            }

            Client.Dispose();
        }
    }

    public override string ToString() => $"web session {Id} for {Name}";

    // Closing a feed waits for its hub connection to end; nobody waits for that.
    private static void CloseInBackground(RoofStatusFeed feed)
        => _ = feed.DisposeAsync().AsTask().ContinueWith(
            closing => _ = closing.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void OnEnded(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _endedAtTicks, _time.GetUtcNow().UtcTicks, 0);
        Ended?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A page's hold on its person's status feed; disposing it releases the hold.</summary>
public sealed class WebStatusFeedHold : IDisposable
{
    private WebSession? _session;

    internal WebStatusFeedHold(WebSession session, RoofStatusFeed feed)
    {
        _session = session;
        Feed = feed;
    }

    /// <summary>The shared feed. It is already started; the holder never stops or disposes it.</summary>
    public RoofStatusFeed Feed { get; }

    public void Dispose() => Interlocked.Exchange(ref _session, null)?.Release(Feed);
}
