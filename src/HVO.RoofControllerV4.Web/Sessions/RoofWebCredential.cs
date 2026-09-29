using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// A person signed in to the web UI: their controller session (<c>Authorization: Bearer</c>) on every request. Stop also
/// carries the web UI's own Stop key, naming the person in <c>X-On-Behalf-Of</c>; the controller tries the session first
/// and falls back to the key only when it refuses the session, so Stop still works after the session ended. When the
/// controller refuses the session, it is marked ended.
/// </summary>
public sealed class RoofWebCredential : RoofCredential
{
    private readonly KeyValuePair<string, string>[] _stopHeaders;

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

    public override IReadOnlyList<KeyValuePair<string, string>> GetHeaders(RoofCredentialUse use)
        => use == RoofCredentialUse.Stop ? _stopHeaders : Session.GetHeaders(use);

    // A refused Stop means the session and the Stop key were both refused (the controller tries the key only when it
    // refuses the session), so the session has ended either way.
    protected override bool OnRefused(RoofCredentialUse use)
    {
        Session.End();
        return false;
    }

    public override string ToString() => $"web UI {Session}";
}
