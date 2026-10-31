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
        $"A PIN must be exactly {RoofIdentityContract.MinimumPinLength} digits; the temporary commissioning PIN is reserved.");

    public const string RoleChangeEndsSessions = "Changing the role ends every session of the person.";

    public const string PasswordChangeEndsSessions = "Setting it ends every session of the person.";

    public const string PinChangeEndsSessions = "For a kiosk; operators and admins only. Setting it ends the person's PIN sessions.";

    public const string RotateEndsOldSecret = "The old secret stops working at once.";

    /// <summary>What changing your own PIN ends, and what it keeps (#205).</summary>
    public const string OwnPinChangeEndsSessions =
        "Changing your PIN keeps this session open and ends your other PIN sessions, at every kiosk. Your password sessions stay open.";

    /// <summary>Said to a person who has no PIN to change (#205): the first one is an admin's to give.</summary>
    public const string NoPinYet = "You have no PIN. An admin gives you your first one; then you can change it yourself.";

    /// <summary>Said to a Linux account signed in with its password (#205).</summary>
    public const string LinuxHasNoPin =
        "A Linux account has no PIN of its own here. The kiosk's commissioned PIN is changed at a kiosk, after unlocking with it.";

    /// <summary>Said once your own PIN changed (#205).</summary>
    public const string OwnPinChanged = "Your PIN was changed. Your other PIN sessions were ended.";

    /// <summary>What removing a person's PIN does (#205).</summary>
    public const string PinRemoveEndsSessions =
        "Removing it ends the person's PIN sessions: they cannot unlock a kiosk until an admin gives them a new PIN.";

    public static readonly string DisplayNameRule = string.Create(
        CultureInfo.InvariantCulture,
        $"A display name is optional: up to {RoofIdentityContract.MaximumDisplayNameLength} characters, without control characters.");

    public static readonly string ShortNameRule = string.Create(
        CultureInfo.InvariantCulture,
        $"A short name is optional: up to {RoofIdentityContract.MaximumShortNameLength} characters, for narrow places such as the kiosk.");

    /// <summary>Changing a display or short name ends nothing.</summary>
    public const string NamesChangeEndsNothing = "Leave a name empty to remove it. Changing the names ends no session.";

    /// <summary>Where a Linux account's names come from, and that they cannot be changed here (#209).</summary>
    public const string LinuxNamesReadOnly = "A Linux account's names come from the operating system: its full name and its user name. Change the full name on the controller's machine.";

    /// <summary>Where a person changes their own names: with a keyboard, not at the kiosk (#209).</summary>
    public const string OwnNamesWhere = "Change your own names in the web UI (your account) or the terminal (Setup); an admin can change anyone's.";

    /// <summary>
    /// In place of a last sign-in the controller has not recorded: none since the person was added, or since the
    /// controller began recording them (#209).
    /// </summary>
    public const string NoSignInRecorded = "none recorded";

    /// <summary>
    /// Checks a display or short name a person typed. Empty means none and is accepted; returns what is wrong, or null.
    /// </summary>
    public static string? CheckNames(string? displayName, string? shortName)
    {
        var display = RoofIdentityContract.NormalizeLabel(displayName);
        var @short = RoofIdentityContract.NormalizeLabel(shortName);
        return display is not null && !RoofIdentityContract.IsValidDisplayName(display) ? DisplayNameRule
            : @short is not null && !RoofIdentityContract.IsValidShortName(@short) ? ShortNameRule
            : null;
    }

    /// <summary>A person as a list shows them: "Roy Salisbury (roys)" with a display name, otherwise the name.</summary>
    public static string DescribePerson(string name, string? displayName)
        => string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, name, StringComparison.Ordinal)
            ? name
            : $"{displayName} ({name})";

    /// <summary>How a person signs in (<see cref="RoofLoginTypes"/>): "local" or "Linux".</summary>
    public static string DescribeLoginType(string? loginType) => loginType switch
    {
        RoofLoginTypes.Linux => "Linux",
        null or RoofLoginTypes.Local => "local",
        _ => loginType
    };

    /// <summary>A last sign-in: its time, or <see cref="NoSignInRecorded"/>.</summary>
    public static string DescribeLastLogin(DateTimeOffset? lastLoginUtc)
        => lastLoginUtc is { } at ? RoofStatusText.Time(at) : NoSignInRecorded;

    public const string SecretShownOnce = "Copy it now: it is not shown again.";

    /// <summary>
    /// Checks a new password typed twice. Empty (both) means none and is accepted; returns what is wrong, or null.
    /// </summary>
    public static string? CheckNewPassword(string? value, string? again)
        => CheckNewSecret(value, again, RoofIdentityContract.IsValidPassword, PasswordRule, "passwords");

    /// <summary>Checks a new PIN typed twice. Empty (both) means none and is accepted; returns what is wrong, or null.</summary>
    public static string? CheckNewPin(string? value, string? again)
        => CheckNewSecret(value, again, RoofIdentityContract.IsValidPin, PinRule, "PINs");

    /// <summary>
    /// Checks a change of your own PIN (#205): the current PIN, then the new one typed twice. Returns what is wrong, or
    /// null. The controller checks the current PIN itself.
    /// </summary>
    public static string? CheckPinChange(string? current, string? value, string? again)
        => !RoofIdentityContract.IsAcceptedPin(current) ? "Enter your current PIN."
            : string.IsNullOrEmpty(value) ? "Enter your new PIN."
            : CheckNewPin(value, again) is { } problem ? problem
            : string.Equals(current, value, StringComparison.Ordinal) ? "Choose a new PIN different from your current one."
            : null;

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
