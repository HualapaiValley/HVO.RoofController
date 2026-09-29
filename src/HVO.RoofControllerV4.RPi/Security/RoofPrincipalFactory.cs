using System;
using System.Collections.Generic;
using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Builds the principal for an authenticated API key or session. Used by the <c>ApiKey</c> and session handlers so both
/// carry the same claims. Roles are hierarchical: an Admin principal also carries the
/// Operator and Viewer role claims, so <c>IsInRole(RoofViewer)</c> is true for every authenticated caller.
/// </summary>
public static class RoofPrincipalFactory
{
    /// <summary>Claim carrying a non-secret identifier of the key that authenticated the principal.</summary>
    public const string KeyIdClaimType = "hvo:roof:key_id";

    /// <summary>Claim carrying how the caller proved who it is (a <see cref="RoofCredentialKind"/> name).</summary>
    public const string CredentialClaimType = "hvo:roof:credential";

    /// <summary>Claim carrying a session's non-secret identifier.</summary>
    public const string SessionIdClaimType = "hvo:roof:session_id";

    /// <summary>For a PIN session: the name of the kiosk key it was opened at.</summary>
    public const string DeviceClaimType = "hvo:roof:device";

    /// <summary>For a PIN session: the identifier of the kiosk key it was opened at.</summary>
    public const string DeviceKeyIdClaimType = "hvo:roof:device_key_id";

    /// <summary>Present (<c>true</c>) on a kiosk key's principal.</summary>
    public const string KioskClaimType = "hvo:roof:kiosk";

    /// <summary>
    /// The person an API key's holder says it acts for (<see cref="RoofIdentityContract.OnBehalfOfHeaderName"/>). Recorded
    /// in audit logs only; it grants nothing.
    /// </summary>
    public const string OnBehalfOfClaimType = "hvo:roof:on_behalf_of";

    private static readonly string[] AdminRoles =
        [RoofControllerApiContract.AdminRole, RoofControllerApiContract.OperatorRole, RoofControllerApiContract.ViewerRole];

    private static readonly string[] OperatorRoles =
        [RoofControllerApiContract.OperatorRole, RoofControllerApiContract.ViewerRole];

    private static readonly string[] ViewerRoles = [RoofControllerApiContract.ViewerRole];

    /// <summary>Returns the canonical role name for <paramref name="role"/> (case-insensitive), or null when unknown.</summary>
    public static string? NormalizeRole(string? role)
    {
        if (string.Equals(role, RoofControllerApiContract.AdminRole, StringComparison.OrdinalIgnoreCase))
        {
            return RoofControllerApiContract.AdminRole;
        }

        if (string.Equals(role, RoofControllerApiContract.OperatorRole, StringComparison.OrdinalIgnoreCase))
        {
            return RoofControllerApiContract.OperatorRole;
        }

        if (string.Equals(role, RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase))
        {
            return RoofControllerApiContract.ViewerRole;
        }

        return null;
    }

    /// <summary>The role itself plus every role it implies, highest first. Empty for an unknown role.</summary>
    public static IReadOnlyList<string> GetImpliedRoles(string? role) => NormalizeRole(role) switch
    {
        RoofControllerApiContract.AdminRole => AdminRoles,
        RoofControllerApiContract.OperatorRole => OperatorRoles,
        RoofControllerApiContract.ViewerRole => ViewerRoles,
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// Creates the principal for <paramref name="identity"/> under <paramref name="authenticationType"/>.
    /// <paramref name="onBehalfOf"/> is kept only when it is a valid name.
    /// </summary>
    public static ClaimsPrincipal Create(RoofApiKeyIdentity identity, string authenticationType, string? onBehalfOf = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationType);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.NameIdentifier, identity.Name),
            new(KeyIdClaimType, identity.KeyId),
            new(CredentialClaimType, nameof(RoofCredentialKind.ApiKey))
        };

        if (identity.Kiosk)
        {
            claims.Add(new Claim(KioskClaimType, "true"));
        }

        if (RoofIdentityContract.IsValidName(onBehalfOf))
        {
            claims.Add(new Claim(OnBehalfOfClaimType, onBehalfOf!));
        }

        return Finish(claims, identity.Role, authenticationType);
    }

    /// <summary>Creates the principal for an open session.</summary>
    public static ClaimsPrincipal CreateForSession(StoredSession session, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationType);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, session.UserName),
            new(ClaimTypes.NameIdentifier, session.UserName),
            new(CredentialClaimType, session.Kind.ToString()),
            new(SessionIdClaimType, session.Id)
        };

        if (session.Kind == RoofCredentialKind.Pin)
        {
            claims.Add(new Claim(DeviceClaimType, session.Device ?? string.Empty));
            claims.Add(new Claim(DeviceKeyIdClaimType, session.DeviceKeyId ?? string.Empty));
        }

        return Finish(claims, session.Role, authenticationType);
    }

    /// <summary>
    /// How <paramref name="user"/> proved who it is, or null when it is not authenticated. A principal with a key
    /// identifier and no credential claim is an API key.
    /// </summary>
    public static RoofCredentialKind? GetCredentialKind(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var value = user.FindFirst(CredentialClaimType)?.Value;
        if (value is null)
        {
            return user.FindFirst(KeyIdClaimType) is null ? null : RoofCredentialKind.ApiKey;
        }

        return Enum.TryParse<RoofCredentialKind>(value, ignoreCase: false, out var kind) && Enum.IsDefined(kind) ? kind : null;
    }

    /// <summary>The highest roof role held by <paramref name="user"/>, or null when it holds none.</summary>
    public static string? GetHighestRole(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        foreach (var role in AdminRoles)
        {
            if (user.IsInRole(role))
            {
                return role;
            }
        }

        return null;
    }

    /// <summary>
    /// A non-secret identifier of the credential behind <paramref name="user"/> (the key identifier, or
    /// <c>session:&lt;id&gt;</c>), for per-credential limits; null when there is none.
    /// </summary>
    public static string? GetCredentialId(ClaimsPrincipal? user) => GetCredentialKind(user) switch
    {
        RoofCredentialKind.ApiKey => user!.FindFirst(KeyIdClaimType)?.Value,
        RoofCredentialKind.Session or RoofCredentialKind.Pin => user!.FindFirst(SessionIdClaimType)?.Value is { } id ? "session:" + id : null,
        _ => null
    };

    /// <summary>
    /// Who made a request, for audit logs: the key's name (with <c>for &lt;person&gt;</c> when the key's holder named
    /// someone), <c>&lt;person&gt; (signed in)</c>, <c>&lt;person&gt; (PIN at &lt;kiosk&gt;)</c>, or <c>anonymous</c>.
    /// </summary>
    public static string DescribeCaller(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } name })
        {
            return "anonymous";
        }

        return GetCredentialKind(user) switch
        {
            RoofCredentialKind.Session => $"{name} (signed in)",
            RoofCredentialKind.Pin => $"{name} (PIN at {user.FindFirst(DeviceClaimType)?.Value})",
            _ => user.FindFirst(OnBehalfOfClaimType)?.Value is { Length: > 0 } person ? $"{name} for {person}" : name
        };
    }

    private static ClaimsPrincipal Finish(List<Claim> claims, string role, string authenticationType)
    {
        foreach (var implied in GetImpliedRoles(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, implied));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}
