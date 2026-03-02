using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Asp.Versioning;
using System.IO;
using System.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Controllers
{
    /// <summary>
    /// Roof Controller API v4.0 - Controls the observatory roof operations
    /// </summary>
    [ApiController, ApiVersion("1.0"), Produces("application/json")]
    [Route("api/v{version:apiVersion}/Camera")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [Tags("Camera Control")]
    public class CameraController : ControllerBase
    {
        private const string BlueIrisBase = "http://192.168.0.4:80";

        private readonly ILogger<CameraController> _logger;

        public CameraController(ILogger<CameraController> logger)
        {
            this._logger = logger;
        }

        private static HttpClient CreateBlueIrisClient()
        {
            var h = new HttpClientHandler();
            var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
            var basic = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("roys:salisbury"));
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            return c;
        }

        // MJPEG passthrough. Example URL: /api/v1.0/camera/cam02/mjpeg
        [HttpGet("{cameraId:int:range(1, 99)}/mjpeg")]
        public async Task<IActionResult> CameraMotionJpeg(int cameraId)
        {
            var cancellationToken = HttpContext.RequestAborted;

            try
            {
                using var http = CreateBlueIrisClient();
                using var upstream = await http.GetAsync($"{BlueIrisBase}/mjpg/cam{cameraId:D2}/video.mjpg", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!upstream.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Camera stream upstream returned non-success status {StatusCode} for camera {CameraId}", (int)upstream.StatusCode, cameraId);
                    return StatusCode((int)upstream.StatusCode);
                }

                // Forward Content-Type EXACTLY as-is (don’t “fix” boundary)
                if (upstream.Content.Headers.TryGetValues("Content-Type", out var ct))
                {
                    Response.Headers["Content-Type"] = ct.ToArray();
                }

                // Helpful streaming headers
                Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["Expires"] = "0";
                Response.Headers["X-Accel-Buffering"] = "no";

                // Copy the upstream bytes to the client; don't dispose early
                await using var s = await upstream.Content.ReadAsStreamAsync(cancellationToken);
                await s.CopyToAsync(Response.Body, cancellationToken);

                return new EmptyResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Camera stream cancelled by client for camera {CameraId}", cameraId);
                return new EmptyResult();
            }
            catch (IOException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace(ex, "Camera stream ended due to client disconnect for camera {CameraId}", cameraId);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying camera stream for camera {CameraId}", cameraId);
                return Problem(title: "Camera Stream Error", detail: "The camera stream could not be proxied.", statusCode: StatusCodes.Status502BadGateway);
            }
        }
    }
}