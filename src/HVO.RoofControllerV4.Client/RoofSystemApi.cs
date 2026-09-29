using System.Net;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>Restart and the host's information and metrics (admin).</summary>
public sealed class RoofSystemApi
{
    private readonly RoofHttp _http;

    internal RoofSystemApi(RoofHttp http) => _http = http;

    /// <summary>
    /// Asks the controller to restart. Accepted (202) once the roof is stopped; the process then exits with
    /// <see cref="RoofSettingsContract.RestartExitCode"/> and its supervisor starts it again. Refused with RestartRefused
    /// while the roof moves.
    /// </summary>
    public Task<RoofRestartResponse> RestartAsync(bool confirmSafetyCriticalChange = false, CancellationToken cancellationToken = default)
        => _http.SendAsync<RoofRestartResponse>(
            HttpMethod.Post,
            RoofApiRoutes.Restart,
            new RoofRestartRequest { ConfirmSafetyCriticalChange = confirmSafetyCriticalChange },
            cancellationToken);

    public Task<SystemInformationResponse> GetInformationAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<SystemInformationResponse>(RoofApiRoutes.SystemInformation, cancellationToken);

    public Task<SystemRuntimeMetricsResponse> GetMetricsAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<SystemRuntimeMetricsResponse>(RoofApiRoutes.SystemMetrics, cancellationToken);
}

/// <summary>The answer to an anonymous health probe: its HTTP status and the status text (Healthy, Degraded or Unhealthy).</summary>
public sealed record RoofProbeResult(HttpStatusCode StatusCode, string Status)
{
    public bool IsHealthy => StatusCode == HttpStatusCode.OK;
}

/// <summary>The controller's health endpoints.</summary>
public sealed class RoofHealthApi
{
    private readonly RoofHttp _http;

    internal RoofHealthApi(RoofHttp http) => _http = http;

    /// <summary>The detailed report (Viewer role). An unhealthy controller answers 503 with the report, which is returned too.</summary>
    public async Task<HealthReportPayload> GetReportAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.SendAsync(
            HttpMethod.Get,
            RoofApiRoutes.Health,
            null,
            RoofCredentialUse.Request,
            cancellationToken,
            alsoAccept: HttpStatusCode.ServiceUnavailable).ConfigureAwait(false);
        return await RoofHttp.ReadAsync<HealthReportPayload>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Readiness (anonymous): healthy when the hardware checks pass.</summary>
    public Task<RoofProbeResult> GetReadinessAsync(CancellationToken cancellationToken = default)
        => ProbeAsync(RoofApiRoutes.HealthReady, cancellationToken);

    /// <summary>Liveness (anonymous): healthy whenever the process answers.</summary>
    public Task<RoofProbeResult> GetLivenessAsync(CancellationToken cancellationToken = default)
        => ProbeAsync(RoofApiRoutes.HealthLive, cancellationToken);

    private async Task<RoofProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(
            HttpMethod.Get,
            path,
            null,
            use: null,
            cancellationToken,
            alsoAccept: HttpStatusCode.ServiceUnavailable).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new RoofProbeResult(response.StatusCode, text.Trim());
    }
}

/// <summary>A camera's MJPEG stream. Dispose it to close the connection.</summary>
public sealed class RoofCameraStream : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;

    internal RoofCameraStream(HttpResponseMessage response, Stream content)
    {
        _response = response;
        Content = content;
        ContentType = response.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
    }

    /// <summary>The media type with its boundary, for example <c>multipart/x-mixed-replace; boundary=frame</c>.</summary>
    public string ContentType { get; }

    /// <summary>The stream of multipart JPEG frames, as the camera sends it.</summary>
    public Stream Content { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }

    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }
}

/// <summary>The camera proxy (<c>api/v1.0/Camera</c>).</summary>
public sealed class RoofCameraApi
{
    public const int MinimumCameraId = 1;
    public const int MaximumCameraId = 99;

    private readonly RoofHttp _http;

    internal RoofCameraApi(RoofHttp http) => _http = http;

    /// <summary>
    /// Opens a camera's MJPEG stream. The request timeout covers only the answer's headers; the stream then runs until
    /// it is disposed or <paramref name="cancellationToken"/> is cancelled. An unreachable camera answers 502, 503 or 504.
    /// </summary>
    public async Task<RoofCameraStream> OpenAsync(int cameraId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cameraId, MinimumCameraId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cameraId, MaximumCameraId);
        var response = await _http.SendAsync(
            HttpMethod.Get,
            RoofApiRoutes.Camera(cameraId),
            null,
            RoofCredentialUse.Request,
            cancellationToken,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        try
        {
            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new RoofCameraStream(response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
