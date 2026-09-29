using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Remote configuration (<c>api/v4.0/Settings</c>): the catalogue that describes every setting, the current values, and
/// changes by group. <see cref="RoofSettingsForm"/> turns the first two into editable fields.
/// </summary>
public sealed class RoofSettingsApi
{
    private readonly RoofHttp _http;

    internal RoofSettingsApi(RoofHttp http) => _http = http;

    /// <summary>Every setting the caller may read: type, limits, roles and safety.</summary>
    public Task<RoofSettingsCatalogueResponse> GetCatalogueAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofSettingsCatalogueResponse>(RoofApiRoutes.SettingsCatalogue, cancellationToken);

    /// <summary>The current values, the version to send back with a change, and any pending hand edit.</summary>
    public Task<RoofSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofSettingsResponse>(RoofApiRoutes.Settings, cancellationToken);

    /// <summary>
    /// Changes settings in one group. Refused with ConfigurationVersionConflict when the settings changed since
    /// <see cref="RoofSettingsUpdateRequest.ExpectedVersion"/>.
    /// </summary>
    public Task<RoofSettingsResponse> UpdateAsync(string group, RoofSettingsUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofSettingsResponse>(HttpMethod.Post, RoofApiRoutes.SettingsGroup(group), request, cancellationToken);
    }

    /// <summary>Applies a hand edit of the settings file. Admin role.</summary>
    public Task<RoofSettingsResponse> ApplyHandEditAsync(RoofSettingsHandEditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofSettingsResponse>(HttpMethod.Post, RoofApiRoutes.SettingsReload, request, cancellationToken);
    }

    /// <summary>Discards a hand edit of the settings file, restoring the saved settings. Admin role.</summary>
    public Task<RoofSettingsResponse> DiscardHandEditAsync(RoofSettingsHandEditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofSettingsResponse>(HttpMethod.Post, RoofApiRoutes.SettingsDiscard, request, cancellationToken);
    }
}
