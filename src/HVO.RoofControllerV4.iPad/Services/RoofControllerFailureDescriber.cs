using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Operator-facing wording for request failures. Keeps "the controller refused" separate from "the app could not
/// reach the controller", because the two call for different actions.
/// </summary>
public static class RoofControllerFailureDescriber
{
    public static string Describe(Exception? error, string operation)
    {
        var failure = RoofControllerApiException.From(error);

        if (failure.IsGatewayFailure)
        {
            return $"{operation} failed: the controller service is unavailable (HTTP {failure.StatusCode}).";
        }

        return failure.Kind switch
        {
            RoofControllerFailureKind.NotConfigured => $"{operation} not sent: {failure.Message}",
            RoofControllerFailureKind.Transport => $"{operation} failed: cannot reach the controller ({failure.Message}).",
            RoofControllerFailureKind.Timeout => $"{operation} timed out: the controller did not answer in time.",
            RoofControllerFailureKind.Cancelled => $"{operation} cancelled.",
            RoofControllerFailureKind.Unauthorized => $"{operation} refused: the API key is missing or not accepted (HTTP 401). Check the API key on the Configuration tab.",
            RoofControllerFailureKind.Forbidden when failure.IsHttpsRequired => $"{operation} refused: the controller requires HTTPS. Change the controller URL to https:// on the Configuration tab.",
            RoofControllerFailureKind.Forbidden => $"{operation} refused: this API key's role does not allow it (HTTP 403).",
            RoofControllerFailureKind.InvalidResponse => $"{operation} failed: the controller's response could not be read.",
            _ => DescribeRejection(failure, operation)
        };
    }

    public static string DescribeCode(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.NotInitialized => "the controller has not finished initializing",
        RoofControllerErrorCode.ShuttingDown => "the controller is shutting down",
        RoofControllerErrorCode.HardwareUnavailable => "the safety inputs cannot be read",
        RoofControllerErrorCode.RelayStateUnverified => "the relay register could not be verified",
        RoofControllerErrorCode.FaultLatched => "a safety fault is latched; clear the fault first",
        RoofControllerErrorCode.InterlockActive => "a safety interlock is active",
        RoofControllerErrorCode.OperationInProgress => "another operation is in progress",
        RoofControllerErrorCode.LeaseNotActive => "no leased motion is active",
        RoofControllerErrorCode.ConfigurationVersionConflict => "the configuration changed on the controller since it was loaded",
        RoofControllerErrorCode.ConfigurationRejected => "the controller does not permit this configuration",
        RoofControllerErrorCode.InvalidRequest => "the request was invalid",
        _ => "the controller reported an internal error"
    };

    private static string DescribeRejection(RoofControllerApiException failure, string operation)
    {
        var detail = failure.ProblemDetail ?? failure.ProblemTitle;
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";

        if (failure.ErrorCode is { } code)
        {
            return $"{operation} rejected by the controller: {DescribeCode(code)}{suffix}.";
        }

        if (failure.StatusCode is { } status)
        {
            var codeText = string.IsNullOrWhiteSpace(failure.RawCode) ? string.Empty : $" {failure.RawCode}";
            return $"{operation} rejected by the controller: HTTP {status}{codeText}{suffix}.";
        }

        return $"{operation} failed: {failure.Message}";
    }
}
