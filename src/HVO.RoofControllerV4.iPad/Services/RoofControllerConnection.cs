using System.Diagnostics.CodeAnalysis;
using HVO.RoofControllerV4.iPad.Configuration;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// The controller the app talks to: API base, site root, camera URL and API key, always switched together.
/// </summary>
public sealed class RoofControllerEndpoint
{
    private const string DefaultApiPath = "/api/v4.0/";

    private RoofControllerEndpoint(Uri apiBaseUri, Uri rootUri, Uri? cameraStreamUri, string? apiKey, int requestAttempts)
    {
        ApiBaseUri = apiBaseUri;
        RootUri = rootUri;
        CameraStreamUri = cameraStreamUri;
        ApiKey = apiKey;
        RequestAttempts = requestAttempts;
    }

    /// <summary>Attempts (1-3) for idempotent reads against this controller.</summary>
    public int RequestAttempts { get; }

    /// <summary>Versioned API base, always ending in '/', for example <c>https://roof.local:8443/api/v4.0/</c>.</summary>
    public Uri ApiBaseUri { get; }

    /// <summary>Site root (<c>scheme://host:port/</c>) used for <c>/health</c> and the camera routes.</summary>
    public Uri RootUri { get; }

    public Uri? CameraStreamUri { get; }

    public string? ApiKey { get; }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    public bool IsHttps => RootUri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Host and port shown to the operator so they know which controller they are commanding.</summary>
    public string DisplayHost => RootUri.IsDefaultPort ? RootUri.Host : $"{RootUri.Host}:{RootUri.Port}";

    public Uri RoofControlUri(string action) => new(ApiBaseUri, "RoofControl/" + action);

    public Uri HealthUri => new(RootUri, "health");

    /// <summary>
    /// Validates the URLs (absolute http/https only). A base URL without a path gets the default <c>/api/v4.0/</c>.
    /// </summary>
    public static bool TryCreate(string? baseUrl, string? cameraStreamUrl, string? apiKey, [NotNullWhen(true)] out RoofControllerEndpoint? endpoint, [NotNullWhen(false)] out string? error, int requestAttempts = 1)
    {
        endpoint = null;

        if (!RoofControllerApiOptions.TryParseHttpUri(baseUrl, out var baseUri))
        {
            error = "The controller URL must be an absolute http:// or https:// address.";
            return false;
        }

        Uri? cameraUri = null;
        if (!string.IsNullOrWhiteSpace(cameraStreamUrl) && !RoofControllerApiOptions.TryParseHttpUri(cameraStreamUrl, out cameraUri))
        {
            error = "The camera stream URL must be an absolute http:// or https:// address.";
            return false;
        }

        var path = baseUri.AbsolutePath;
        if (path.Length <= 1)
        {
            path = DefaultApiPath;
        }
        else if (!path.EndsWith('/'))
        {
            path += "/";
        }

        var root = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        var apiBase = new Uri(root, path);
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        endpoint = new RoofControllerEndpoint(apiBase, root, cameraUri, key, Math.Clamp(requestAttempts, 1, RoofControllerApiOptions.MaxRequestAttempts));
        error = null;
        return true;
    }

    /// <summary>Never includes the API key.</summary>
    public override string ToString() => ApiBaseUri.ToString();
}

/// <summary>
/// A consistent view of the active endpoint. <see cref="Cancellation"/> fires when the endpoint is replaced, which
/// cancels in-flight requests to the previous controller.
/// </summary>
public readonly record struct RoofControllerConnectionSnapshot(RoofControllerEndpoint? Endpoint, long Generation, CancellationToken Cancellation);

/// <summary>
/// Holds the active <see cref="RoofControllerEndpoint"/>. Applying a new endpoint is atomic: requests either use the
/// old endpoint (and are cancelled, except Stop) or the new one, never a mix.
/// </summary>
public sealed class RoofControllerConnection
{
    private readonly object _sync = new();
    private RoofControllerEndpoint? _endpoint;
    private CancellationTokenSource _lifetime = new();
    private long _generation;

    public event EventHandler? Changed;

    public RoofControllerEndpoint? Endpoint
    {
        get
        {
            lock (_sync)
            {
                return _endpoint;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public RoofControllerConnectionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new RoofControllerConnectionSnapshot(_endpoint, _generation, _lifetime.Token);
        }
    }

    /// <summary>
    /// Makes <paramref name="endpoint"/> active and cancels requests issued against the previous endpoint.
    /// Returns the new generation.
    /// </summary>
    public long Apply(RoofControllerEndpoint? endpoint)
    {
        CancellationTokenSource previous;
        long generation;

        lock (_sync)
        {
            previous = _lifetime;
            _lifetime = new CancellationTokenSource();
            _endpoint = endpoint;
            generation = ++_generation;
        }

        // Not disposed: tokens from it may still be linked by requests that are finishing.
        previous.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
        return generation;
    }
}
