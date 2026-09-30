using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>What a request is for, so a credential can choose the headers it sends.</summary>
public enum RoofCredentialUse
{
    /// <summary>An ordinary REST request.</summary>
    Request,

    /// <summary>Stop. The controller accepts a refused session's device key for Stop, so a kiosk sends both.</summary>
    Stop,

    /// <summary>The status hub, which only needs to read.</summary>
    StatusHub,

    /// <summary>A request made as the device itself: PIN sign-in and the PIN user list.</summary>
    Device
}

/// <summary>
/// How a client proves who it is. Credentials are sent in headers only (never in a URL), and <see cref="ToString"/>
/// never includes a secret.
/// </summary>
public abstract class RoofCredential
{
    /// <summary>The headers to send for a request of the given kind.</summary>
    public abstract IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use);

    /// <summary>
    /// Called when the controller refused this credential (HTTP 401). Returns true when the credential changed so that a
    /// retry may succeed; a kiosk, for example, drops an expired PIN session and falls back to its device key.
    /// </summary>
    protected internal virtual bool OnRefused(RoofCredentialUse use) => false;

    /// <summary>Why a key or token cannot be used: a header cannot carry it.</summary>
    public const string InvalidHeaderValue =
        "A key or token can hold only printable ASCII characters: no line break, control character or character outside ASCII.";

    protected static KeyValuePair<string, string> ApiKeyHeader(string key) => new(RoofControllerApiContract.ApiKeyHeaderName, key);

    /// <summary>
    /// Refuses a key or token that a header cannot carry, when the credential is made. Otherwise every request, Stop
    /// included, would fail as it is sent, and read as a controller that cannot be reached.
    /// </summary>
    protected static void RequireHeaderValue(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException(InvalidHeaderValue, paramName);
        }
    }

    protected static KeyValuePair<string, string> BearerHeader(string token) => new("Authorization", $"{RoofIdentityContract.BearerScheme} {token}");
}

/// <summary>An API key (<c>X-Api-Key</c>), optionally naming the person it acts for (<c>X-On-Behalf-Of</c>, audit only).</summary>
public sealed class RoofApiKeyCredential : RoofCredential
{
    private readonly KeyValuePair<string, string>[] _headers;

    public RoofApiKeyCredential(string apiKey, string? onBehalfOf = null)
    {
        RequireHeaderValue(apiKey, nameof(apiKey));
        OnBehalfOf = string.IsNullOrWhiteSpace(onBehalfOf) ? null : onBehalfOf.Trim();
        if (OnBehalfOf is not null && !RoofIdentityContract.IsValidName(OnBehalfOf))
        {
            throw new ArgumentException(InvalidOnBehalfOf, nameof(onBehalfOf));
        }

        _headers = OnBehalfOf is null
            ? [ApiKeyHeader(apiKey)]
            : [ApiKeyHeader(apiKey), new(RoofIdentityContract.OnBehalfOfHeaderName, OnBehalfOf)];
    }

    /// <summary>Why a name cannot be sent as <c>X-On-Behalf-Of</c>: it must be a user name, as the controller defines one.</summary>
    public const string InvalidOnBehalfOf =
        "The on-behalf-of name must be a user name: up to 64 characters, a letter or digit, then letters, digits, '.', '_', '@' or '-'.";

    public string? OnBehalfOf { get; }

    public override IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use) => _headers;

    public override string ToString() => OnBehalfOf is null ? "API key" : $"API key on behalf of {OnBehalfOf}";
}

/// <summary>
/// A signed-in person's session (<c>Authorization: Bearer</c>). The controller ends a session on sign-out, expiry,
/// idle timeout or a credential change; the first refusal marks it <see cref="IsEnded"/> and raises
/// <see cref="Ended"/> so the client can ask the person to sign in again.
/// </summary>
public sealed class RoofSessionCredential : RoofCredential
{
    private readonly KeyValuePair<string, string>[] _headers;
    private int _ended;

    public RoofSessionCredential(RoofSessionResponse session)
        : this(session?.Token!, session?.Name, session?.Role, session?.SessionId, session?.ExpiresUtc)
    {
    }

    public RoofSessionCredential(string token, string? name = null, string? role = null, string? sessionId = null, DateTimeOffset? expiresUtc = null)
    {
        RequireHeaderValue(token, nameof(token));
        Token = token;
        Name = name;
        Role = role;
        SessionId = sessionId;
        ExpiresUtc = expiresUtc;
        _headers = [BearerHeader(token)];
    }

    /// <summary>The bearer token. A secret: store it as carefully as a password.</summary>
    public string Token { get; }

    public string? Name { get; private set; }

    public string? Role { get; private set; }

    public string? SessionId { get; private set; }

    public DateTimeOffset? ExpiresUtc { get; private set; }

    public bool IsEnded => Volatile.Read(ref _ended) != 0;

    /// <summary>Raised once, when the controller first refuses the session or it is signed out.</summary>
    public event EventHandler? Ended;

    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresUtc is { } expires && nowUtc >= expires;

    public override IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use) => _headers;

    /// <summary>Updates the name, role and expiry from <c>GET Auth/Me</c>.</summary>
    public void Update(RoofCallerResponse caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Name = caller.Name;
        Role = caller.Role;
        SessionId = caller.SessionId ?? SessionId;
        ExpiresUtc = caller.ExpiresUtc ?? ExpiresUtc;
    }

    /// <summary>Marks the session ended (after sign-out, or when the controller refused it).</summary>
    public void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    protected internal override bool OnRefused(RoofCredentialUse use)
    {
        End();
        return false;
    }

    public override string ToString() => Name is null ? "session" : $"session for {Name}";
}

/// <summary>
/// A kiosk: a device key (<c>X-Api-Key</c>) that is always sent, plus the session of the person who unlocked it with a
/// PIN. Requests carry both; the status hub and PIN sign-in use the device key alone. When the controller refuses the
/// PIN session, it is dropped and <see cref="PinSessionChanged"/> is raised, and the device key still works, so Stop and
/// the status view keep working when a PIN session times out. The Mac app uses it the same way, with the session of the
/// person who signed in with their password (<see cref="RoofAuthApi.SignInOnDeviceAsync"/>).
/// </summary>
public sealed class RoofKioskCredential : RoofCredential
{
    private readonly KeyValuePair<string, string> _deviceHeader;
    private readonly object _gate = new();
    private RoofSessionResponse? _pinSession;
    private KeyValuePair<string, string>[] _requestHeaders;

    public RoofKioskCredential(string deviceKey)
    {
        RequireHeaderValue(deviceKey, nameof(deviceKey));
        _deviceHeader = ApiKeyHeader(deviceKey);
        _requestHeaders = [_deviceHeader];
    }

    /// <summary>The PIN session in use, or null while the kiosk is locked.</summary>
    public RoofSessionResponse? PinSession
    {
        get
        {
            lock (_gate)
            {
                return _pinSession;
            }
        }
    }

    /// <summary>Raised when a PIN session starts or ends; the argument is the new session, or null when it ended.</summary>
    public event EventHandler<RoofSessionResponse?>? PinSessionChanged;

    public void UsePinSession(RoofSessionResponse session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHeaderValue(session.Token, nameof(session));
        lock (_gate)
        {
            _pinSession = session;
            _requestHeaders = [_deviceHeader, BearerHeader(session.Token)];
        }

        PinSessionChanged?.Invoke(this, session);
    }

    /// <summary>Locks the kiosk: drops the PIN session. Returns false when none was active.</summary>
    public bool EndPinSession()
    {
        lock (_gate)
        {
            if (_pinSession is null)
            {
                return false;
            }

            _pinSession = null;
            _requestHeaders = [_deviceHeader];
        }

        PinSessionChanged?.Invoke(this, null);
        return true;
    }

    public override IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use)
    {
        if (use is RoofCredentialUse.StatusHub or RoofCredentialUse.Device)
        {
            return [_deviceHeader];
        }

        lock (_gate)
        {
            return _requestHeaders;
        }
    }

    protected internal override bool OnRefused(RoofCredentialUse use)
        => use is RoofCredentialUse.Request or RoofCredentialUse.Stop && EndPinSession();

    public override string ToString()
    {
        var session = PinSession;
        return session is null ? "kiosk (locked)" : $"kiosk unlocked by {session.Name}";
    }
}
