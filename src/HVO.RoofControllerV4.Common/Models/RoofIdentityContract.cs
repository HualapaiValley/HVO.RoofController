using System.ComponentModel.DataAnnotations;
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

    public const int MinimumPasswordLength = 12;

    public const int MaximumPasswordLength = 256;

    /// <summary>PINs are digits only.</summary>
    public const string PinPattern = "^[0-9]+$";

    public const int MinimumPinLength = 6;

    public const int MaximumPinLength = 12;

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
        if (pin is null || pin.Length < MinimumPinLength || pin.Length > MaximumPinLength)
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

    /// <summary>True when <paramref name="password"/> has an allowed length.</summary>
    public static bool IsValidPassword(string? password)
        => password is not null && password.Length >= MinimumPasswordLength && password.Length <= MaximumPasswordLength;
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
    bool IsKiosk);

/// <summary>A person who may sign in with a PIN at a kiosk, from <c>GET Auth/Pin/Users</c>.</summary>
public sealed record RoofPinUserResponse(string Name, string Role);

/// <summary>A person, as the admin endpoints show them. Secrets are never returned: only whether each is set.</summary>
public sealed record RoofUserResponse(
    string Name,
    string Role,
    bool HasPassword,
    bool HasPin,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int ActiveSessions);

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
}

/// <summary>
/// Body of <c>PUT Identity/Users/{name}</c>. <see cref="Role"/> is always sent; a null password or PIN leaves it as it
/// is. Changing the role or the password ends every session of the person; changing or removing the PIN ends their PIN
/// sessions.
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
