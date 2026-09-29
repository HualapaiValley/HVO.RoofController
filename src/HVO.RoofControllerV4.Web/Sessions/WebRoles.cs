using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>The controller's roles, as the web UI uses them: each role grants the ones below it.</summary>
public static class WebRoles
{
    public const string Viewer = RoofControllerApiContract.ViewerRole;

    public const string Operator = RoofControllerApiContract.OperatorRole;

    public const string Admin = RoofControllerApiContract.AdminRole;

    /// <summary>For <c>[Authorize(Roles = ...)]</c>: an operator or an admin.</summary>
    public const string OperatorOrAdmin = Operator + "," + Admin;

    /// <summary>The role's own name (case-insensitive), or null for a role the web UI does not know.</summary>
    public static string? Normalize(string? role)
        => role is null ? null
            : string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin
            : string.Equals(role, Operator, StringComparison.OrdinalIgnoreCase) ? Operator
            : string.Equals(role, Viewer, StringComparison.OrdinalIgnoreCase) ? Viewer
            : null;

    /// <summary>The role and every role it grants, highest first; empty for a role the web UI does not know.</summary>
    public static IReadOnlyList<string> Implied(string? role) => Normalize(role) switch
    {
        Admin => [Admin, Operator, Viewer],
        Operator => [Operator, Viewer],
        Viewer => [Viewer],
        _ => []
    };

    /// <summary>The role as people read it: Viewer, Operator or Admin.</summary>
    public static string Describe(string? role) => Normalize(role) switch
    {
        Admin => "Admin",
        Operator => "Operator",
        Viewer => "Viewer",
        _ => "Unknown role"
    };
}
