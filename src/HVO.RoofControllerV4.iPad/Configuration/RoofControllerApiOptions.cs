using System.ComponentModel.DataAnnotations;

namespace HVO.RoofControllerV4.iPad.Configuration;

/// <summary>
/// Strongly-typed configuration for communicating with the Roof Controller Web API.
/// </summary>
public sealed class RoofControllerApiOptions : IValidatableObject
{
    /// <summary>
    /// Base URL for the Roof Controller Web API (e.g. https://observatory.local:7151/api/v4.0/).
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
    /// Number of retry attempts for API requests before surfacing an error.
    /// </summary>
    public int RequestRetryCount { get; set; } = 3;

    /// <summary>
    /// Consecutive polling failures before prompting the operator with recovery options.
    /// </summary>
    public int ConnectionFailurePromptThreshold { get; set; } = 3;

    /// <summary>
    /// Converts the configured BaseUrl into a <see cref="Uri"/> instance.
    /// </summary>
    public Uri GetBaseUri() => new(BaseUrl, UriKind.Absolute);

    /// <summary>
    /// Converts the configured CameraStreamUrl into a <see cref="Uri"/> instance when available.
    /// </summary>
    public Uri? GetCameraStreamUri() => string.IsNullOrWhiteSpace(CameraStreamUrl) ? null : new Uri(CameraStreamUrl, UriKind.Absolute);

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

        if (ClearFaultPulseMs <= 0)
        {
            yield return new ValidationResult("Clear fault pulse duration must be greater than zero.", new[] { nameof(ClearFaultPulseMs) });
        }

        if (!string.IsNullOrWhiteSpace(BaseUrl) && !Uri.IsWellFormedUriString(BaseUrl, UriKind.Absolute))
        {
            yield return new ValidationResult("BaseUrl must be an absolute URI.", new[] { nameof(BaseUrl) });
        }

        if (!string.IsNullOrWhiteSpace(CameraStreamUrl) && !Uri.IsWellFormedUriString(CameraStreamUrl, UriKind.Absolute))
        {
            yield return new ValidationResult("CameraStreamUrl must be an absolute URI when provided.", new[] { nameof(CameraStreamUrl) });
        }

        if (SafetyWatchdogTimeoutSeconds is { } timeout && timeout <= 0)
        {
            yield return new ValidationResult("SafetyWatchdogTimeoutSeconds must be greater than zero when provided.", new[] { nameof(SafetyWatchdogTimeoutSeconds) });
        }

        if (RequestRetryCount < 1)
        {
            yield return new ValidationResult("RequestRetryCount must be at least 1.", new[] { nameof(RequestRetryCount) });
        }

        if (ConnectionFailurePromptThreshold < 1)
        {
            yield return new ValidationResult("ConnectionFailurePromptThreshold must be at least 1.", new[] { nameof(ConnectionFailurePromptThreshold) });
        }
    }
}
