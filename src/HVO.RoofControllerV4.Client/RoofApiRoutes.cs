namespace HVO.RoofControllerV4.Client;

/// <summary>Relative paths of the controller's endpoints, as the client calls them.</summary>
internal static class RoofApiRoutes
{
    private const string V4 = "api/v4.0/";
    private const string V1 = "api/v1.0/";

    public const string Status = V4 + "RoofControl/Status";
    public const string Mode = V4 + "RoofControl/Mode";
    public const string Open = V4 + "RoofControl/Open";
    public const string Close = V4 + "RoofControl/Close";
    public const string Stop = V4 + "RoofControl/Stop";
    public const string Lease = V4 + "RoofControl/Lease";
    public const string ClearFault = V4 + "RoofControl/ClearFault";
    public const string Configuration = V4 + "RoofControl/Configuration";

    public const string Session = V4 + "Auth/Session";
    public const string Pin = V4 + "Auth/Pin";
    public const string PinUsers = V4 + "Auth/Pin/Users";
    public const string Me = V4 + "Auth/Me";
    public const string Password = V4 + "Auth/Password";

    public const string Users = V4 + "Identity/Users";
    public const string ApiKeys = V4 + "Identity/ApiKeys";
    public const string Sessions = V4 + "Identity/Sessions";

    public const string Settings = V4 + "Settings";
    public const string SettingsCatalogue = V4 + "Settings/Catalogue";
    public const string SettingsReload = V4 + "Settings/Reload";
    public const string SettingsDiscard = V4 + "Settings/Discard";

    public const string Restart = V4 + "System/Restart";
    public const string SystemInformation = V1 + "System/info";
    public const string SystemMetrics = V1 + "System/metrics";

    public const string Health = "health";
    public const string HealthReady = "health/ready";
    public const string HealthLive = "health/live";

    /// <summary>The CA that issued the controller's certificate, anonymous (404 when a private CA did not issue it).</summary>
    public const string CaCertificate = "ca.crt";

    public static string User(string name) => $"{Users}/{Segment(name)}";

    public static string ApiKey(string name) => $"{ApiKeys}/{Segment(name)}";

    public static string RotateApiKey(string name) => $"{ApiKeys}/{Segment(name)}/Rotate";

    public static string IdentitySession(string id) => $"{Sessions}/{Segment(id)}";

    public static string SettingsGroup(string group) => $"{Settings}/{Segment(group)}";

    public static string Camera(int cameraId) => $"{V1}Camera/{cameraId}/mjpeg";

    /// <summary>Escapes a name for one path segment, so a name can never reach another route.</summary>
    private static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return Uri.EscapeDataString(value);
    }
}
