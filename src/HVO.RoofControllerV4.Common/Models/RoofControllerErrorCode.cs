namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Machine-readable reason a roof controller command was rejected or could not be verified.
/// Returned in the <c>code</c> extension of API problem responses (see <see cref="RoofControllerApiContract"/>).
/// </summary>
public enum RoofControllerErrorCode
{
    /// <summary>Unexpected failure. HTTP 500.</summary>
    Unknown = 0,

    /// <summary>The controller has not completed initialization. HTTP 503.</summary>
    NotInitialized = 1,

    /// <summary>The controller is shutting down or disposed and no longer admits commands. HTTP 503.</summary>
    ShuttingDown = 2,

    /// <summary>Safety inputs could not be read or are stale; motion is refused. HTTP 503.</summary>
    HardwareUnavailable = 3,

    /// <summary>A relay write failed or its register read-back did not match; relay state is unverified. HTTP 503.</summary>
    RelayStateUnverified = 4,

    /// <summary>A safety fault is latched and must be reset before motion is allowed. HTTP 409.</summary>
    FaultLatched = 5,

    /// <summary>A safety interlock (drive fault input, contradictory limits, destination limit) prevents the command. HTTP 409.</summary>
    InterlockActive = 6,

    /// <summary>Another exclusive operation (for example a clear-fault pulse) is in progress. HTTP 409.</summary>
    OperationInProgress = 7,

    /// <summary>A lease renewal was requested but no leased motion is active. HTTP 409.</summary>
    LeaseNotActive = 8,

    /// <summary>The configuration changed since the caller read it (version mismatch). HTTP 409.</summary>
    ConfigurationVersionConflict = 9,

    /// <summary>The configuration is not permitted in the current hardware mode. HTTP 409.</summary>
    ConfigurationRejected = 10,

    /// <summary>The request was malformed or out of range. HTTP 400.</summary>
    InvalidRequest = 11,

    /// <summary>The name, password or PIN is wrong, or the person cannot sign in that way. HTTP 401.</summary>
    SignInFailed = 12,

    /// <summary>
    /// Too many failed sign-ins for this name, or PIN attempts at this kiosk: sign-in is refused until the time in the
    /// <c>Retry-After</c> header. Stop is never locked out. HTTP 429.
    /// </summary>
    SignInLockedOut = 13,

    /// <summary>Too many sign-ins are being checked at once; retry shortly. HTTP 429.</summary>
    SignInBusy = 14,

    /// <summary>A PIN sign-in was sent with an API key that is not a kiosk key. HTTP 403.</summary>
    KioskKeyRequired = 15,

    /// <summary>No user, API key or session has that name or identifier. HTTP 404.</summary>
    IdentityNotFound = 16,

    /// <summary>A user or API key with that name already exists. HTTP 409.</summary>
    IdentityNameConflict = 17,

    /// <summary>The API key comes from the controller's configuration and cannot be changed through the API. HTTP 409.</summary>
    IdentityReadOnly = 18,

    /// <summary>The change would leave no admin credential (admin API key, or admin with a password). HTTP 409.</summary>
    LastAdministrator = 19,

    /// <summary>The identity store could not be read or written, so sign-in and management are unavailable. HTTP 503.</summary>
    IdentityStoreUnavailable = 20,

    /// <summary>
    /// The caller's role would allow this, but not the way they signed in: a PIN session cannot manage people, API keys
    /// or sessions. Use an admin API key or sign in with a password. HTTP 403.
    /// </summary>
    CredentialNotAllowed = 21
}
