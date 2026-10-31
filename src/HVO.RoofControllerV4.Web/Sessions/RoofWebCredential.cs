using System.Net;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// A person signed in to the web UI: their controller session (<c>Authorization: Bearer</c>) on every request. Stop also
/// carries the web UI's own Stop key, naming the person in <c>X-On-Behalf-Of</c>; the controller tries the session first
/// and falls back to the key only when it refuses the session, so Stop still works after the session ended. When the
/// controller refuses the session, it is marked ended. Every request names the address of the person's browser in
/// <c>X-Forwarded-For</c> (<see cref="ForwardFor"/>), so the controller's command history says where the person was; the
/// controller believes it only from the web UI's own connection.
/// </summary>
public sealed class RoofWebCredential : RoofCredential
{
    private readonly KeyValuePair<string, string>[] _stopHeaders;
    private volatile string? _forwardedFor;

    public RoofWebCredential(RoofSessionCredential session, string? stopKey)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        if (stopKey is null)
        {
            _stopHeaders = [.. session.GetHeaders(RoofCredentialUse.Stop)];
            return;
        }

        RequireHeaderValue(stopKey, nameof(stopKey));
        _stopHeaders = session.Name is { } name && RoofIdentityContract.IsValidName(name)
            ? [.. session.GetHeaders(RoofCredentialUse.Stop), ApiKeyHeader(stopKey), new(RoofIdentityContract.OnBehalfOfHeaderName, name)]
            : [.. session.GetHeaders(RoofCredentialUse.Stop), ApiKeyHeader(stopKey)];
    }

    /// <summary>The person's controller session.</summary>
    public RoofSessionCredential Session { get; }

    /// <summary>True when Stop also carries the web UI's Stop key.</summary>
    public bool HasStopKey => _stopHeaders.Length > 1;

    /// <summary>The address of the person's browser that the requests name, or null for none.</summary>
    public string? ForwardedFor => _forwardedFor;

    /// <summary>
    /// Names <paramref name="browser"/> (the address the person's last request to the web UI came from) as the address the
    /// following requests are sent for. Only the address that connected: a header the browser sent is never passed on.
    /// </summary>
    public void ForwardFor(IPAddress? browser)
        => _forwardedFor = browser is null ? null : (browser.IsIPv4MappedToIPv6 ? browser.MapToIPv4() : browser).ToString();

    public override IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use)
    {
        var headers = use == RoofCredentialUse.Stop ? _stopHeaders : Session.GetHeaders(use);
        return _forwardedFor is { } browser
            ? [.. headers, new(RoofCommandHistoryContract.ForwardedForHeaderName, browser)]
            : headers;
    }

    // A refused Stop means the session and the Stop key were both refused (the controller tries the key only when it
    // refuses the session), so the session has ended either way.
    protected override bool OnRefused(RoofCredentialUse use)
    {
        Session.End();
        return false;
    }

    public override string ToString() => $"web UI {Session}";
}
