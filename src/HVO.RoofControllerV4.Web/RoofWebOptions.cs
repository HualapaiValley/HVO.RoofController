using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Web;

/// <summary>
/// The web UI's settings: the <c>RoofWeb</c> section. In the container they come from the environment (RoofWeb__*),
/// which the supervisor passes to the web UI (and nothing else of the controller's settings).
/// </summary>
public sealed class RoofWebOptions
{
    public const string SectionName = "RoofWeb";

    /// <summary>The web UI's own port.</summary>
    public const int DefaultPort = 8088;

    /// <summary>Where the web UI listens, as ASP.NET Core URLs separated by semicolons. Default http://+:8088.</summary>
    public string Urls { get; set; } = $"http://+:{DefaultPort}";

    /// <summary>The controller's API, over the container's loopback. Default http://localhost:8080.</summary>
    public Uri ControllerUrl { get; set; } = new("http://localhost:8080");

    /// <summary>The certificate for an https:// URL in <see cref="Urls"/>.</summary>
    public RoofWebCertificateOptions Certificate { get; set; } = new();

    /// <summary>
    /// The container supervisor's state file (supervisor.json). Null or empty when the web UI runs without the
    /// supervisor, for example from an IDE; the pages then say so instead of reporting the supervisor's view.
    /// </summary>
    public string? SupervisorStatePath { get; set; }

    /// <summary>The supervisor's control directory, where a forced restart of the controller is requested.</summary>
    public string? SupervisorControlPath { get; set; }

    /// <summary>How often the pages check the controller's readiness and the supervisor's state, in seconds. Default 2.</summary>
    public int StatusRefreshSeconds { get; set; } = 2;

    /// <summary>
    /// A file holding the web UI's own API key for Stop (a Viewer key is enough; a trailing line break is ignored). The
    /// web UI sends it with a person's Stop, naming them in <c>X-On-Behalf-Of</c>, so Stop still works when their session
    /// has ended at the controller. None: Stop uses the person's session alone.
    /// </summary>
    public string? StopKeyFile { get; set; }

    /// <summary>
    /// Origins, besides the web UI's own, that may post its forms and open its live connection: for example
    /// <c>https://roof.example.org</c> when a proxy serves the web UI under another name. Scheme, host and port only.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Where the web UI keeps the keys that protect its sign-in cookies and forms, so they survive a restart. None: the
    /// keys live in memory, and everyone signs in again after the web UI restarts.
    /// </summary>
    public string? DataProtectionPath { get; set; }

    /// <summary>Sign-in attempts accepted from one address per minute; more are refused before the controller is asked. Default 10.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    /// <summary>The camera the roof page shows when <see cref="CameraIds"/> names none: the roof camera, camera 2.</summary>
    public const int DefaultCameraId = 2;

    /// <summary>The most cameras the roof page shows. Each open page streams every one, and the controller relays 4 streams by default.</summary>
    public const int MaximumCameras = 4;

    /// <summary>
    /// The cameras the roof page shows, by their number on the camera server (1 to 99), through the controller's camera
    /// proxy: for example <c>RoofWeb__CameraIds__0=2</c>. None: camera 2.
    /// </summary>
    public int[] CameraIds { get; set; } = [];

    /// <summary>The cameras the roof page shows: <see cref="CameraIds"/> without repeats, or <see cref="DefaultCameraId"/> when it names none.</summary>
    public IReadOnlyList<int> Cameras => CameraIds is { Length: > 0 } ? CameraIds.Distinct().ToArray() : [DefaultCameraId];

    /// <summary>The problems with these settings, empty when they are valid.</summary>
    public IReadOnlyList<string> Validate(bool isDevelopment)
    {
        var problems = new List<string>();
        var urls = SplitUrls(Urls);
        if (urls.Count == 0)
        {
            problems.Add($"{SectionName}:Urls must name at least one URL, such as http://+:{DefaultPort}.");
        }

        foreach (var url in urls)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{SectionName}:Urls has '{url}', which is not an http:// or https:// URL.");
            }
        }

        if (!ControllerUrl.IsAbsoluteUri || ControllerUrl.Scheme is not ("http" or "https"))
        {
            problems.Add($"{SectionName}:ControllerUrl must be an absolute http or https URL, such as http://localhost:8080.");
        }

        if (StatusRefreshSeconds is < 1 or > 60)
        {
            problems.Add($"{SectionName}:StatusRefreshSeconds must be between 1 and 60, got {StatusRefreshSeconds}.");
        }

        if (SignInAttemptsPerMinute is < 1 or > 1000)
        {
            problems.Add($"{SectionName}:SignInAttemptsPerMinute must be between 1 and 1000, got {SignInAttemptsPerMinute}.");
        }

        if (!string.IsNullOrWhiteSpace(StopKeyFile) && !File.Exists(StopKeyFile))
        {
            problems.Add($"{SectionName}:StopKeyFile names {StopKeyFile}, which does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(DataProtectionPath) && !Directory.Exists(DataProtectionPath))
        {
            problems.Add($"{SectionName}:DataProtectionPath names {DataProtectionPath}, which is not a directory.");
        }

        foreach (var camera in (CameraIds ?? []).Distinct())
        {
            if (camera is < RoofCameraApi.MinimumCameraId or > RoofCameraApi.MaximumCameraId)
            {
                problems.Add($"{SectionName}:CameraIds has {camera}, which is not a camera number from {RoofCameraApi.MinimumCameraId} to {RoofCameraApi.MaximumCameraId}.");
            }
        }

        if ((CameraIds ?? []).Distinct().Count() > MaximumCameras)
        {
            problems.Add($"{SectionName}:CameraIds names {CameraIds!.Distinct().Count()} cameras; the roof page shows at most {MaximumCameras}.");
        }

        foreach (var origin in AllowedOrigins ?? [])
        {
            if (!Security.OriginCheck.IsValidAllowedOrigin(origin))
            {
                problems.Add($"{SectionName}:AllowedOrigins has '{origin}', which is not an origin such as https://roof.example.org (scheme, host and port only).");
            }
        }

        var usesHttps = urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (usesHttps && string.IsNullOrWhiteSpace(Certificate.Path) && !isDevelopment)
        {
            problems.Add($"{SectionName}:Urls has an https:// URL, so {SectionName}:Certificate:Path must name the certificate (a .pfx file).");
        }

        if (!string.IsNullOrWhiteSpace(Certificate.Path) && !File.Exists(Certificate.Path))
        {
            problems.Add($"{SectionName}:Certificate:Path names {Certificate.Path}, which does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(Certificate.PasswordFile) && !File.Exists(Certificate.PasswordFile))
        {
            problems.Add($"{SectionName}:Certificate:PasswordFile names {Certificate.PasswordFile}, which does not exist.");
        }

        return problems;
    }

    /// <summary>The URLs in <see cref="Urls"/>, trimmed, without empty entries.</summary>
    public static IReadOnlyList<string> SplitUrls(string? urls)
        => (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The web UI's HTTPS certificate. The supervisor gives the web UI private copies of the controller's.</summary>
public sealed class RoofWebCertificateOptions
{
    /// <summary>The certificate and its key, as a PKCS#12 (.pfx) file.</summary>
    public string? Path { get; set; }

    /// <summary>A file holding the certificate's password (a trailing line break is ignored). None for no password.</summary>
    public string? PasswordFile { get; set; }
}
