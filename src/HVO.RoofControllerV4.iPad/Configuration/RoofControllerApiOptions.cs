using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Configuration;

/// <summary>
/// Strongly-typed configuration for communicating with the Roof Controller Web API.
/// </summary>
public sealed class RoofControllerApiOptions : IValidatableObject
{
    /// <summary>
    /// Base URL for the Roof Controller Web API (e.g. https://observatory.local:7151/api/v4.0/). Only http and https
    /// are accepted; https is recommended because the API key is sent with every request.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Poll interval, in seconds, for refreshing roof status from the API.
    /// </summary>
    public int StatusPollIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Optional public URL for the roof camera stream.
    /// </summary>
    public string? CameraStreamUrl { get; set; }

    /// <summary>
    /// Default pulse duration used when sending a Clear Fault request.
    /// </summary>
    public int ClearFaultPulseMs { get; set; } = 250;

    /// <summary>
    /// Optional watchdog timeout used for UI progress calculations, when reported by configuration.
    /// </summary>
    public double? SafetyWatchdogTimeoutSeconds { get; set; }

    /// <summary>
    /// Total attempts (1-3) for idempotent requests (status, health, configuration reads). Open, Close, ClearFault and
    /// configuration changes are never replayed automatically; Stop has its own bounded retry.
    /// </summary>
    public int RequestRetryCount { get; set; } = 3;

    /// <summary>
    /// Consecutive polling failures before prompting the operator with recovery options.
    /// </summary>
    public int ConnectionFailurePromptThreshold { get; set; } = 3;

    /// <summary>Upper bound for <see cref="RequestRetryCount"/>.</summary>
    public const int MaxRequestAttempts = 3;

    /// <summary>
    /// Parses an absolute http:// or https:// URI. Any other scheme (file, ftp, javascript, ...) is rejected.
    /// </summary>
    public static bool TryParseHttpUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            && (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(candidate.Host))
        {
            uri = candidate;
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>
    /// Creates a copy of these options.
    /// </summary>
    public RoofControllerApiOptions Clone()
    {
        var copy = new RoofControllerApiOptions();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>
    /// Replaces every value with the values from <paramref name="source"/>, including cleared (null) values.
    /// </summary>
    public void CopyFrom(RoofControllerApiOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BaseUrl = source.BaseUrl;
        StatusPollIntervalSeconds = source.StatusPollIntervalSeconds;
        CameraStreamUrl = source.CameraStreamUrl;
        ClearFaultPulseMs = source.ClearFaultPulseMs;
        SafetyWatchdogTimeoutSeconds = source.SafetyWatchdogTimeoutSeconds;
        RequestRetryCount = source.RequestRetryCount;
        ConnectionFailurePromptThreshold = source.ConnectionFailurePromptThreshold;
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            yield return new ValidationResult("Roof Controller API base URL is required.", new[] { nameof(BaseUrl) });
        }

        if (StatusPollIntervalSeconds <= 0)
        {
            yield return new ValidationResult("Status poll interval must be greater than zero.", new[] { nameof(StatusPollIntervalSeconds) });
        }

        if (ClearFaultPulseMs is < RoofControllerLimits.MinClearFaultPulseMilliseconds or > RoofControllerLimits.MaxClearFaultPulseMilliseconds)
        {
            yield return new ValidationResult(
                $"Clear fault pulse duration must be between {RoofControllerLimits.MinClearFaultPulseMilliseconds} and {RoofControllerLimits.MaxClearFaultPulseMilliseconds} ms.",
                new[] { nameof(ClearFaultPulseMs) });
        }

        if (!string.IsNullOrWhiteSpace(BaseUrl) && !TryParseHttpUri(BaseUrl, out _))
        {
            yield return new ValidationResult("BaseUrl must be an absolute http:// or https:// URI.", new[] { nameof(BaseUrl) });
        }

        if (!string.IsNullOrWhiteSpace(CameraStreamUrl) && !TryParseHttpUri(CameraStreamUrl, out _))
        {
            yield return new ValidationResult("CameraStreamUrl must be an absolute http:// or https:// URI when provided.", new[] { nameof(CameraStreamUrl) });
        }

        if (SafetyWatchdogTimeoutSeconds is { } timeout && timeout <= 0)
        {
            yield return new ValidationResult("SafetyWatchdogTimeoutSeconds must be greater than zero when provided.", new[] { nameof(SafetyWatchdogTimeoutSeconds) });
        }

        if (RequestRetryCount is < 1 or > MaxRequestAttempts)
        {
            yield return new ValidationResult($"RequestRetryCount must be between 1 and {MaxRequestAttempts}.", new[] { nameof(RequestRetryCount) });
        }

        if (ConnectionFailurePromptThreshold < 1)
        {
            yield return new ValidationResult("ConnectionFailurePromptThreshold must be at least 1.", new[] { nameof(ConnectionFailurePromptThreshold) });
        }
    }
}
