using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>What the Alpaca server forwards about its client with a command (#220): reported, never trusted by the controller.</summary>
/// <param name="ClientId">The Alpaca client's <c>ClientID</c>.</param>
/// <param name="ClientTransactionId">Its <c>ClientTransactionID</c>.</param>
/// <param name="Address">Its address as the Alpaca server saw it.</param>
/// <param name="AuthMethod">How it sent its key: <c>header</c> or <c>basic</c>.</param>
public sealed record RoofAlpacaForwarding(uint ClientId, uint ClientTransactionId, string Address, string AuthMethod)
{
    internal IReadOnlyDictionary<string, string> Headers() => new Dictionary<string, string>
    {
        [RoofAlpacaContract.ClientIdHeader] = ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [RoofAlpacaContract.ClientTransactionIdHeader] = ClientTransactionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [RoofAlpacaContract.ClientAddressHeader] = Address,
        [RoofAlpacaContract.AuthMethodHeader] = AuthMethod
    };
}

/// <summary>
/// The controller's Alpaca command surface (<c>api/v4.0/alpaca</c>, #220). The UIs use the person-only routes (override
/// grants, the human Stop hold, the operator lease, alerts) with the person's own session; the controller refuses every
/// API key there. The Alpaca server uses the ticket, Open and Close routes with the Alpaca client's own key, passed on for
/// that one call and never kept, and reports its own refusals with its own key.
/// </summary>
public sealed class RoofAlpacaApi
{
    private readonly RoofHttp _http;

    internal RoofAlpacaApi(RoofHttp http) => _http = http;

    /// <summary>The override grants in force. Viewer role.</summary>
    public Task<IReadOnlyList<RoofOverrideGrant>> GetGrantsAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<IReadOnlyList<RoofOverrideGrant>>(RoofApiRoutes.AlpacaGrants, cancellationToken);

    /// <summary>
    /// Grants an override for one command of <paramref name="kind"/>, lifting only <paramref name="reasonKeys"/> (the
    /// reasons the person was shown). A person's session with the override role.
    /// </summary>
    public Task<RoofOverrideGrant> GrantAsync(RoofAlpacaCommandKind kind, IReadOnlyList<string> reasonKeys, CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofOverrideGrant>(HttpMethod.Post, RoofApiRoutes.AlpacaGrants, new RoofOverrideGrantRequest(kind, reasonKeys), cancellationToken);

    /// <summary>Revokes the grant of <paramref name="kind"/>. A person's session.</summary>
    public Task RevokeAsync(RoofAlpacaCommandKind kind, CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.AlpacaGrant(kind), null, cancellationToken);

    /// <summary>Clears the human Stop hold, so Alpaca commands may run again. A person's session with the Operator role.</summary>
    public Task<RoofStopHoldState> ClearHoldAsync(CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofStopHoldState>(HttpMethod.Delete, RoofApiRoutes.AlpacaHold, null, cancellationToken);

    /// <summary>
    /// Takes or keeps the operator lease (a heartbeat); <paramref name="input"/> when the person did something since the
    /// last one. A person's session with the Operator role.
    /// </summary>
    public Task<RoofOperatorLeaseState> HoldLeaseAsync(bool input, CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofOperatorLeaseState>(HttpMethod.Post, RoofApiRoutes.AlpacaLeaseHeartbeat(input), null, cancellationToken);

    /// <summary>Gives the operator lease up. A person's session.</summary>
    public Task ReleaseLeaseAsync(CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.AlpacaLease, null, cancellationToken);

    /// <summary>The alerts, newest first. Viewer role.</summary>
    public Task<IReadOnlyList<RoofAlpacaAlert>> GetAlertsAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<IReadOnlyList<RoofAlpacaAlert>>(RoofApiRoutes.AlpacaAlerts, cancellationToken);

    /// <summary>Acknowledges an alert. A person's session.</summary>
    public Task<RoofAlpacaAlert> AcknowledgeAlertAsync(string id, CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofAlpacaAlert>(HttpMethod.Post, RoofApiRoutes.AcknowledgeAlpacaAlert(id), null, cancellationToken);

    /// <summary>
    /// A ticket for one Alpaca command, asked for with the Alpaca client's own key (<paramref name="clientKey"/>, the
    /// External Alpaca account's), passed on for this call only.
    /// </summary>
    public Task<RoofAlpacaTicket> RequestTicketAsync(RoofAlpacaCommandKind kind, RoofCredential clientKey, RoofAlpacaForwarding client, TimeSpan timeout, CancellationToken cancellationToken = default)
        => SendWithAsync<RoofAlpacaTicket>($"{RoofApiRoutes.AlpacaTicket}?kind={kind}", null, clientKey, client, timeout, cancellationToken);

    /// <summary>An Alpaca Close carrying its ticket, with the Alpaca client's own key passed on for this call only.</summary>
    public Task<RoofStatusResponse> CloseAsync(RoofAlpacaCommandRequest request, RoofCredential clientKey, RoofAlpacaForwarding client, TimeSpan timeout, CancellationToken cancellationToken = default)
        => SendWithAsync<RoofStatusResponse>(RoofApiRoutes.AlpacaClose, request, clientKey, client, timeout, cancellationToken);

    /// <summary>An Alpaca Open carrying its ticket, with the Alpaca client's own key passed on for this call only (#220, phase 4).</summary>
    public Task<RoofStatusResponse> OpenAsync(RoofAlpacaCommandRequest request, RoofCredential clientKey, RoofAlpacaForwarding client, TimeSpan timeout, CancellationToken cancellationToken = default)
        => SendWithAsync<RoofStatusResponse>(RoofApiRoutes.AlpacaOpen, request, clientKey, client, timeout, cancellationToken);

    /// <summary>Reports the Alpaca server's own refusal of a command, with this client's own key (the Alpaca server's account).</summary>
    public Task ReportEventAsync(RoofAlpacaEvent report, CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Post, RoofApiRoutes.AlpacaEvents, report, cancellationToken);

    private async Task<T> SendWithAsync<T>(string path, object? body, RoofCredential clientKey, RoofAlpacaForwarding client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        ArgumentNullException.ThrowIfNull(client);
        using var response = await _http.SendAsync(HttpMethod.Post, path, body, RoofCredentialUse.Request, cancellationToken,
            timeout: timeout, credentialOverride: clientKey, headers: client.Headers()).ConfigureAwait(false);
        return await RoofHttp.ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
