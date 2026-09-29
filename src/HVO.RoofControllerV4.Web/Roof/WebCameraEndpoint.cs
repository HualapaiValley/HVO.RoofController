using System.Globalization;
using System.Net;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>
/// <c>GET /camera/{id}/mjpeg</c>: a camera's MJPEG stream for the roof page's player, relayed from the controller's
/// camera proxy with the person's session, so the page reads it from its own origin and never holds a credential of the
/// controller. Only the cameras the page shows (<see cref="RoofWebOptions.CameraIds"/>) are relayed. The stream ends
/// with the person's session (signed out, ended at the controller, or expired) and when the web UI stops.
/// </summary>
public static class WebCameraEndpoint
{
    /// <summary>Where the camera streams are, under the web UI's root.</summary>
    public const string PathPrefix = "/camera";

    /// <summary>A camera's stream, relative to the page's base address.</summary>
    public static string StreamPath(int cameraId) => $"{PathPrefix.TrimStart('/')}/{cameraId.ToString(CultureInfo.InvariantCulture)}/mjpeg";

    public static IEndpointRouteBuilder MapWebCameraEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(PathPrefix + "/{cameraId:int:range(1, 99)}/mjpeg", StreamAsync)
            .RequireAuthorization(policy => policy.RequireRole(WebRoles.Viewer))
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> StreamAsync(
        HttpContext http,
        int cameraId,
        WebSessionStore store,
        IOptions<RoofWebOptions> options,
        IHostApplicationLifetime lifetime,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WebCameraEndpoint).FullName!);
        if (!options.Value.Cameras.Contains(cameraId))
        {
            return Results.NotFound();
        }

        if (!store.TryGet(WebAuthentication.GetSessionId(http.User), out var session))
        {
            return Results.Unauthorized();
        }

        using var expiry = new CancellationTokenSource(TimeUntil(session.ExpiresUtc, time), time);
        using var ends = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping, expiry.Token);
        void OnSessionEnded(object? sender, EventArgs e)
        {
            try
            {
                ends.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stream already ended.
            }
        }

        session.Ended += OnSessionEnded;
        try
        {
            if (session.IsEnded)
            {
                return Results.Unauthorized();
            }

            RoofCameraStream stream;
            try
            {
                stream = await session.Client.Camera.OpenAsync(cameraId, ends.Token);
            }
            catch (RoofApiException refusal)
            {
                // A 401 ends the person's session (their client's credential hears of it), and their pages say so. The
                // controller's 502, 503 and 504 describe the camera server and are passed on, as is a Retry-After.
                logger.LogWarning("Camera {CameraId} for {Name}: the controller answered {StatusCode}", cameraId, session.Name, (int)refusal.StatusCode);
                if (refusal.RetryAfter is { } retryAfter)
                {
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return refusal.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => Results.Unauthorized(),
                    HttpStatusCode.ServiceUnavailable => Problem(StatusCodes.Status503ServiceUnavailable, "Camera unavailable", refusal.Detail),
                    HttpStatusCode.GatewayTimeout => Problem(StatusCodes.Status504GatewayTimeout, "Camera timeout", refusal.Detail),
                    _ => Problem(StatusCodes.Status502BadGateway, "Camera unavailable", refusal.Detail),
                };
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Camera {CameraId} for {Name}: the controller did not answer in time", cameraId, session.Name);
                return Problem(StatusCodes.Status504GatewayTimeout, "Camera timeout", "The roof controller did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Camera {CameraId} for {Name}: the controller could not be reached: {Error}", cameraId, session.Name, ex.Message);
                return Problem(StatusCodes.Status502BadGateway, "Camera unavailable", "The roof controller could not be reached.");
            }
            catch (Exception ex) when (ends.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
            {
                // The viewer left, the web UI is stopping, or the session ended.
                return http.RequestAborted.IsCancellationRequested ? Results.Empty
                    : lifetime.ApplicationStopping.IsCancellationRequested ? Problem(StatusCodes.Status503ServiceUnavailable, "Camera unavailable", "The web UI is stopping.")
                    : Results.Unauthorized();
            }
            catch (ObjectDisposedException)
            {
                // The person signed out while the stream opened.
                return Results.Unauthorized();
            }

            await using (stream)
            {
                await RelayAsync(http, cameraId, session, stream, ends.Token, logger);
            }

            return Results.Empty;
        }
        finally
        {
            session.Ended -= OnSessionEnded;
        }
    }

    private static async Task RelayAsync(HttpContext http, int cameraId, WebSession session, RoofCameraStream stream, CancellationToken ends, ILogger logger)
    {
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = stream.ContentType ?? "multipart/x-mixed-replace";
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
        response.Headers["X-Accel-Buffering"] = "no";

        logger.LogDebug("Camera {CameraId} stream started for {Name}", cameraId, session.Name);
        try
        {
            // The headers go at once (a test server sends them only with a flush), so the player says it is waiting for
            // the first frame rather than connecting.
            await response.StartAsync(ends);
            await response.Body.FlushAsync(ends);
            await stream.Content.CopyToAsync(response.Body, ends);
            logger.LogDebug("Camera {CameraId} stream for {Name} ended by the controller", cameraId, session.Name);
        }
        catch (Exception ex) when (ends.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
        {
            if (!http.RequestAborted.IsCancellationRequested)
            {
                // The session ended or the web UI is stopping: the viewer's stream is cut, and the player says so.
                logger.LogDebug("Camera {CameraId} stream for {Name} ended with the session or the web UI", cameraId, session.Name);
                http.Abort();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            // The controller's stream broke off (it aborts one that fails after it started): the viewer's is aborted too,
            // so the player sees a failed stream instead of a frozen frame.
            logger.LogWarning("Camera {CameraId} stream for {Name} broke off: {Error}", cameraId, session.Name, ex.Message);
            http.Abort();
        }
    }

    private static TimeSpan TimeUntil(DateTimeOffset expiresUtc, TimeProvider time)
    {
        var left = expiresUtc - time.GetUtcNow();
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private static IResult Problem(int statusCode, string title, string? detail)
        => Results.Problem(statusCode: statusCode, title: title, detail: detail);
}
