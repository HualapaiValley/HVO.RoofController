using System;

namespace HVO.RoofControllerV4.RPi.Controllers.Camera;

/// <summary>
/// Configuration section <c>BlueIris</c> for the MJPEG camera proxy. <see cref="UserName"/> and <see cref="Password"/>
/// are secrets: supply them with <c>BlueIris__UserName</c> / <c>BlueIris__Password</c> environment variables or Docker
/// secrets, never in a committed appsettings file.
/// </summary>
public sealed class BlueIrisOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BlueIris";

    /// <summary>Blue Iris server base URL, e.g. <c>http://192.168.0.4:80</c>. The proxy is disabled when empty.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Blue Iris user (optional; set together with <see cref="Password"/>).</summary>
    public string? UserName { get; set; }

    /// <summary>Blue Iris password (optional; set together with <see cref="UserName"/>).</summary>
    public string? Password { get; set; }

    /// <summary>Maximum simultaneous proxied streams. Further requests get 503.</summary>
    public int MaxConcurrentStreams { get; set; } = 4;

    /// <summary>TCP connect timeout to Blue Iris.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Time allowed for Blue Iris to return response headers.</summary>
    public TimeSpan ResponseHeadersTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A stream that delivers no bytes for this long is closed.</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Absolute base URI when <see cref="BaseUrl"/> is a valid http(s) URL.</summary>
    public Uri? GetBaseUri()
        => Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>
    /// Describes why the proxy cannot be used, or null when it is configured. Never includes secret values.
    /// </summary>
    public string? GetConfigurationProblem()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return "The camera proxy is not configured (BlueIris:BaseUrl is empty).";
        }

        if (GetBaseUri() is null)
        {
            return "The camera proxy is misconfigured (BlueIris:BaseUrl is not an absolute http or https URL).";
        }

        if (string.IsNullOrEmpty(UserName) != string.IsNullOrEmpty(Password))
        {
            return "The camera proxy is misconfigured (set both BlueIris:UserName and BlueIris:Password, or neither).";
        }

        return null;
    }
}
