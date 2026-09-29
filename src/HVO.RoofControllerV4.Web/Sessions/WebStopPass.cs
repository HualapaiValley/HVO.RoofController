using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>Who a Stop pass was issued to: the person, their role and session, and when the pass runs out.</summary>
public sealed record WebStopPassHolder(string Name, string Role, string SessionId, DateTimeOffset NotAfter);

/// <summary>
/// The Stop pass: a second cookie, issued at sign-in when the web UI has a Stop key (<see cref="RoofWebOptions.StopKeyFile"/>),
/// that keeps a person's Stop working after their session has ended. The browser sends it only with <c>POST /stop</c>
/// (HttpOnly, SameSite=Strict, its path is <c>/stop</c>). It holds no controller token, only who the person is,
/// protected by the web UI's data protection keys. When the sign-in cookie has gone (its session ended and a page was
/// sent to the sign-in page), <c>/stop</c> sends the person's Stop with the Stop key on their behalf until the pass runs
/// out, <see cref="RoofWebOptions.StopAfterSessionHours"/> after the session would have expired. Signing out removes it;
/// signing in again replaces it.
/// </summary>
public sealed class WebStopPass
{
    /// <summary>The cookie.</summary>
    public const string CookieName = "hvo.roof.web.stop";

    private const string Purpose = "HVO.RoofControllerV4.Web.StopPass.v1";

    private readonly IDataProtector _protector;
    private readonly WebStopKey _stopKey;
    private readonly IOptions<RoofWebOptions> _options;
    private readonly TimeProvider _time;

    public WebStopPass(IDataProtectionProvider protection, WebStopKey stopKey, IOptions<RoofWebOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector(Purpose);
        _stopKey = stopKey;
        _options = options;
        _time = time;
    }

    /// <summary>True when the web UI has a Stop key, so a pass can send Stop.</summary>
    public bool IsAvailable => _stopKey.Value is not null;

    /// <summary>
    /// True when a person's Stop works after their session has ended, whether it was ended or expired: there is a Stop
    /// key and <see cref="RoofWebOptions.StopAfterSessionHours"/> is above 0. The pages say so only then.
    /// </summary>
    public bool OutlastsSessions => IsAvailable && _options.Value.StopAfterSessionHours > 0;

    /// <summary>Gives the browser a pass for <paramref name="session"/>, replacing any it has; removes it when there is no Stop key.</summary>
    public void Issue(HttpContext http, WebSession session)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        if (!IsAvailable)
        {
            Remove(http);
            return;
        }

        var notAfter = session.ExpiresUtc + TimeSpan.FromHours(_options.Value.StopAfterSessionHours);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(session.Name, session.Role, session.Id, notAfter.ToUnixTimeSeconds()));
        http.Response.Cookies.Append(CookieName, WebEncoders.Base64UrlEncode(_protector.Protect(payload)), CreateCookieOptions(http));
    }

    /// <summary>Removes the browser's pass (signing out).</summary>
    public void Remove(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(CookieName, CreateCookieOptions(http));
    }

    /// <summary>The holder of this request's pass, or null when it has none, it is not the web UI's, or it has run out.</summary>
    public WebStopPassHolder? Read(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(WebEncoders.Base64UrlDecode(value)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }

        if (payload is null
            || string.IsNullOrEmpty(payload.Name)
            || string.IsNullOrEmpty(payload.SessionId)
            || WebRoles.Normalize(payload.Role) is not { } role)
        {
            return null;
        }

        var notAfter = DateTimeOffset.FromUnixTimeSeconds(payload.NotAfter);
        return _time.GetUtcNow() < notAfter ? new WebStopPassHolder(payload.Name, role, payload.SessionId, notAfter) : null;
    }

    private static CookieOptions CreateCookieOptions(HttpContext http) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = http.Request.IsHttps,
        Path = http.Request.PathBase.Add(WebAuthentication.StopPostPath).Value,
        IsEssential = true,
    };

    private sealed record Payload(string Name, string Role, string SessionId, long NotAfter);
}
