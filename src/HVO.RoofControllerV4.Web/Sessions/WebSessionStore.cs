using System.Collections.Concurrent;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// The people signed in to the web UI, by their controller session's identifier. Kept in memory only: the session's
/// token lives in the person's sign-in cookie (encrypted and signed by the web UI's data protection keys), so after the
/// web UI restarts a session is picked up again from the cookie at the person's next request. A session that ended is
/// remembered until it would have expired, so its cookie cannot bring it back.
/// </summary>
public sealed class WebSessionStore : IDisposable
{
    /// <summary>How long an ended session's client is kept for requests already under way, before it is closed.</summary>
    public static TimeSpan EndedGrace { get; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, WebSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly WebStopKey _stopKey;
    private readonly RoofControllerConnector _connector;
    private readonly TimeProvider _time;
    private readonly ILogger<WebSessionStore> _logger;
    private readonly ITimer _sweeper;

    public WebSessionStore(WebStopKey stopKey, RoofControllerConnector connector, TimeProvider time, ILogger<WebSessionStore> logger)
    {
        _stopKey = stopKey;
        _connector = connector;
        _time = time;
        _logger = logger;
        _sweeper = time.CreateTimer(_ => Sweep(), null, EndedGrace, EndedGrace);
    }

    /// <summary>The sessions held, open or ended (tests).</summary>
    internal int Count => _sessions.Count;

    /// <summary>Holds a session that has just signed in.</summary>
    public WebSession Open(RoofSessionCredential session)
    {
        var opened = new WebSession(session, _stopKey, _connector, _time);
        lock (_gate)
        {
            if (_sessions.TryGetValue(opened.Id, out var previous))
            {
                previous.Dispose();
            }

            _sessions[opened.Id] = opened;
        }

        return opened;
    }

    /// <summary>
    /// The open session a sign-in cookie names, picking it up again from the cookie when the web UI does not hold it
    /// (after a restart). False when the session ended or expired.
    /// </summary>
    public bool TryResume(WebTicket ticket, out WebSession session)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (_sessions.TryGetValue(ticket.SessionId, out session!))
        {
            return session.IsOpen;
        }

        if (_time.GetUtcNow() >= ticket.ExpiresUtc)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(ticket.SessionId, out session!))
            {
                session = new WebSession(
                    new RoofSessionCredential(ticket.Token, ticket.Name, ticket.Role, ticket.SessionId, ticket.ExpiresUtc),
                    _stopKey,
                    _connector,
                    _time);
                _sessions[session.Id] = session;
                _logger.LogInformation("Web session for {Name} ({Role}) picked up again from its cookie", session.Name, session.Role);
            }
        }

        return session.IsOpen;
    }

    /// <summary>The open session with this identifier.</summary>
    public bool TryGet(string? sessionId, out WebSession session)
    {
        session = null!;
        return sessionId is not null && _sessions.TryGetValue(sessionId, out session!) && session.IsOpen;
    }

    /// <summary>Closes the clients of sessions that ended a while ago, and forgets sessions that have expired.</summary>
    internal void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, session) in _sessions)
        {
            if (now >= session.ExpiresUtc)
            {
                if (_sessions.TryRemove(new KeyValuePair<string, WebSession>(id, session)))
                {
                    session.Dispose();
                }
            }
            else if (session.EndedAt is { } ended && now - ended >= EndedGrace)
            {
                session.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }
}

/// <summary>What a sign-in cookie holds: the controller session, including its token.</summary>
public sealed record WebTicket(string SessionId, string Token, string Name, string Role, DateTimeOffset ExpiresUtc)
{
    // The token is left out, so the record can be logged.
    public override string ToString() => $"WebTicket {{ SessionId = {SessionId}, Name = {Name}, Role = {Role}, ExpiresUtc = {ExpiresUtc:O} }}";
}
