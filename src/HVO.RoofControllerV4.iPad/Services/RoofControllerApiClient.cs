using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.iPad.Configuration;
using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Typed HTTP client for interacting with the Roof Controller Web API.
/// </summary>
public sealed class RoofControllerApiClient : IRoofControllerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly RoofControllerApiOptions _options;
    private readonly ILogger<RoofControllerApiClient> _logger;

    private static readonly JsonSerializerOptions JsonSerializerOptions = CreateSerializerOptions();

    public RoofControllerApiClient(HttpClient httpClient, IOptions<RoofControllerApiOptions> options, ILogger<RoofControllerApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        EnsureBaseAddress();
    }

    public Task<Result<RoofStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default)
        => SendRequestAsync<RoofStatusResponse>(HttpMethod.Get, "roofcontrol/status", cancellationToken: cancellationToken);

    public Task<Result<RoofStatusResponse>> OpenAsync(CancellationToken cancellationToken = default)
        => SendRequestAsync<RoofStatusResponse>(HttpMethod.Get, "roofcontrol/open", cancellationToken: cancellationToken);

    public Task<Result<RoofStatusResponse>> CloseAsync(CancellationToken cancellationToken = default)
        => SendRequestAsync<RoofStatusResponse>(HttpMethod.Get, "roofcontrol/close", cancellationToken: cancellationToken);

    public Task<Result<RoofStatusResponse>> StopAsync(CancellationToken cancellationToken = default)
        => SendRequestAsync<RoofStatusResponse>(HttpMethod.Get, "roofcontrol/stop", cancellationToken: cancellationToken);

    public Task<Result<bool>> ClearFaultAsync(int? pulseMs = null, CancellationToken cancellationToken = default)
    {
        var query = pulseMs.HasValue ? $"clearfault?pulseMs={pulseMs.Value}" : "clearfault";
        return SendRequestAsync<bool>(HttpMethod.Post, $"roofcontrol/{query}", cancellationToken: cancellationToken);
    }

    public Task<Result<RoofConfigurationResponse>> GetConfigurationAsync(CancellationToken cancellationToken = default)
        => SendRequestAsync<RoofConfigurationResponse>(HttpMethod.Get, "roofcontrol/configuration", cancellationToken: cancellationToken);

    public Task<Result<RoofConfigurationResponse>> UpdateConfigurationAsync(RoofConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = JsonContent.Create(request, options: JsonSerializerOptions);
        return SendRequestAsync<RoofConfigurationResponse>(HttpMethod.Post, "roofcontrol/configuration", content, cancellationToken);
    }

    public Task<Result<HealthReportPayload>> GetHealthReportAsync(CancellationToken cancellationToken = default)
    {
        var root = GetRootBaseUri();
        var healthUri = new Uri(root, "health");
        return SendRequestAsync<HealthReportPayload>(HttpMethod.Get, healthUri.ToString(), cancellationToken: cancellationToken);
    }

    private async Task<Result<T>> SendRequestAsync<T>(HttpMethod method, string requestUri, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);

        var attempt = 0;
        var maxAttempts = Math.Max(1, _options.RequestRetryCount);

        while (true)
        {
            attempt++;
            Uri? requestUriObject = null;

            try
            {
                requestUriObject = CreateRequestUri(requestUri);

                using var request = new HttpRequestMessage(method, requestUriObject)
                {
                    Content = content
                };

                _logger.LogDebug("Sending {Method} request to {Endpoint} (attempt {Attempt}/{MaxAttempts})", method, requestUriObject, attempt, maxAttempts);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await TryReadProblemDetailsAsync(response, cancellationToken).ConfigureAwait(false);
                    var exception = new HttpRequestException($"API request to '{requestUriObject}' failed with {(int)response.StatusCode} {response.ReasonPhrase}: {errorText}");
                    _logger.LogError(exception, "Roof controller API call failed");
                    return Result<T>.Failure(exception);
                }

                if (response.Content.Headers.ContentLength == 0)
                {
                    if (typeof(T) == typeof(bool))
                    {
                        return Result<T>.Success((T)(object)true);
                    }

                    var noContent = new InvalidOperationException($"API response from '{requestUriObject}' was empty.");
                    _logger.LogError(noContent, "Unexpected empty response.");
                    return Result<T>.Failure(noContent);
                }

                var result = await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    var nullContent = new InvalidOperationException($"API response from '{requestUriObject}' could not be deserialized to {typeof(T).Name}.");
                    _logger.LogError(nullContent, "Failed to deserialize API response");
                    return Result<T>.Failure(nullContent);
                }

                return Result<T>.Success(result);
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts || cancellationToken.IsCancellationRequested)
                {
                    var endpoint = requestUriObject?.ToString() ?? requestUri;
                    _logger.LogError(ex, "Unhandled error calling Roof Controller API {Method} {Endpoint} after {Attempts} attempts", method, endpoint, attempt);
                    return Result<T>.Failure(ex);
                }

                var delay = TimeSpan.FromMilliseconds(Math.Min(500 * attempt, 2_000));
                var retryEndpoint = requestUriObject?.ToString() ?? requestUri;
                _logger.LogWarning(ex, "Error calling Roof Controller API {Method} {Endpoint} on attempt {Attempt}. Retrying in {Delay}ms", method, retryEndpoint, attempt, delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result<T>.Failure(ex);
                }
            }
        }
    }

    private async Task<string> TryReadProblemDetailsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return "<no error body>";
            }

            try
            {
                var problem = JsonSerializer.Deserialize<ProblemDetailsPayload>(content, JsonSerializerOptions);
                if (problem is not null)
                {
                    return problem.Detail ?? problem.Title ?? content;
                }
            }
            catch (JsonException)
            {
                // ignored, we fall back to raw content
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read problem details from API response");
            return "<unable to read error body>";
        }
    }

    private void EnsureBaseAddress()
    {
        if (_httpClient.BaseAddress is not null)
        {
            return;
        }

        var baseUri = _options.GetBaseUri();
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
        }

        _httpClient.BaseAddress = baseUri;
    }

    private Uri CreateRequestUri(string requestUri)
    {
        if (Uri.TryCreate(requestUri, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (_httpClient.BaseAddress is null)
        {
            EnsureBaseAddress();
        }

        return new Uri(_httpClient.BaseAddress!, requestUri);
    }

    private Uri GetRootBaseUri()
    {
        var baseUri = _httpClient.BaseAddress ?? _options.GetBaseUri();
        var builder = new UriBuilder(baseUri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private sealed record ProblemDetailsPayload(string? Title, string? Detail);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));

        return options;
    }
}
