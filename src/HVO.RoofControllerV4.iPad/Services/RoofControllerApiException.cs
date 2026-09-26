using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// How a roof controller request failed. The distinction that matters to the operator is whether the controller
/// answered and refused (<see cref="Rejected"/>, <see cref="Unauthorized"/>, <see cref="Forbidden"/>) or whether
/// the app could not tell what happened (<see cref="Transport"/>, <see cref="Timeout"/>).
/// </summary>
public enum RoofControllerFailureKind
{
    /// <summary>The request could not be sent or the connection failed before a response arrived.</summary>
    Transport,

    /// <summary>No response arrived before the request deadline. The controller may or may not have acted.</summary>
    Timeout,

    /// <summary>The request was cancelled by the app (page closed, settings changed, shutdown).</summary>
    Cancelled,

    /// <summary>The controller answered with an error status.</summary>
    Rejected,

    /// <summary>HTTP 401: the API key is missing or not accepted.</summary>
    Unauthorized,

    /// <summary>HTTP 403: the key's role is too low, or the controller requires HTTPS.</summary>
    Forbidden,

    /// <summary>The controller answered with a success status but the body could not be read.</summary>
    InvalidResponse,

    /// <summary>The app has no valid controller address, so nothing was sent.</summary>
    NotConfigured
}

/// <summary>
/// Failure returned by <see cref="IRoofControllerApiClient"/>. Carries the parsed problem details when the controller
/// answered, including the <see cref="RoofControllerErrorCode"/> and the controller's status snapshot.
/// </summary>
public sealed class RoofControllerApiException : Exception
{
    /// <summary>Problem code the controller returns for protected endpoints reached over plain HTTP.</summary>
    public const string HttpsRequiredCode = "https_required";

    public RoofControllerApiException(RoofControllerFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public RoofControllerFailureKind Kind { get; }

    /// <summary>HTTP status code, when the controller (or a proxy) answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Parsed <c>code</c> extension, when it names a known <see cref="RoofControllerErrorCode"/>.</summary>
    public RoofControllerErrorCode? ErrorCode { get; init; }

    /// <summary>Raw <c>code</c> extension (or the suffix of the problem <c>type</c>), including codes this app does not know.</summary>
    public string? RawCode { get; init; }

    /// <summary>Controller status snapshot attached to the problem response, when present.</summary>
    public RoofStatusResponse? RoofStatus { get; init; }

    public string? ProblemTitle { get; init; }

    public string? ProblemDetail { get; init; }

    public bool IsHttpsRequired => string.Equals(RawCode, HttpsRequiredCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for 502/503/504 without a controller error code: a proxy or the web host answered, not the roof service.
    /// </summary>
    public bool IsGatewayFailure => StatusCode is 502 or 503 or 504 && string.IsNullOrEmpty(RawCode);

    /// <summary>True when the app could not reach the controller (as opposed to the controller refusing the request).</summary>
    public bool IsConnectivityFailure => Kind is RoofControllerFailureKind.Transport or RoofControllerFailureKind.Timeout || IsGatewayFailure;

    /// <summary>
    /// True when an idempotent request (GET, Stop) may be sent again. Never used for Open, Close, ClearFault or configuration.
    /// </summary>
    public bool IsRetryableForIdempotentRequest => IsConnectivityFailure;

    /// <summary>Wraps any exception as a <see cref="RoofControllerApiException"/>.</summary>
    public static RoofControllerApiException From(Exception? error)
    {
        return error switch
        {
            RoofControllerApiException apiException => apiException,
            null => new RoofControllerApiException(RoofControllerFailureKind.Transport, "The request failed without an error description."),
            OperationCanceledException cancelled => new RoofControllerApiException(RoofControllerFailureKind.Cancelled, "The request was cancelled.", cancelled),
            HttpRequestException transport => new RoofControllerApiException(RoofControllerFailureKind.Transport, transport.Message, transport),
            _ => new RoofControllerApiException(RoofControllerFailureKind.Transport, error.Message, error)
        };
    }
}
