using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Roof commands and status (<c>api/v4.0/RoofControl</c>). A refused command throws <see cref="RoofApiException"/>
/// carrying the controller's code and its status snapshot. Stop is on <see cref="RoofControllerClient.StopAsync"/>.
/// </summary>
public sealed class RoofControlApi
{
    private readonly RoofHttp _http;

    internal RoofControlApi(RoofHttp http) => _http = http;

    public Task<RoofStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofStatusResponse>(RoofApiRoutes.Status, cancellationToken);

    /// <summary>Starts opening. Operator role.</summary>
    public Task<RoofStatusResponse> OpenAsync(CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofStatusResponse>(HttpMethod.Post, RoofApiRoutes.Open, null, cancellationToken);

    /// <summary>Starts closing. Operator role.</summary>
    public Task<RoofStatusResponse> CloseAsync(CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofStatusResponse>(HttpMethod.Post, RoofApiRoutes.Close, null, cancellationToken);

    /// <summary>Renews the operator lease on leased motion. Operator role; refused with LeaseNotActive when nothing is leased.</summary>
    public Task<RoofStatusResponse> RenewLeaseAsync(CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofStatusResponse>(HttpMethod.Post, RoofApiRoutes.Lease, null, cancellationToken);

    /// <summary>
    /// Pulses the drive's fault-reset relay. Operator role. <paramref name="pulseMilliseconds"/> defaults to the
    /// controller's default and must lie within <see cref="RoofControllerLimits"/>.
    /// </summary>
    public Task<RoofStatusResponse> ClearFaultAsync(int? pulseMilliseconds = null, CancellationToken cancellationToken = default)
    {
        var path = pulseMilliseconds is { } pulse
            ? $"{RoofApiRoutes.ClearFault}?pulseMs={pulse.ToString(CultureInfo.InvariantCulture)}"
            : RoofApiRoutes.ClearFault;
        return _http.SendAsync<RoofStatusResponse>(HttpMethod.Post, path, null, cancellationToken);
    }

    /// <summary>The controller's roof configuration. Admin role.</summary>
    public Task<RoofConfigurationResponse> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofConfigurationResponse>(RoofApiRoutes.Configuration, cancellationToken);

    /// <summary>Changes the roof configuration. Admin role; refused with ConfigurationVersionConflict when it changed since it was read.</summary>
    public Task<RoofConfigurationResponse> UpdateConfigurationAsync(RoofConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _http.SendAsync<RoofConfigurationResponse>(HttpMethod.Post, RoofApiRoutes.Configuration, request, cancellationToken);
    }
}
