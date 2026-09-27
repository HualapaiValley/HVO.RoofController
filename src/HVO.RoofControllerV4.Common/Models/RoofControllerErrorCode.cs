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
    InvalidRequest = 11
}
