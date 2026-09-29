using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for people, API keys and sessions: the rules for a name, a password and a PIN, and what a
/// change ends, so the web UI, the CLI and the terminal UI say the same.
/// </summary>
public static class RoofIdentityText
{
    /// <summary>What a name may be; it follows "not a valid name:" or "Name:".</summary>
    public static readonly string NameRule = string.Create(
        CultureInfo.InvariantCulture,
        $"use 1 to {RoofIdentityContract.MaximumNameLength} letters, digits, '.', '_', '@' or '-', starting with a letter or digit.");

    public static readonly string PasswordRule = string.Create(
        CultureInfo.InvariantCulture,
        $"A password must be {RoofIdentityContract.MinimumPasswordLength} to {RoofIdentityContract.MaximumPasswordLength} characters.");

    public static readonly string PinRule = string.Create(
        CultureInfo.InvariantCulture,
        $"A PIN must be {RoofIdentityContract.MinimumPinLength} to {RoofIdentityContract.MaximumPinLength} digits.");

    public const string RoleChangeEndsSessions = "Changing the role ends every session of the person.";

    public const string PasswordChangeEndsSessions = "Setting it ends every session of the person.";

    public const string PinChangeEndsSessions = "For a kiosk; operators and admins only. Setting it ends the person's PIN sessions.";

    public const string RotateEndsOldSecret = "The old secret stops working at once.";

    public const string SecretShownOnce = "Copy it now: it is not shown again.";

    /// <summary>
    /// Checks a new password typed twice. Empty (both) means none and is accepted; returns what is wrong, or null.
    /// </summary>
    public static string? CheckNewPassword(string? value, string? again)
        => CheckNewSecret(value, again, RoofIdentityContract.IsValidPassword, PasswordRule, "passwords");

    /// <summary>Checks a new PIN typed twice. Empty (both) means none and is accepted; returns what is wrong, or null.</summary>
    public static string? CheckNewPin(string? value, string? again)
        => CheckNewSecret(value, again, RoofIdentityContract.IsValidPin, PinRule, "PINs");

    /// <summary>How a session signed in: "PIN" or "password".</summary>
    public static string DescribeKind(RoofCredentialKind kind) => kind == RoofCredentialKind.Pin ? "PIN" : "password";

    private static string? CheckNewSecret(string? value, string? again, Func<string?, bool> isValid, string rule, string plural)
    {
        value ??= string.Empty;
        again ??= string.Empty;
        return value.Length == 0 ? again.Length == 0 ? null : $"The two {plural} differ."
            : !isValid(value) ? rule
            : !string.Equals(value, again, StringComparison.Ordinal) ? $"The two {plural} differ."
            : null;
    }
}
