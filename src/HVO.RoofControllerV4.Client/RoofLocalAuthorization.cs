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

        if (path is RoofApiRoutes.Open or RoofApiRoutes.Close or RoofApiRoutes.Lease or RoofApiRoutes.ClearFault)
        {
            return "operate";
        }

        if (path is RoofApiRoutes.Configuration or RoofApiRoutes.Settings or RoofApiRoutes.SettingsCatalogue
            or RoofApiRoutes.SettingsReload or RoofApiRoutes.SettingsDiscard or RoofApiRoutes.Restart
            || path.StartsWith(RoofApiRoutes.Settings + "/", StringComparison.Ordinal))
        {
            return method == HttpMethod.Get ? "read" : "configure";
        }

        if (method == HttpMethod.Get && (path is RoofApiRoutes.Health or RoofApiRoutes.Status or RoofApiRoutes.SystemInformation or RoofApiRoutes.SystemMetrics
            || path.StartsWith("api/v1.0/Camera/", StringComparison.Ordinal)))
        {
            return "read";
        }

        throw new InvalidOperationException("This request is unavailable in local authorization mode. Use explicit remote sign-in for account credentials.");
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
