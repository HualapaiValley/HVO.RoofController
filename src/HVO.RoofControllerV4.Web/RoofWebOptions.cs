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
