using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// People, API keys and sessions (<c>api/v4.0/Identity</c>). Admin role; a kiosk PIN session is refused with
/// CredentialNotAllowed. Names are escaped into one path segment.
/// </summary>
public sealed class RoofIdentityApi
{
    private readonly RoofHttp _http;

    internal RoofIdentityApi(RoofHttp http) => _http = http;

    public Task<RoofUserResponse[]> GetUsersAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofUserResponse[]>(RoofApiRoutes.Users, cancellationToken);

    public Task<RoofUserResponse> GetUserAsync(string name, CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofUserResponse>(RoofApiRoutes.User(name), cancellationToken);

    public Task<RoofUserResponse> AddUserAsync(RoofUserCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofUserResponse>(HttpMethod.Post, RoofApiRoutes.Users, request, cancellationToken);
    }

    public Task<RoofUserResponse> UpdateUserAsync(string name, RoofUserUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofUserResponse>(HttpMethod.Put, RoofApiRoutes.User(name), request, cancellationToken);
    }

    /// <summary>Removes a person and ends their sessions.</summary>
    public Task RemoveUserAsync(string name, CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.User(name), null, cancellationToken);

    public Task<RoofApiKeyResponse[]> GetApiKeysAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofApiKeyResponse[]>(RoofApiRoutes.ApiKeys, cancellationToken);

    /// <summary>Adds a key. The secret is in the answer only; it cannot be read again.</summary>
    public Task<RoofApiKeySecretResponse> AddApiKeyAsync(RoofApiKeyCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofApiKeySecretResponse>(HttpMethod.Post, RoofApiRoutes.ApiKeys, request, cancellationToken);
    }

    public Task<RoofApiKeyResponse> UpdateApiKeyAsync(string name, RoofApiKeyUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofApiKeyResponse>(HttpMethod.Put, RoofApiRoutes.ApiKey(name), request, cancellationToken);
    }

    /// <summary>Replaces a key's secret. The old secret stops working at once; the new one is in the answer only.</summary>
    public Task<RoofApiKeySecretResponse> RotateApiKeyAsync(string name, CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofApiKeySecretResponse>(HttpMethod.Post, RoofApiRoutes.RotateApiKey(name), null, cancellationToken);

    public Task RemoveApiKeyAsync(string name, CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.ApiKey(name), null, cancellationToken);

    public Task<RoofSessionInfoResponse[]> GetSessionsAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofSessionInfoResponse[]>(RoofApiRoutes.Sessions, cancellationToken);

    /// <summary>Ends someone's session.</summary>
    public Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.IdentitySession(sessionId), null, cancellationToken);
}
