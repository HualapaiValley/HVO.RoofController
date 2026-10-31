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

    /// <summary>The External Alpaca account's role (#220): an API key's only, never a person's.</summary>
    public const string AlpacaCommander = RoofAlpacaContract.CommanderRole;

    /// <summary>The Alpaca server's own account's role (#220): an API key's only, never a person's.</summary>
    public const string AlpacaServer = RoofAlpacaContract.ServerRole;

    /// <summary>
    /// An API key's role (case-insensitive): a person's role, or one of the Alpaca accounts' roles; null for a role the
    /// controller does not give a key.
    /// </summary>
    public static string? NormalizeKeyRole(string? role)
        => Normalize(role)
            ?? (string.Equals(role, AlpacaCommander, StringComparison.OrdinalIgnoreCase) ? AlpacaCommander
                : string.Equals(role, AlpacaServer, StringComparison.OrdinalIgnoreCase) ? AlpacaServer
                : null);

    /// <summary>True for one of the Alpaca accounts' roles, which a kiosk's key never has.</summary>
    public static bool IsAlpacaAccount(string? role) => NormalizeKeyRole(role) is AlpacaCommander or AlpacaServer;

    /// <summary>
    /// The role as people read it: Viewer, Operator or Admin; for an API key, also the Alpaca accounts' roles, with the
    /// controller's name for each.
    /// </summary>
    public static string Describe(string? role) => NormalizeKeyRole(role) switch
    {
        Admin => "Admin",
        Operator => "Operator",
        Viewer => "Viewer",
        AlpacaCommander => "External Alpaca account (RoofAlpacaCommander)",
        AlpacaServer => "Alpaca server's own account (RoofAlpacaServer)",
        _ => "Unknown role"
    };

    /// <summary>What an API key with an Alpaca account's role is for, or null for any other role.</summary>
    public static string? DescribeAlpacaAccount(string? role) => NormalizeKeyRole(role) switch
    {
        AlpacaCommander => "The External Alpaca account: Alpaca clients send this key through the Alpaca server to close the roof while the controller allows Alpaca commands. It can read the status and send that Close, nothing else, and is never a kiosk's key.",
        AlpacaServer => "The Alpaca server's own account: the Alpaca server reads the status with it and reports the refusals it makes itself; its AbortSlew stops the roof without setting the Stop hold. It is never a kiosk's key.",
        _ => null
    };
}
