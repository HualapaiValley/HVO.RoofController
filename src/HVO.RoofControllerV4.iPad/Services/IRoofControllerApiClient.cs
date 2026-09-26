using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Abstraction for interacting with the Roof Controller Web API. Every failure is a
/// <see cref="RoofControllerApiException"/> that says whether the controller refused the request or could not be
/// reached. Only idempotent reads are retried; Open, Close, ClearFault, lease renewal and configuration changes are
/// sent once.
/// </summary>
public interface IRoofControllerApiClient
{
    Task<Result<RoofStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> OpenAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one Stop request to <paramref name="endpoint"/> with the given timeout. It is not cancelled when the
    /// settings change and is not retried here; <see cref="StopCommandCoordinator"/> owns the deadline and retry.
    /// </summary>
    Task<Result<RoofStatusResponse>> StopAsync(RoofControllerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> ClearFaultAsync(int pulseMs, CancellationToken cancellationToken = default);

    Task<Result<RoofStatusResponse>> RenewLeaseAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofConfigurationResponse>> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<Result<RoofConfigurationResponse>> UpdateConfigurationAsync(RoofConfigurationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads <c>/health</c>. An unhealthy report (HTTP 503 with a health body) is returned as a report, not a failure.</summary>
    Task<Result<HealthReportPayload>> GetHealthReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests a short-lived ticket for the controller's camera proxy.</summary>
    Task<Result<CameraStreamTicketResponse>> CreateCameraTicketAsync(string cameraId, CancellationToken cancellationToken = default);
}
