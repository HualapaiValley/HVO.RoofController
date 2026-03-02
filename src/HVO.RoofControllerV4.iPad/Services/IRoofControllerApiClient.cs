using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Abstraction for interacting with the Roof Controller Web API.
/// </summary>
public interface IRoofControllerApiClient
{
    Task<Result<RoofStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> OpenAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> CloseAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> StopAsync(CancellationToken cancellationToken = default);

    Task<Result<bool>> ClearFaultAsync(int? pulseMs = null, CancellationToken cancellationToken = default);

    Task<Result<RoofConfigurationResponse>> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofConfigurationResponse>> UpdateConfigurationAsync(RoofConfigurationRequest request, CancellationToken cancellationToken = default);

    Task<Result<HealthReportPayload>> GetHealthReportAsync(CancellationToken cancellationToken = default);
}
