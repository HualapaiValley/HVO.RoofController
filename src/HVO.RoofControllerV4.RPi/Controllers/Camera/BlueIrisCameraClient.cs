using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Controllers.Camera;

/// <summary>
/// Typed <see cref="HttpClient"/> for the Blue Iris MJPEG endpoint. Registered through <c>IHttpClientFactory</c> with a
/// finite connect timeout (see <c>Program</c>); response-header and idle timeouts are enforced by the caller with
/// cancellation tokens because the stream itself is unbounded. Credentials are read from configuration per request so
/// a rotated password is picked up without a restart.
/// </summary>
public sealed class BlueIrisCameraClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<BlueIrisOptions> _options;

    public BlueIrisCameraClient(HttpClient httpClient, IOptionsMonitor<BlueIrisOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Requests the MJPEG stream for <paramref name="cameraId"/> and returns once response headers arrive. The caller
    /// owns (and must dispose) the response.
    /// </summary>
    public async Task<HttpResponseMessage> OpenMjpegStreamAsync(int cameraId, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var baseUri = options.GetBaseUri()
            ?? throw new InvalidOperationException("BlueIris:BaseUrl is not configured.");

        var requestUri = new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"/mjpg/cam{cameraId:D2}/video.mjpg"));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrEmpty(options.UserName) && !string.IsNullOrEmpty(options.Password))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }
}
