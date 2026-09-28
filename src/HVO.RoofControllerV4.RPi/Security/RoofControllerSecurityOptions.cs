using System.Collections.Generic;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Configuration section <c>RoofControllerSecurity</c>. API keys are secrets: supply them through environment
/// variables (<c>RoofControllerSecurity__ApiKeys__0__Key</c>) or Docker secrets mounted under <c>/run/secrets</c>,
/// never through a committed appsettings file.
/// </summary>
public sealed class RoofControllerSecurityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RoofControllerSecurity";

    /// <summary>Keys shorter than this are rejected at load time (use e.g. <c>openssl rand -base64 32</c>).</summary>
    public const int MinimumKeyLength = 24;

    /// <summary>
    /// Accepted API keys. Each entry grants one role; roles are hierarchical (Admin includes Operator includes Viewer).
    /// </summary>
    public List<RoofApiKeyOptions> ApiKeys { get; set; } = new();

    /// <summary>
    /// When true, <c>POST .../RoofControl/Stop</c> is accepted without credentials. Default false. Stop never starts
    /// motion, but enabling this lets anyone who can reach the controller interrupt an operator.
    /// </summary>
    public bool AllowAnonymousStop { get; set; }

    /// <summary>
    /// When true, requests that are not HTTPS are refused with 403 (<c>https_required</c>), except loopback
    /// connections and the anonymous <c>/health/live</c> and <c>/health/ready</c> probes. When unset, the default is
    /// true outside the Development environment.
    /// </summary>
    public bool? RequireHttps { get; set; }

    /// <summary>
    /// Extra origins (scheme://host[:port]) allowed to open the Blazor console and post to <c>/account/*</c> and
    /// <c>/console/*</c>, in addition to the request's own origin. Needed only behind a reverse proxy that changes the host name.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = new();
}

/// <summary>One accepted API key. Provide either <see cref="Key"/> or <see cref="KeySha256"/>, not both.</summary>
public sealed class RoofApiKeyOptions
{
    /// <summary>Identifies the key holder in logs and audit records (for example <c>console-operator</c>). Not secret.</summary>
    public string? Name { get; set; }

    /// <summary>One of <c>RoofViewer</c>, <c>RoofOperator</c>, <c>RoofAdmin</c>.</summary>
    public string? Role { get; set; }

    /// <summary>The key value itself (secret).</summary>
    public string? Key { get; set; }

    /// <summary>Lower- or upper-case hex SHA-256 of the key's UTF-8 bytes, as an alternative to storing the key.</summary>
    public string? KeySha256 { get; set; }

    /// <summary>
    /// True for a kiosk's key: people may sign in with a PIN at the device that holds it (<c>POST Auth/Pin</c>). A kiosk
    /// key must have the <c>RoofViewer</c> role; the PIN unlocks more.
    /// </summary>
    public bool Kiosk { get; set; }
}
