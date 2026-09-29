using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>What a client knows about the most recent Stop request.</summary>
public enum RoofStopOutcome
{
    None,
    Sent,
    Acknowledged,
    RelayUnverified,
    Failed
}

/// <summary>
/// The Stop wording every client uses: the button and the result messages. A client never claims more than the
/// controller told it; a stop whose relay register could not be read back says so and sends the operator to the roof.
/// A test pins these strings; change them deliberately.
/// </summary>
public static class RoofStopText
{
    public const string ButtonLabel = "Stop roof";

    /// <summary>Shown next to Stop wherever other controls may be unavailable.</summary>
    public const string AlwaysAvailable = "Stop is always available.";

    public const string Sending = "Stop sent. Waiting for the controller…";

    public const string AcknowledgedVerified = "Stop acknowledged. Relay register verified de-energized.";

    public const string Acknowledged = "Stop acknowledged by the controller.";

    public const string AcknowledgedUnverified =
        "Stop acknowledged, but the relay register could not be verified. Confirm at the roof that the motor has stopped.";

    public const string SentUnverified =
        "Stop sent, but the relay register could not be verified. Confirm at the roof that the motor has stopped. [RelayStateUnverified]";

    public const string UseRoofStop = "Use the stop control at the roof.";

    /// <summary>A 401 for a session, or for a client with no credential.</summary>
    public const string SignedOut = "Stop was not sent because the session is signed out. Sign in again, or use the stop control at the roof.";

    /// <summary>A 401 for an API key or a kiosk's device key: there is nothing to sign in to.</summary>
    public const string KeyRefused = "Stop was not sent because the controller did not accept the key. Use the stop control at the roof.";

    /// <summary>A 401 for a web page's Stop form (the Stop bar, or the reconnect dialog), which has no sign-in of its own.</summary>
    public const string PageSignedOut =
        "Stop was not sent because this page is signed out. Reload the page to sign in again, or use the stop control at the roof.";

    /// <summary>A web page's Stop form with a missing or old antiforgery token.</summary>
    public const string PageOutOfDate = "Stop was not sent because this page is out of date. Reload the page, or use the stop control at the roof.";

    /// <summary>A failed stop, with the reason, sending the operator to the roof.</summary>
    public static string Failed(string reason) => $"Stop failed: {reason} {UseRoofStop}";

    /// <summary>
    /// Classifies a completed Stop request from what the controller answered: whether it accepted the stop, the status it
    /// reported, and the refusal code if it refused.
    /// </summary>
    public static (RoofStopOutcome Outcome, string Message) Classify(
        bool succeeded,
        RoofStatusResponse? status,
        RoofControllerErrorCode? errorCode,
        string failureReason)
    {
        if (succeeded)
        {
            return status?.RelayRegisterState switch
            {
                RoofRelayRegisterState.Unverified => (RoofStopOutcome.RelayUnverified, AcknowledgedUnverified),
                RoofRelayRegisterState.Verified => (RoofStopOutcome.Acknowledged, AcknowledgedVerified),
                _ => (RoofStopOutcome.Acknowledged, Acknowledged)
            };
        }

        if (errorCode == RoofControllerErrorCode.RelayStateUnverified)
        {
            return (RoofStopOutcome.RelayUnverified, SentUnverified);
        }

        return (RoofStopOutcome.Failed, Failed(failureReason));
    }
}

/// <summary>The answer to a Stop request.</summary>
/// <param name="Outcome">Acknowledged, RelayUnverified or Failed.</param>
/// <param name="Message">The shared wording for the outcome.</param>
/// <param name="Status">The status the controller reported with its answer, if any.</param>
/// <param name="Error">Why the stop failed or was unverified, if it did not succeed.</param>
public sealed record RoofStopResult(RoofStopOutcome Outcome, string Message, RoofStatusResponse? Status, Exception? Error)
{
    public bool IsAcknowledged => Outcome == RoofStopOutcome.Acknowledged;
}
