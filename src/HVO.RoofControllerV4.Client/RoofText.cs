using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for roof states, stop reasons and refusals, so the web UI, the CLI and the kiosk describe
/// the roof the same way. A test pins these strings; change them deliberately.
/// </summary>
public static class RoofText
{
    /// <summary>Shown for an unreachable controller.</summary>
    public const string Unreachable = "The controller could not be reached.";

    /// <summary>Shown when the controller did not answer before the request timed out.</summary>
    public const string TimedOut = "The controller did not answer in time.";

    /// <summary>Shown when the controller answered with something the client could not read.</summary>
    public const string AnswerUnreadable = "The controller's answer could not be read. It may be a different version.";

    public static string DescribePosition(RoofControllerStatus status) => status switch
    {
        RoofControllerStatus.Open => "Open",
        RoofControllerStatus.Closed => "Closed",
        RoofControllerStatus.Opening => "Opening",
        RoofControllerStatus.Closing => "Closing",
        RoofControllerStatus.Stopped => "Stopped",
        RoofControllerStatus.PartiallyOpen => "Partially open",
        RoofControllerStatus.PartiallyClose => "Partially closed",
        RoofControllerStatus.Error => "Error",
        RoofControllerStatus.NotInitialized => "Not initialized",
        _ => "Unknown"
    };

    public static string DescribeStopReason(RoofControllerStopReason reason) => reason switch
    {
        RoofControllerStopReason.None => "No stop recorded",
        RoofControllerStopReason.NormalStop => "Stopped by operator",
        RoofControllerStopReason.LimitSwitchReached => "Limit switch reached",
        RoofControllerStopReason.EmergencyStop => "Emergency stop",
        RoofControllerStopReason.StopButtonPressed => "Stop button pressed",
        RoofControllerStopReason.SafetyWatchdogTimeout => "Safety watchdog timed out",
        RoofControllerStopReason.SystemDisposal => "Controller shut down",
        RoofControllerStopReason.InputReadFailure => "Safety inputs could not be read",
        RoofControllerStopReason.ContradictoryLimitInputs => "Open and closed limits both active",
        RoofControllerStopReason.StartLimitReasserted => "Starting limit reasserted after release",
        RoofControllerStopReason.DriveFault => "Drive fault input active",
        RoofControllerStopReason.RelayVerificationFailed => "Relay register verification failed",
        RoofControllerStopReason.OperatorLeaseExpired => "Operator lease expired",
        RoofControllerStopReason.DriveNotRunning => "Drive not reporting running (IN4)",
        RoofControllerStopReason.HostShutdown => "Host shutting down",
        RoofControllerStopReason.DepartureLimitNotReleased => "Starting limit did not release",
        _ => reason.ToString()
    };

    /// <summary>True for stop reasons that a safety mechanism, not a person or a limit, caused.</summary>
    public static bool IsSafetyStopReason(RoofControllerStopReason reason) => reason switch
    {
        RoofControllerStopReason.EmergencyStop => true,
        RoofControllerStopReason.SafetyWatchdogTimeout => true,
        RoofControllerStopReason.InputReadFailure => true,
        RoofControllerStopReason.ContradictoryLimitInputs => true,
        RoofControllerStopReason.StartLimitReasserted => true,
        RoofControllerStopReason.DriveFault => true,
        RoofControllerStopReason.RelayVerificationFailed => true,
        RoofControllerStopReason.OperatorLeaseExpired => true,
        RoofControllerStopReason.DriveNotRunning => true,
        RoofControllerStopReason.DepartureLimitNotReleased => true,
        _ => false
    };

    public static string DescribeErrorCode(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.NotInitialized => "The controller has not finished initializing.",
        RoofControllerErrorCode.ShuttingDown => "The controller is shutting down and accepts no new motion.",
        RoofControllerErrorCode.HardwareUnavailable => "The relay hardware is unavailable.",
        RoofControllerErrorCode.RelayStateUnverified => "The relay register could not be verified. Confirm at the roof that the motor has stopped.",
        RoofControllerErrorCode.FaultLatched => "A fault is latched. Resolve the cause, then clear the fault.",
        RoofControllerErrorCode.InterlockActive => "A safety interlock refused the command.",
        RoofControllerErrorCode.OperationInProgress => "Another operation is in progress.",
        RoofControllerErrorCode.LeaseNotActive => "No leased motion is active.",
        RoofControllerErrorCode.ConfigurationVersionConflict => "The configuration changed since it was read.",
        RoofControllerErrorCode.ConfigurationRejected => "The configuration was rejected as unsafe.",
        RoofControllerErrorCode.InvalidRequest => "The request was invalid.",
        RoofControllerErrorCode.SignInFailed => "Sign-in failed. Check the name and the password or PIN.",
        RoofControllerErrorCode.SignInLockedOut => "Too many failed sign-ins. Wait, then try again.",
        RoofControllerErrorCode.SignInBusy => "The controller is busy with other sign-ins. Try again in a moment.",
        RoofControllerErrorCode.KioskKeyRequired => "PIN sign-in is accepted only from a kiosk device.",
        RoofControllerErrorCode.IdentityNotFound => "The user, key or session was not found.",
        RoofControllerErrorCode.IdentityNameConflict => "That name is already in use.",
        RoofControllerErrorCode.IdentityReadOnly => "This entry is defined in the configuration file and cannot be changed here.",
        RoofControllerErrorCode.LastAdministrator => "The last administrator credential cannot be removed or demoted.",
        RoofControllerErrorCode.IdentityStoreUnavailable => "The identity store is unavailable. Try again shortly.",
        RoofControllerErrorCode.CredentialNotAllowed => "This kind of sign-in cannot do that. Sign in with a password or use an API key.",
        RoofControllerErrorCode.SettingNotPermitted => "You are not permitted to change this setting from here.",
        RoofControllerErrorCode.SettingsHandEditPending => "The settings file was edited by hand. Apply or discard that edit first.",
        RoofControllerErrorCode.SettingsStoreUnavailable => "The settings file could not be read or written.",
        RoofControllerErrorCode.RestartRefused => "The controller refused to restart.",
        RoofControllerErrorCode.SettingNotFound => "The setting was not found.",
        _ => "The controller reported an unexpected error."
    };

    /// <summary>
    /// Text for a refused request: the shared wording for its code, followed by the code in brackets. Requests refused
    /// before they reach the roof (HTTPS, origin, sign-in, permissions) fall back to wording by HTTP status.
    /// </summary>
    public static string DescribeRefusal(int statusCode, RoofControllerErrorCode? code, string? codeText)
    {
        if (code is { } known)
        {
            return $"{DescribeErrorCode(known)} [{known}]";
        }

        return codeText switch
        {
            "https_required" => "The controller requires HTTPS from this network. [https_required]",
            "origin_not_allowed" => "The controller refused a request from this page's origin. [origin_not_allowed]",
            _ => statusCode switch
            {
                400 => "The request was invalid.",
                401 => "Not signed in, or the credential is no longer valid. Sign in again.",
                403 => "Your role does not permit this.",
                404 => "The controller does not offer this. It may be an older version.",
                408 => TimedOut,
                429 => "Too many requests. Wait, then try again.",
                503 => "The controller is not ready. Try again shortly.",
                >= 500 => "The controller reported an unexpected error.",
                _ => $"The controller refused the request (HTTP {statusCode})."
            }
        };
    }

    /// <summary>
    /// Operator-facing text for a failed call. Messages of unexpected exceptions are not shown; they may carry internals
    /// that belong in a log.
    /// </summary>
    public static string DescribeFailure(Exception? error) => error switch
    {
        RoofApiException refusal => refusal.Message,
        TimeoutException => TimedOut,
        TaskCanceledException { InnerException: TimeoutException } => TimedOut,
        HttpRequestException => Unreachable,
        RoofProtocolException => AnswerUnreadable,
        _ => DescribeErrorCode(RoofControllerErrorCode.Unknown)
    };
}
