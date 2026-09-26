using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Typed HTTP client for the Roof Controller Web API. Requests go to the endpoint held by
/// <see cref="RoofControllerConnection"/>; changing the endpoint cancels requests to the previous controller
/// (except Stop, which is always allowed to finish).
/// </summary>
public sealed class RoofControllerApiClient : IRoofControllerApiClient
{
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];

    private readonly HttpClient _httpClient;
    private readonly RoofControllerConnection _connection;
    private readonly ILogger<RoofControllerApiClient> _logger;

    public RoofControllerApiClient(HttpClient httpClient, RoofControllerConnection connection, ILogger<RoofControllerApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<Result<RoofStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetWithRetryAsync<RoofStatusResponse>("Status", e => e.RoofControlUri("Status"), StatusTimeout, cancellationToken);

    public Task<Result<RoofStatusResponse>> OpenAsync(CancellationToken cancellationToken = default)
        => PostOnceAsync<RoofStatusResponse>("Open", e => e.RoofControlUri("Open"), null, CommandTimeout, cancellationToken);

    public Task<Result<RoofStatusResponse>> CloseAsync(CancellationToken cancellationToken = default)
        => PostOnceAsync<RoofStatusResponse>("Close", e => e.RoofControlUri("Close"), null, CommandTimeout, cancellationToken);

    public Task<Result<RoofStatusResponse>> StopAsync(RoofControllerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return SendOnceAsync<RoofStatusResponse>(endpoint, "Stop", HttpMethod.Post, endpoint.RoofControlUri("Stop"), null, timeout, CancellationToken.None, cancellationToken);
    }

    public Task<Result<RoofStatusResponse>> ClearFaultAsync(int pulseMs, CancellationToken cancellationToken = default)
    {
        var pulse = Math.Clamp(pulseMs, RoofControllerLimits.MinClearFaultPulseMilliseconds, RoofControllerLimits.MaxClearFaultPulseMilliseconds);
        var query = "ClearFault?pulseMs=" + pulse.ToString(CultureInfo.InvariantCulture);
        return PostOnceAsync<RoofStatusResponse>("Clear fault", e => e.RoofControlUri(query), null, CommandTimeout + TimeSpan.FromMilliseconds(pulse), cancellationToken);
    }

    public Task<Result<RoofStatusResponse>> RenewLeaseAsync(CancellationToken cancellationToken = default)
        => PostOnceAsync<RoofStatusResponse>("Lease renewal", e => e.RoofControlUri("Lease"), null, LeaseTimeout, cancellationToken);

    public Task<Result<RoofConfigurationResponse>> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => GetWithRetryAsync<RoofConfigurationResponse>("Configuration load", e => e.RoofControlUri("Configuration"), StatusTimeout, cancellationToken);

    public Task<Result<RoofConfigurationResponse>> UpdateConfigurationAsync(RoofConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostOnceAsync<RoofConfigurationResponse>(
            "Configuration save",
            e => e.RoofControlUri("Configuration"),
            () => JsonContent.Create(request, options: RoofControllerJson.Options),
            CommandTimeout,
            cancellationToken);
    }

    public Task<Result<HealthReportPayload>> GetHealthReportAsync(CancellationToken cancellationToken = default)
        => GetWithRetryAsync<HealthReportPayload>("Health check", e => e.HealthUri, StatusTimeout, cancellationToken, ReadUnhealthyReport);

    public Task<Result<CameraStreamTicketResponse>> CreateCameraTicketAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        return PostOnceAsync<CameraStreamTicketResponse>("Camera ticket", e => CameraStreamResolver.GetTicketEndpoint(e.RootUri, cameraId), null, CommandTimeout, cancellationToken);
    }

    private async Task<Result<T>> GetWithRetryAsync<T>(
        string operation,
        Func<RoofControllerEndpoint, Uri> uriFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<int, string, T?>? errorBodyReader = null)
        where T : class
    {
        var connection = _connection.Snapshot();
        if (connection.Endpoint is not { } endpoint)
        {
            return NotConfigured<T>();
        }

        var uri = uriFactory(endpoint);
        var attempts = endpoint.RequestAttempts;

        for (var attempt = 1; ; attempt++)
        {
            var result = await SendOnceAsync(endpoint, operation, HttpMethod.Get, uri, null, timeout, connection.Cancellation, cancellationToken, errorBodyReader).ConfigureAwait(false);
            if (result.IsSuccessful || attempt >= attempts)
            {
                return result;
            }

            var failure = RoofControllerApiException.From(result.Error);
            if (!failure.IsRetryableForIdempotentRequest)
            {
                return result;
            }

            var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
            _logger.LogDebug("{Operation} attempt {Attempt}/{Attempts} failed ({Kind}); retrying in {Delay} ms", operation, attempt, attempts, failure.Kind, delay.TotalMilliseconds);

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Cancellation);
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                return new RoofControllerApiException(RoofControllerFailureKind.Cancelled, $"{operation} was cancelled.", ex);
            }
        }
    }

    private Task<Result<T>> PostOnceAsync<T>(string operation, Func<RoofControllerEndpoint, Uri> uriFactory, Func<HttpContent>? contentFactory, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        var connection = _connection.Snapshot();
        if (connection.Endpoint is not { } endpoint)
        {
            return Task.FromResult(NotConfigured<T>());
        }

        return SendOnceAsync<T>(endpoint, operation, HttpMethod.Post, uriFactory(endpoint), contentFactory, timeout, connection.Cancellation, cancellationToken);
    }

    private async Task<Result<T>> SendOnceAsync<T>(
        RoofControllerEndpoint endpoint,
        string operation,
        HttpMethod method,
        Uri uri,
        Func<HttpContent>? contentFactory,
        TimeSpan timeout,
        CancellationToken connectionCancellation,
        CancellationToken cancellationToken,
        Func<int, string, T?>? errorBodyReader = null)
        where T : class
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCancellation, timeoutSource.Token);

        try
        {
            using var request = new HttpRequestMessage(method, uri);
            if (contentFactory is not null)
            {
                request.Content = contentFactory();
            }

            if (endpoint.HasApiKey)
            {
                request.Headers.TryAddWithoutValidation(RoofControllerApiContract.ApiKeyHeaderName, endpoint.ApiKey);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                if (errorBodyReader?.Invoke(status, body) is { } alternate)
                {
                    return alternate;
                }

                var failure = RoofControllerProblemParser.CreateHttpFailure(status, response.ReasonPhrase, body, operation);
                _logger.LogWarning("{Operation} {Method} {Uri} returned HTTP {Status} {Code}", operation, method, uri.GetLeftPart(UriPartial.Path), status, failure.RawCode);
                return failure;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return InvalidResponse<T>(operation, "the response body was empty");
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(body, RoofControllerJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "{Operation} response could not be parsed", operation);
                return InvalidResponse<T>(operation, "the response body could not be parsed", ex);
            }

            return value is null ? InvalidResponse<T>(operation, "the response body was empty") : value;
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested || connectionCancellation.IsCancellationRequested)
            {
                return new RoofControllerApiException(RoofControllerFailureKind.Cancelled, $"{operation} was cancelled before an answer arrived.", ex);
            }

            _logger.LogWarning("{Operation} {Method} {Uri} timed out after {Timeout} ms", operation, method, uri.GetLeftPart(UriPartial.Path), timeout.TotalMilliseconds);
            return new RoofControllerApiException(RoofControllerFailureKind.Timeout, $"{operation} timed out after {timeout.TotalSeconds:0.#} s.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning("{Operation} {Method} {Uri} failed: {Error}", operation, method, uri.GetLeftPart(UriPartial.Path), ex.Message);
            return new RoofControllerApiException(RoofControllerFailureKind.Transport, ex.Message, ex);
        }
    }

    private static HealthReportPayload? ReadUnhealthyReport(int statusCode, string body)
    {
        if (statusCode != 503 || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HealthReportPayload>(body, RoofControllerJson.Options);
            return report is not null && !string.IsNullOrWhiteSpace(report.Status) ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Result<T> NotConfigured<T>()
        => new RoofControllerApiException(RoofControllerFailureKind.NotConfigured, "no valid controller URL is configured.");

    private static Result<T> InvalidResponse<T>(string operation, string reason, Exception? inner = null)
        => new RoofControllerApiException(RoofControllerFailureKind.InvalidResponse, $"{operation}: {reason}.", inner);
}
