using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// A person signed in to the web UI: their controller session and a client of the controller that uses it. Every page
/// the person has open shares it. The controller decides what they may do; the web UI only mirrors their role to choose
/// what to offer.
/// </summary>
public sealed class WebSession : IDisposable
{
    private readonly TimeProvider _time;
    private long _endedAtTicks;
    private int _disposed;

    internal WebSession(RoofSessionCredential session, WebStopKey stopKey, RoofControllerConnector connector, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = session.SessionId ?? throw new ArgumentException("The session has no identifier.", nameof(session));
        Name = session.Name ?? throw new ArgumentException("The session names nobody.", nameof(session));
        Role = WebRoles.Normalize(session.Role) ?? throw new ArgumentException("The session's role is not one the web UI knows.", nameof(session));
        ExpiresUtc = session.ExpiresUtc ?? throw new ArgumentException("The session has no expiry.", nameof(session));
        _time = time;
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

    /// <summary>True while the session has neither ended nor expired.</summary>
    public bool IsOpen => !IsEnded && _time.GetUtcNow() < ExpiresUtc;

    /// <summary>Marks the session ended (after sign-out).</summary>
    public void End() => Credential.Session.End();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Credential.Session.Ended -= OnEnded;
            Client.Dispose();
        }
    }

    public override string ToString() => $"web session {Id} for {Name}";

    private void OnEnded(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _endedAtTicks, _time.GetUtcNow().UtcTicks, 0);
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
