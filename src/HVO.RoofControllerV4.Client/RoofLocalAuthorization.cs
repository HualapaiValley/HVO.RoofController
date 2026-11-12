using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>Maps protected application requests to native authorization categories. Unknown routes fail closed.</summary>
internal static class RoofLocalAuthorization
{
    internal static async Task<RoofCredential> ObtainAsync(
        Func<string, CancellationToken, Task<RoofCredential>> authorize, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await authorize(operation, cancellationToken).ConfigureAwait(false)
                ?? throw new RoofLocalAuthorizationException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never include a helper response or assertion in user-facing errors or application logs.
            throw new RoofLocalAuthorizationException();
        }
    }

    internal static string Operation(HttpMethod method, string path)
    {
        if (path == RoofApiRoutes.Me || path == RoofApiRoutes.Stop)
        {
            return "read";
        }

        if (path == RoofApiRoutes.Principals || path.StartsWith(RoofApiRoutes.Principals + "/", StringComparison.Ordinal)
            || path == RoofApiRoutes.Users || path.StartsWith(RoofApiRoutes.Users + "/", StringComparison.Ordinal)
            || path == RoofApiRoutes.ApiKeys || path.StartsWith(RoofApiRoutes.ApiKeys + "/", StringComparison.Ordinal)
            || path == RoofApiRoutes.Sessions || path.StartsWith(RoofApiRoutes.Sessions + "/", StringComparison.Ordinal))
        {
            return "identity";
        }

        if (path is RoofApiRoutes.Open or RoofApiRoutes.Close or RoofApiRoutes.Lease or RoofApiRoutes.ClearFault
            || IsCommandWithQuery(path))
        {
            return "operate";
        }

        // The person-only Alpaca routes (#220): grants, the Stop hold, the operator lease, acknowledging alerts.
        if (AlpacaOperation(method, path) is { } alpaca)
        {
            return alpaca;
        }

        if (path is RoofApiRoutes.Configuration or RoofApiRoutes.Settings or RoofApiRoutes.SettingsCatalogue
            or RoofApiRoutes.SettingsReload or RoofApiRoutes.SettingsDiscard or RoofApiRoutes.Restart
            || path.StartsWith(RoofApiRoutes.Settings + "/", StringComparison.Ordinal))
        {
            return method == HttpMethod.Get ? "read" : "configure";
        }

        if (method == HttpMethod.Get && (path is RoofApiRoutes.Health or RoofApiRoutes.Status or RoofApiRoutes.SystemSummary or RoofApiRoutes.SystemInformation or RoofApiRoutes.SystemMetrics
            || path.StartsWith(RoofApiRoutes.History + "?", StringComparison.Ordinal)
            || path.StartsWith("api/v1.0/Camera/", StringComparison.Ordinal)))
        {
            return "read";
        }

        throw new RoofLocalRouteUnavailableException();
    }

    /// <summary>
    /// The person-only Alpaca requests and the two reads beside them (Refs #260): each has its category only exactly as
    /// <see cref="RoofApiRoutes"/> builds its path and <see cref="RoofAlpacaApi"/> sends it, method included. So a path
    /// that only starts as one of them (<c>leaseExtra</c>, <c>grants/Extra</c>, <c>alerts/1/acknowledge/Extra</c>),
    /// another query on one, or another method has none here, and fails closed as every unknown request does.
    /// </summary>
    private static string? AlpacaOperation(HttpMethod method, string path)
    {
        if (method == HttpMethod.Get)
        {
            return path is RoofApiRoutes.AlpacaGrants or RoofApiRoutes.AlpacaAlerts ? "read" : null;
        }

        var known = method == HttpMethod.Post
            ? path == RoofApiRoutes.AlpacaGrants
                || path == RoofApiRoutes.AlpacaLeaseHeartbeat(input: false) || path == RoofApiRoutes.AlpacaLeaseHeartbeat(input: true)
                || IsAlertAcknowledgement(path)
            : method == HttpMethod.Delete
                && (path is RoofApiRoutes.AlpacaHold or RoofApiRoutes.AlpacaLease || IsGrantOfOneKind(path));
        return known ? "operate" : null;
    }

    /// <summary>The grant of one command kind, exactly as <see cref="RoofApiRoutes.AlpacaGrant"/> names it.</summary>
    private static bool IsGrantOfOneKind(string path)
    {
        foreach (var kind in Enum.GetValues<RoofAlpacaCommandKind>())
        {
            if (path == RoofApiRoutes.AlpacaGrant(kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Acknowledging one alert. The ID is the one segment between the alerts and <c>acknowledge</c>, and the path must
    /// be what <see cref="RoofApiRoutes.AcknowledgeAlpacaAlert"/> makes of that ID. An ID that is a dot segment has no
    /// category: the request's address resolves it away, so the request would go to another path than this one.
    /// </summary>
    private static bool IsAlertAcknowledgement(string path)
    {
        const string before = RoofApiRoutes.AlpacaAlerts + "/";
        const string after = "/acknowledge";
        if (path.Length <= before.Length + after.Length
            || !path.StartsWith(before, StringComparison.Ordinal) || !path.EndsWith(after, StringComparison.Ordinal))
        {
            return false;
        }

        var id = Uri.UnescapeDataString(path[before.Length..^after.Length]);
        return id is not ("." or "..") && path == RoofApiRoutes.AcknowledgeAlpacaAlert(id);
    }

    /// <summary>
    /// Open with the safety override and ClearFault with a pulse length (#255): the same operate commands as without
    /// the query. Each matches only exactly as <see cref="RoofApiRoutes"/> builds it, so another path (a near miss such
    /// as <c>OpenExtra</c> or <c>ClearFault/Extra</c>) or another query on the same path still fails closed.
    /// </summary>
    private static bool IsCommandWithQuery(string path)
    {
        if (path == RoofApiRoutes.OpenCommand(overrideSafety: true))
        {
            return true;
        }

        // The pulse length is the number after the last '='; the path must be what the builder makes of that number.
        var value = path.LastIndexOf('=');
        return value >= 0
            && int.TryParse(path.AsSpan(value + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pulse)
            && path == RoofApiRoutes.ClearFaultCommand(pulse);
    }
}


/// <summary>The native authorization gate refused or could not be completed. Remote credentials never substitute.</summary>
public sealed class RoofLocalAuthorizationException : Exception
{
    public RoofLocalAuthorizationException()
        : base("Local Linux authorization failed. The installed helper and current sudo authorization are required; saved web credentials are not used.")
    {
    }
}

/// <summary>The request has no native authorization category and was refused before sending it.</summary>
public sealed class RoofLocalRouteUnavailableException : InvalidOperationException
{
    /// <summary>Creates a refusal without including the request path or credentials.</summary>
    public RoofLocalRouteUnavailableException()
        : base("This request is unavailable in local authorization mode. Use explicit remote sign-in for account credentials.")
    {
    }
}
