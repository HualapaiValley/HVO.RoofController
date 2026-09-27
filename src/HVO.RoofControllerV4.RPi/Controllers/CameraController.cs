using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using Asp.Versioning;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Camera proxy API v1.0: relays the Blue Iris MJPEG stream so clients never see the Blue Iris credentials.
/// </summary>
[ApiController, ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/Camera")]
[Tags("Camera Control")]
public sealed class CameraController : ControllerBase
{
    private readonly ILogger<CameraController> _logger;
    private readonly BlueIrisCameraClient _cameraClient;
    private readonly CameraStreamLimiter _limiter;
    private readonly IOptionsMonitor<BlueIrisOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;

    public CameraController(
        ILogger<CameraController> logger,
        BlueIrisCameraClient cameraClient,
        CameraStreamLimiter limiter,
        IOptionsMonitor<BlueIrisOptions> options,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cameraClient = cameraClient ?? throw new ArgumentNullException(nameof(cameraClient));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>
    /// Streams the camera's MJPEG feed. Accepts a Viewer API key or a signed-in console cookie.
    /// </summary>
    /// <param name="cameraId">Camera number (1-99).</param>
    /// <response code="200">multipart/x-mixed-replace MJPEG stream.</response>
    /// <response code="401">No valid API key or console sign-in.</response>
    /// <response code="502">Blue Iris failed or answered with an error.</response>
    /// <response code="503">The proxy is not configured, or too many streams are open.</response>
    /// <response code="504">Blue Iris did not answer in time.</response>
    [HttpGet("{cameraId:int:range(1, 99)}/mjpeg", Name = nameof(CameraMotionJpeg))]
    [Authorize(AuthenticationSchemes = RoofSecurityServiceCollectionExtensions.ApiKeyOrCookieSchemes, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> CameraMotionJpeg(int cameraId)
    {
        var options = _options.CurrentValue;
        var configurationProblem = options.GetConfigurationProblem();
        if (configurationProblem is not null)
        {
            return CameraProblem(StatusCodes.Status503ServiceUnavailable, "Camera proxy unavailable", configurationProblem);
        }

        using var lease = _limiter.TryAcquire();
        if (lease is null)
        {
            _logger.LogWarning("Refused camera {CameraId} stream: {MaxStreams} streams already open", cameraId, _limiter.MaxStreams);
            Response.Headers.RetryAfter = "5";
            return CameraProblem(
                StatusCodes.Status503ServiceUnavailable,
                "Too many camera streams",
                $"At most {_limiter.MaxStreams} camera streams can be open at once. Close another viewer and retry.");
        }

        // Ends the stream when the viewer disconnects or the host starts shutting down, so an open viewer never holds
        // up the graceful shutdown (and with it the roof stop).
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _lifetime.ApplicationStopping);
        var streamToken = streamCancellation.Token;

        HttpResponseMessage? upstream = null;
        try
        {
            using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(streamToken))
            {
                headersTimeout.CancelAfter(options.ResponseHeadersTimeout);
                try
                {
                    upstream = await _cameraClient.OpenMjpegStreamAsync(cameraId, headersTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!streamToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Camera {CameraId}: Blue Iris did not answer within {Timeout}", cameraId, options.ResponseHeadersTimeout);
                    return CameraProblem(StatusCodes.Status504GatewayTimeout, "Camera timeout", "The camera server did not respond in time.");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("Camera {CameraId}: Blue Iris request failed: {Error}", cameraId, ex.Message);
                    return CameraProblem(StatusCodes.Status502BadGateway, "Camera unavailable", "The camera server could not be reached.");
                }
            }

            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning("Camera {CameraId}: Blue Iris answered {StatusCode}", cameraId, (int)upstream.StatusCode);
                return CameraProblem(
                    StatusCodes.Status502BadGateway,
                    "Camera unavailable",
                    $"The camera server answered HTTP {(int)upstream.StatusCode}.");
            }

            await using var source = await upstream.Content.ReadAsStreamAsync(streamToken).ConfigureAwait(false);

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Expires = "0";
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers["X-Accel-Buffering"] = "no";

            _logger.LogDebug("Camera {CameraId} stream started for {Caller}", cameraId, RoofPrincipalFactory.DescribeCaller(User));
            await CopyWithIdleTimeoutAsync(source, Response.Body, options.StreamIdleTimeout, streamToken).ConfigureAwait(false);
            return new EmptyResult();
        }
        catch (Exception ex) when (streamToken.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            _logger.LogDebug(
                "Camera {CameraId} stream ended ({Reason})",
                cameraId,
                _lifetime.ApplicationStopping.IsCancellationRequested ? "host stopping" : "viewer disconnected");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            if (Response.HasStarted)
            {
                // Headers and frames were already sent: a ProblemDetails body cannot be written any more. Abort the
                // connection so the viewer sees a failed stream instead of a silently frozen frame.
                _logger.LogWarning("Camera {CameraId} stream failed after it started: {Error}", cameraId, ex.Message);
                HttpContext.Abort();
                return new EmptyResult();
            }

            _logger.LogError(ex, "Camera {CameraId} stream failed", cameraId);
            return CameraProblem(StatusCodes.Status502BadGateway, "Camera stream error", "The camera stream could not be relayed.");
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>; throws <see cref="TimeoutException"/> when no
    /// data arrives for <paramref name="idleTimeout"/>.
    /// </summary>
    internal static async Task CopyWithIdleTimeoutAsync(Stream source, Stream destination, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                idle.CancelAfter(idleTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No camera data received for {idleTimeout}.");
                }

                if (read == 0)
                {
                    return;
                }

                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ObjectResult CameraProblem(int statusCode, string title, string detail)
        => Problem(statusCode: statusCode, title: title, detail: detail);
}
