using System;
using System.Collections.Generic;
using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Builds the principal for an authenticated API key. Used by both the <c>ApiKey</c> handler and the console cookie
/// login so both carry the same claims. Roles are hierarchical: an Admin principal also carries the Operator and
/// Viewer role claims, so <c>IsInRole(RoofViewer)</c> is true for every authenticated key.
/// </summary>
public static class RoofPrincipalFactory
{
    /// <summary>Claim carrying a non-secret identifier of the key that authenticated the principal.</summary>
    public const string KeyIdClaimType = "hvo:roof:key_id";

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

    /// <summary>Creates the principal for <paramref name="identity"/> under <paramref name="authenticationType"/>.</summary>
    public static ClaimsPrincipal Create(RoofApiKeyIdentity identity, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationType);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.NameIdentifier, identity.Name),
            new(KeyIdClaimType, identity.KeyId)
        };

        foreach (var role in GetImpliedRoles(identity.Role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
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

    /// <summary>Name used in audit logs: the key name, or <c>anonymous</c>.</summary>
    public static string DescribeCaller(ClaimsPrincipal? user)
        => user?.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "anonymous";
}
