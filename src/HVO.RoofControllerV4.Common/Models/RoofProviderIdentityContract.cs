using System.ComponentModel.DataAnnotations;
using System.Text;
namespace HVO.RoofControllerV4.Common.Models;

/// <summary>Opt-in provider authentication capabilities.</summary>
public sealed record RoofAuthProvidersResponse(bool LinuxEnabled, string[] Providers);
/// <summary>Provider-qualified password authentication. Passwords are never printed.</summary>
public sealed record RoofProviderSignInRequest
{
    [Required] public string Provider { get; init; } = "virtual";
    [Required] public string Name { get; init; } = "";
    [Required] public string Password { get; init; } = "";
    private bool PrintMembers(StringBuilder builder) { builder.Append($"Provider = {Provider}, Name = {Name}"); return true; }
}
/// <summary>Short-lived native authority assertion; never printed.</summary>
public sealed record RoofLocalSignInRequest
{
    [Required] public string Assertion { get; init; } = "";
    private bool PrintMembers(StringBuilder builder) { builder.Append("Assertion = (redacted)"); return true; }
}
/// <summary>Explicit Linux identity enrollment. Native and controller enrollment must agree.</summary>
public sealed record RoofPrincipalEnrollRequest
{
    [Required] public string Provider { get; init; } = "linux";
    [Required] public string Subject { get; init; } = "";
    [Required] public string Name { get; init; } = "";
    [Required] public string EnrollmentStamp { get; init; } = "";
    [Required] public string Role { get; init; } = "";
}
/// <summary>Controller enrollment; role changes revoke existing credentials.</summary>
public sealed record RoofPrincipalUpdateRequest
{
    [Required] public string Role { get; init; } = "";
    public bool Enabled { get; init; } = true;
}
/// <summary>Public provider identity; secrets are never returned.</summary>
public sealed record RoofPrincipalResponse(string Id, string Provider, string Subject, string Name, string Role, bool Enabled)
{
    /// <summary>When the principal last signed in (password, PIN or a local grant), or null when it is not known (#209).</summary>
    public DateTimeOffset? LastLoginUtc { get; init; }
}
/// <summary>Creates a personal automation key bounded by owner role and scopes.</summary>
public sealed record RoofOwnedKeyCreateRequest
{
    [Required] public string Name { get; init; } = "";
    [Required] public string[] Scopes { get; init; } = [];
    public DateTimeOffset ExpiresUtc { get; init; }
}
/// <summary>Personal key metadata, without its secret.</summary>
public sealed record RoofOwnedKeyResponse(string Id, string Name, string OwnerId, string[] Scopes, DateTimeOffset ExpiresUtc, bool Revoked);
/// <summary>A freshly generated personal key, returned once.</summary>
public sealed record RoofOwnedKeySecretResponse(RoofOwnedKeyResponse Key, string Secret)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append($"Key = {Key}"); return true; }
}
/// <summary>Scopes restrict a credential in addition to the controller role. Stop remains available to any authenticated role.</summary>
public static class RoofIdentityScopes
{
    public const string Read = "read";
    public const string Operate = "operate";
    public const string Configure = "configure";
    public const string Identity = "identity";
    public static bool IsValid(string value) => value is Read or Operate or Configure or Identity;
}
