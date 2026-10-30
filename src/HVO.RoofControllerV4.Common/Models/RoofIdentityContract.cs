using System.Linq;
using System.ComponentModel.DataAnnotations;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level rules for people, sessions and managed API keys: the sign-in endpoints (<c>api/v4.0/Auth</c>) and the
/// admin management endpoints (<c>api/v4.0/Identity</c>).
/// </summary>
public static class RoofIdentityContract
{
    /// <summary>Base route of sign-in, sign-out and "who am I".</summary>
    public const string AuthRoute = "api/v{version:apiVersion}/Auth";

    /// <summary>Base route of the admin management endpoints for users, API keys and sessions.</summary>
    public const string IdentityRoute = "api/v{version:apiVersion}/Identity";

    /// <summary>
    /// Scheme of the <c>Authorization</c> header that carries a session token: <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Optional header on a request made with an API key: the person the key's holder acts for, for example the person
    /// signed in to the web UI when it sends Stop with its own key. Recorded in the audit log as the key's claim; it
    /// grants nothing.
    /// </summary>
    public const string OnBehalfOfHeaderName = "X-On-Behalf-Of";

    /// <summary>User and API key names: a letter or digit, then letters, digits, '.', '_', '@' or '-'.</summary>
    public const string NamePattern = "^[A-Za-z0-9][A-Za-z0-9._@-]*$";

    public const int MaximumNameLength = 64;

    public const int MinimumPasswordLength = 7;

    public const int MaximumPasswordLength = 256;

    /// <summary>PINs are digits only.</summary>
    public const string PinPattern = "^[0-9]+$";

    public const int MinimumPinLength = 4;

    public const int MaximumPinLength = 4;

    /// <summary>Existing PIN hashes are retained until their owner changes them.</summary>
    public const int MaximumAcceptedPinLength = 12;

    /// <summary>A person's display name (the friendly name, "Ana Example") has at most this many characters.</summary>
    public const int MaximumDisplayNameLength = 64;

    /// <summary>A person's short name (for narrow places such as a kiosk, "Ana" or "AE") has at most this many characters.</summary>
    public const int MaximumShortNameLength = 16;

    /// <summary>True when <paramref name="name"/> is a valid user or API key name.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '@' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="pin"/> has only digits and an allowed length.</summary>
    public static bool IsValidPin(string? pin)
    {
        if (pin is null || pin == "0000" || pin.Length < MinimumPinLength || pin.Length > MaximumPinLength)
        {
            return false;
        }

        foreach (var c in pin)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sign-in accepts existing legacy PINs and the reserved temporary code; enrollment uses IsValidPin.</summary>
    public static bool IsAcceptedPin(string? pin)
        => pin is { Length: >= MinimumPinLength and <= MaximumAcceptedPinLength } && pin.All(char.IsAsciiDigit);

    /// <summary>True when <paramref name="password"/> has an allowed length.</summary>
    public static bool IsValidPassword(string? password)
        => password is not null && password.Length >= MinimumPasswordLength && password.Length <= MaximumPasswordLength;

    /// <summary>
    /// True when <paramref name="displayName"/> is a valid display name: 1 to <see cref="MaximumDisplayNameLength"/>
    /// characters with no space at either end, and no control, formatting or private-use character (so a name cannot
    /// reorder or hide the text around it).
    /// </summary>
    public static bool IsValidDisplayName(string? displayName) => IsValidLabel(displayName, MaximumDisplayNameLength);

    /// <summary>True when <paramref name="shortName"/> is a valid short name: the display name's rules, at most <see cref="MaximumShortNameLength"/> characters.</summary>
    public static bool IsValidShortName(string? shortName) => IsValidLabel(shortName, MaximumShortNameLength);

    /// <summary>
    /// A display or short name as the controller keeps it: trimmed, and null when nothing is left. Validate the result
    /// with <see cref="IsValidDisplayName"/> or <see cref="IsValidShortName"/>.
    /// </summary>
    public static string? NormalizeLabel(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidLabel(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate
                    or UnicodeCategory.OtherNotAssigned)
            {
                return false;
            }

            rest = rest[used..];
        }

        return true;
    }
}

/// <summary>
/// How a person signs in, from where their account lives (#209): <see cref="Local"/> for a person in the controller's
/// identity store (a password, a PIN or both), <see cref="Linux"/> for an enrolled Linux account (its Linux password,
/// checked by the host). Sent as a string, so a later type does not break an older client.
/// </summary>
public static class RoofLoginTypes
{
    public const string Local = "Local";

    public const string Linux = "Linux";

    /// <summary>The login type of a principal's provider: <c>linux</c> is <see cref="Linux"/>; <c>virtual</c> or none is <see cref="Local"/>.</summary>
    public static string FromProvider(string? provider)
        => string.Equals(provider, "linux", StringComparison.OrdinalIgnoreCase) ? Linux : Local;
}

/// <summary>How a caller proved who it is.</summary>
public enum RoofCredentialKind
{
    /// <summary>An API key in the <c>X-Api-Key</c> header.</summary>
    ApiKey = 0,

    /// <summary>A session from a name and password.</summary>
    Session = 1,

    /// <summary>A short session from a PIN, entered at a kiosk that holds a kiosk key.</summary>
    Pin = 2
}

/// <summary>Where an API key is defined.</summary>
public enum RoofApiKeySource
{
    /// <summary>The controller's configuration (environment or secrets directory). Read-only through the API.</summary>
    Configuration = 0,

    /// <summary>Added through the API and kept in the identity store.</summary>
    Managed = 1
}

/// <summary>Body of <c>POST Auth/Session</c>.</summary>
public sealed record class RoofSignInRequest
{
    [Required]
    [JsonRequired]
    public string? Name { get; init; }

    [Required]
    [JsonRequired]
    public string? Password { get; init; }

    // The password is left out, so the request can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Password = {(Password is null ? "(none)" : "(set)")}");
        return true;
    }
}

/// <summary>Body of <c>POST Auth/Pin</c>, sent with a kiosk key in <c>X-Api-Key</c>.</summary>
public sealed record class RoofPinSignInRequest
{
    [Required]
    [JsonRequired]
    public string? Name { get; init; }

    [Required]
    [JsonRequired]
    public string? Pin { get; init; }

    // The PIN is left out, so the request can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Pin = {(Pin is null ? "(none)" : "(set)")}");
        return true;
    }
}

/// <summary>Body of <c>POST Auth/Password</c>: a signed-in person changes their own password.</summary>
public sealed record class RoofPasswordChangeRequest
{
    [Required]
    [JsonRequired]
    public string? CurrentPassword { get; init; }

    [Required]
    [JsonRequired]
    public string? NewPassword { get; init; }

    // The passwords are left out, so the request can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"CurrentPassword = {(CurrentPassword is null ? "(none)" : "(set)")}, NewPassword = {(NewPassword is null ? "(none)" : "(set)")}");
        return true;
    }
}

/// <summary>
/// Body of <c>POST Auth/Names</c> (#209): a person signed in with a password changes their own display and short name.
/// A null name is left as it is; an empty one (or only spaces) is removed. Refused for a PIN session (names are edited
/// where there is a keyboard: the web UI, the terminal) and for a Linux account (its names come from the operating
/// system).
/// </summary>
public sealed record class RoofNamesChangeRequest
{
    public string? DisplayName { get; init; }

    public string? ShortName { get; init; }
}

/// <summary>A new session. The token is shown only here; send it as <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
/// <param name="Token">The bearer token (secret).</param>
/// <param name="SessionId">Non-secret identifier of the session, as the admin session list shows it.</param>
/// <param name="Name">The person.</param>
/// <param name="Role">The role the session grants.</param>
/// <param name="Kind"><see cref="RoofCredentialKind.Session"/> or <see cref="RoofCredentialKind.Pin"/>.</param>
/// <param name="ExpiresUtc">The session ends at this time whatever happens.</param>
/// <param name="IdleTimeoutSeconds">For a PIN session, it also ends after this long without a request.</param>
public sealed record RoofSessionResponse(
    string Token,
    string SessionId,
    string Name,
    string Role,
    RoofCredentialKind Kind,
    DateTimeOffset ExpiresUtc,
    double? IdleTimeoutSeconds)
{
    // The token is left out, so the record can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"SessionId = {SessionId}, Name = {Name}, Role = {Role}, Kind = {Kind}, ExpiresUtc = {ExpiresUtc:O}, ")
            .Append($"IdleTimeoutSeconds = {IdleTimeoutSeconds}");
        return true;
    }
}

/// <summary>Who the caller is, from <c>GET Auth/Me</c>.</summary>
/// <param name="Name">The person or the API key's name.</param>
/// <param name="Role">The highest role the credential grants.</param>
/// <param name="Kind">How the caller proved who it is.</param>
/// <param name="Device">For a PIN session, the kiosk key it was opened at.</param>
/// <param name="SessionId">For a session, its identifier.</param>
/// <param name="ExpiresUtc">For a session, when it ends at the latest.</param>
/// <param name="IsKiosk">For an API key, whether it is a kiosk key.</param>
public sealed record RoofCallerResponse(
    string Name,
    string Role,
    RoofCredentialKind Kind,
    string? Device,
    string? SessionId,
    DateTimeOffset? ExpiresUtc,
    bool IsKiosk)
{
    /// <summary>
    /// For a person, their display name (#209): as set in the controller, or for a Linux account its full name from the
    /// operating system. Null for an API key, or when there is none.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>For a person, their short name (#209); for a Linux account its user name. Null for an API key.</summary>
    public string? ShortName { get; init; }

    /// <summary>
    /// How the person signs in (<see cref="RoofLoginTypes"/>), or null for an API key. A Linux account's names are
    /// read-only: they come from the operating system.
    /// </summary>
    public string? LoginType { get; init; }
}

/// <summary>A person who may sign in with a PIN at a kiosk, from <c>GET Auth/Pin/Users</c>.</summary>
public sealed record RoofPinUserResponse(string Name, string Role)
{
    public string? PrincipalId { get; init; }
    public string? Provider { get; init; }
    public bool SetupPending { get; init; }

    /// <summary>The person's display name, or null when none is set (#209).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The person's short name, for a narrow place such as a kiosk button, or null (#209).</summary>
    public string? ShortName { get; init; }

    /// <summary>When the person last signed in, with a password or a PIN, or null when it is not known (#209).</summary>
    public DateTimeOffset? LastLoginUtc { get; init; }

    /// <summary>How the person signs in, derived from <see cref="Provider"/> (<see cref="RoofLoginTypes"/>).</summary>
    public string LoginType => RoofLoginTypes.FromProvider(Provider);
}

/// <summary>A person, as the admin endpoints show them. Secrets are never returned: only whether each is set.</summary>
public sealed record RoofUserResponse(
    string Name,
    string Role,
    bool HasPassword,
    bool HasPin,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int ActiveSessions)
{
    /// <summary>The account has an explicit initial emulator commissioning grant, not a personal PIN.</summary>
    public bool HasTemporaryPin { get; init; }

    /// <summary>The person's display name (friendly name), or null when none is set (#209).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The person's short name, for a narrow place such as a kiosk, or null (#209).</summary>
    public string? ShortName { get; init; }

    /// <summary>
    /// When the person last signed in, with a password or a PIN, or null when they have not since the controller began
    /// recording it (#209).
    /// </summary>
    public DateTimeOffset? LastLoginUtc { get; init; }

    /// <summary>How the person signs in (<see cref="RoofLoginTypes"/>): <see cref="RoofLoginTypes.Local"/> for everyone in the identity store.</summary>
    public string LoginType { get; init; } = RoofLoginTypes.Local;
}

/// <summary>Body of <c>POST Identity/Users</c>. A person needs a password, a PIN or both.</summary>
public sealed record class RoofUserCreateRequest
{
    [Required]
    [JsonRequired]
    public string? Name { get; init; }

    /// <summary>One of <c>RoofViewer</c>, <c>RoofOperator</c>, <c>RoofAdmin</c>.</summary>
    [Required]
    [JsonRequired]
    public string? Role { get; init; }

    /// <summary>For signing in by name and password (web UI, CLI).</summary>
    public string? Password { get; init; }

    /// <summary>For signing in at a kiosk. Operators and admins only.</summary>
    public string? Pin { get; init; }

    /// <summary>Explicit emulator-only temporary kiosk access before a personal PIN is set. Requires a password and no PIN.</summary>
    public bool TemporaryPin { get; init; }

    /// <summary>Optional friendly name (<see cref="RoofIdentityContract.IsValidDisplayName"/>); empty means none.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional short name (<see cref="RoofIdentityContract.IsValidShortName"/>); empty means none.</summary>
    public string? ShortName { get; init; }

    // The password and PIN are left out, so the request can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Role = {Role}, Password = {(Password is null ? "(none)" : "(set)")}, Pin = {(Pin is null ? "(none)" : "(set)")}, ")
            .Append($"DisplayName = {DisplayName}, ShortName = {ShortName}");
        return true;
    }
}

/// <summary>
/// Body of <c>PUT Identity/Users/{name}</c>. <see cref="Role"/> is always sent; a null password or PIN leaves it as it
/// is. Changing the role or the password ends every session of the person; changing or removing the PIN ends their PIN
/// sessions. A null display or short name leaves it as it is, an empty one removes it; neither ends a session.
/// </summary>
public sealed record class RoofUserUpdateRequest
{
    [Required]
    [JsonRequired]
    public string? Role { get; init; }

    public string? Password { get; init; }

    public string? Pin { get; init; }

    public bool RemovePassword { get; init; }

    public bool RemovePin { get; init; }

    /// <summary>A new friendly name; null leaves it as it is, empty removes it (#209).</summary>
    public string? DisplayName { get; init; }

    /// <summary>A new short name; null leaves it as it is, empty removes it (#209).</summary>
    public string? ShortName { get; init; }

    // The password and PIN are left out, so the request can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Role = {Role}, Password = {(Password is null ? "(none)" : "(set)")}, Pin = {(Pin is null ? "(none)" : "(set)")}, ")
            .Append($"RemovePassword = {RemovePassword}, RemovePin = {RemovePin}, DisplayName = {DisplayName}, ShortName = {ShortName}");
        return true;
    }
}

/// <summary>An API key, as the admin endpoints show it. Never its value or hash.</summary>
public sealed record RoofApiKeyResponse(
    string Name,
    string Role,
    bool Kiosk,
    RoofApiKeySource Source,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? UpdatedUtc);

/// <summary>
/// Body of <c>POST Identity/ApiKeys</c>. The controller generates the key and returns it once. A kiosk key must have
/// the <c>RoofViewer</c> role: it reads status and stops, and a PIN unlocks more.
/// </summary>
public sealed record class RoofApiKeyCreateRequest
{
    [Required]
    [JsonRequired]
    public string? Name { get; init; }

    [Required]
    [JsonRequired]
    public string? Role { get; init; }

    public bool Kiosk { get; init; }
}

/// <summary>Body of <c>PUT Identity/ApiKeys/{name}</c>.</summary>
public sealed record class RoofApiKeyUpdateRequest
{
    [Required]
    [JsonRequired]
    public string? Role { get; init; }

    [Required]
    [JsonRequired]
    public bool? Kiosk { get; init; }
}

/// <summary>
/// A managed API key that was just created or rotated, with its value. The value is shown only in this response and
/// cannot be read again.
/// </summary>
public sealed record RoofApiKeySecretResponse(RoofApiKeyResponse Key, string Secret)
{
    // The secret is left out, so the record can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Key = {Key}");
        return true;
    }
}

/// <summary>An open session, as the admin endpoints show it. Never its token.</summary>
public sealed record RoofSessionInfoResponse(
    string Id,
    string Name,
    string Role,
    RoofCredentialKind Kind,
    string? Device,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);
