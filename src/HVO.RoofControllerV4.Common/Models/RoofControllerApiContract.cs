namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level constants shared by the Pi web host, the Blazor console, API clients and the request samples.
/// </summary>
public static class RoofControllerApiContract
{
    /// <summary>Request header carrying an API key for the roof controller HTTP API.</summary>
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>Authentication scheme name for API keys.</summary>
    public const string ApiKeyScheme = "ApiKey";

    /// <summary>Role that may read status, health details and the camera.</summary>
    public const string ViewerRole = "RoofViewer";

    /// <summary>
    /// Role that may command motion (Open, Close, ClearFault, lease renewal). Stop is deliberately weaker: any
    /// authenticated role may Stop, or anyone when the controller is configured with AllowAnonymousStop.
    /// </summary>
    public const string OperatorRole = "RoofOperator";

    /// <summary>Role that may change configuration and read logs/diagnostics.</summary>
    public const string AdminRole = "RoofAdmin";

    /// <summary>Problem-details extension holding the <see cref="RoofControllerErrorCode"/> name.</summary>
    public const string ProblemCodeExtension = "code";

    /// <summary>Problem-details extension holding a <see cref="RoofStatusResponse"/> snapshot, when available.</summary>
    public const string ProblemStatusExtension = "roofStatus";

    /// <summary>Prefix of the problem-details <c>type</c> URI; the error code name is appended.</summary>
    public const string ProblemTypePrefix = "urn:hvo:roof-controller:";

    /// <summary>Builds the problem-details <c>type</c> URI for an error code.</summary>
    public static string ProblemType(RoofControllerErrorCode code) => ProblemTypePrefix + code;

    /// <summary>
    /// HTTP status code the API uses for each error code.
    /// </summary>
    public static int HttpStatusFor(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.NotInitialized => 503,
        RoofControllerErrorCode.ShuttingDown => 503,
        RoofControllerErrorCode.HardwareUnavailable => 503,
        RoofControllerErrorCode.RelayStateUnverified => 503,
        RoofControllerErrorCode.FaultLatched => 409,
        RoofControllerErrorCode.InterlockActive => 409,
        RoofControllerErrorCode.OperationInProgress => 409,
        RoofControllerErrorCode.LeaseNotActive => 409,
        RoofControllerErrorCode.ConfigurationVersionConflict => 409,
        RoofControllerErrorCode.ConfigurationRejected => 409,
        RoofControllerErrorCode.InvalidRequest => 400,
        RoofControllerErrorCode.SignInFailed => 401,
        RoofControllerErrorCode.SignInLockedOut => 429,
        RoofControllerErrorCode.SignInBusy => 429,
        RoofControllerErrorCode.KioskKeyRequired => 403,
        RoofControllerErrorCode.IdentityNotFound => 404,
        RoofControllerErrorCode.IdentityNameConflict => 409,
        RoofControllerErrorCode.IdentityReadOnly => 409,
        RoofControllerErrorCode.LastAdministrator => 409,
        RoofControllerErrorCode.IdentityStoreUnavailable => 503,
        RoofControllerErrorCode.CredentialNotAllowed => 403,
        _ => 500
    };
}
