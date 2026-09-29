using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Why the sign-in page is shown again: the value of its <c>message</c> query field, and the words for it. The words
/// never say which of the name or the password was wrong.
/// </summary>
public static class SignInMessages
{
    public const string Failed = "failed";

    public const string Expired = "expired";

    public const string Missing = "missing";

    public const string TooMany = "too-many";

    public const string LockedOut = "locked";

    public const string Busy = "busy";

    public const string Unreachable = "unreachable";

    public const string Refused = "refused";

    public const string Ended = "ended";

    public const string SignedOut = "signed-out";

    /// <summary>The words for a message, and whether it reports a problem; null for a value the page does not know.</summary>
    public static SignInMessage? Describe(string? message) => message switch
    {
        Failed => new("The name or the password was not accepted. Check them and try again.", true),
        Expired => new("The sign-in form expired. Sign in again.", true),
        Missing => new("Enter your name and your password.", true),
        TooMany => new("Too many sign-ins from this computer. Wait a minute, then try again.", true),
        LockedOut => new(RoofText.DescribeErrorCode(RoofControllerErrorCode.SignInLockedOut), true),
        Busy => new(RoofText.DescribeErrorCode(RoofControllerErrorCode.SignInBusy), true),
        Unreachable => new("The controller did not answer. Check that it is running, then try again.", true),
        Refused => new("The controller refused the sign-in. Ask an admin to check the web UI's settings.", true),
        Ended => new("Your session ended: you signed out elsewhere, an admin ended it, your password changed, or it expired. Sign in again.", false),
        SignedOut => new("You are signed out.", false),
        _ => null
    };
}

/// <summary>A message on the sign-in page.</summary>
public sealed record SignInMessage(string Text, bool IsProblem);
