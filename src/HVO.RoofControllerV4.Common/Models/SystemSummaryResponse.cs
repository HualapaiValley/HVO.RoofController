using System;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// The controller's version, where people reach its web UI, and the basic resource use of the machine it runs on, from
/// <c>GET api/v4.0/System/Summary</c> (#209). Every role may read it: a viewer, an operator or an admin, a kiosk key and
/// a PIN session. It names no host, user or file; the detailed host information (<see cref="SystemInformationResponse"/>,
/// <see cref="SystemRuntimeMetricsResponse"/>) stays admin-only. A value the controller could not read (another
/// operating system, or a file its container does not see) is null; clients show it as unavailable.
/// </summary>
/// <param name="ApplicationVersion">
/// The controller's product version, the version and the commit after a + when known (<c>RoofProductVersion.Of</c>).
/// </param>
/// <param name="UptimeSeconds">How long the controller's process has run.</param>
/// <param name="GeneratedAtUtc">When the controller read these values.</param>
public sealed record SystemSummaryResponse(string ApplicationVersion, double UptimeSeconds, DateTimeOffset GeneratedAtUtc)
{
    /// <summary>How long the machine has run (<c>/proc/uptime</c>; in a container, the host's).</summary>
    public double? HostUptimeSeconds { get; init; }

    /// <summary>The processors the controller may use (a container's CPU limit counts).</summary>
    public int ProcessorCount { get; init; }

    /// <summary>
    /// The machine's CPU use in percent, across all its processors (<c>/proc/stat</c>): since the previous summary when
    /// that was recent, otherwise over a short sample.
    /// </summary>
    public double? CpuUsagePercent { get; init; }

    /// <summary>The machine's load average over 1 minute (<c>/proc/loadavg</c>).</summary>
    public double? LoadAverage1Minute { get; init; }

    /// <summary>The machine's load average over 5 minutes.</summary>
    public double? LoadAverage5Minutes { get; init; }

    /// <summary>The machine's load average over 15 minutes.</summary>
    public double? LoadAverage15Minutes { get; init; }

    /// <summary>The CPU's temperature in degrees Celsius (<c>/sys/class/thermal/thermal_zone0/temp</c>, the Pi's SoC).</summary>
    public double? CpuTemperatureCelsius { get; init; }

    /// <summary>The machine's memory (<c>/proc/meminfo</c> MemTotal).</summary>
    public long? MemoryTotalBytes { get; init; }

    /// <summary>The memory available to new work without swapping (<c>/proc/meminfo</c> MemAvailable).</summary>
    public long? MemoryAvailableBytes { get; init; }

    /// <summary>The controller process's working set.</summary>
    public long ProcessWorkingSetBytes { get; init; }

    /// <summary>The size of the file system the controller keeps its data on (the identity store's directory).</summary>
    public long? DiskTotalBytes { get; init; }

    /// <summary>The space free on that file system for the controller.</summary>
    public long? DiskFreeBytes { get; init; }

    /// <summary>
    /// Where people reach the web UI, as absolute URLs (<c>https://192.0.2.10:8088/</c>): built from the host's
    /// addresses, the web UI's published port and its scheme by the installer or the deploy script at each deploy. With
    /// <see cref="WebUiAccess"/> <see cref="RoofWebUiAccess.LocalOnly"/>, the one URL on the host itself. Empty when the
    /// deployment did not say.
    /// </summary>
    public string[] WebUiUrls { get; init; } = [];

    /// <summary>
    /// Whether the web UI is published to the network (<see cref="RoofWebUiAccess"/>): <c>Network</c>, <c>LocalOnly</c>
    /// (only on the controller's machine), or <c>Unknown</c> when the deployment did not say. A string, so a later value
    /// does not break an older client.
    /// </summary>
    public string WebUiAccess { get; init; } = RoofWebUiAccess.Unknown;

    /// <summary>
    /// The host machine's LAN addresses, IPv4 first: as the installer or the deploy script recorded them for a controller
    /// in a container, or the controller's own (<see cref="IpAddresses"/>) when it runs directly on the host. Empty when
    /// they are not known.
    /// </summary>
    public string[] HostAddresses { get; init; } = [];

    /// <summary>
    /// The controller's own IPv4 and IPv6 addresses, IPv4 first, without loopback and link-local ones. In a container
    /// (<see cref="RunningInContainer"/>) on Docker's bridge network these are the container's, which nothing on the
    /// network reaches: never present them as where to reach the web UI (<see cref="WebUiUrls"/>).
    /// </summary>
    public string[] IpAddresses { get; init; } = [];

    /// <summary>True when the controller runs in a container (the .NET images set <c>DOTNET_RUNNING_IN_CONTAINER</c>).</summary>
    public bool RunningInContainer { get; init; }
}

/// <summary>Whether the web UI is published to the network (<see cref="SystemSummaryResponse.WebUiAccess"/>).</summary>
public static class RoofWebUiAccess
{
    /// <summary>Published on the host's network addresses: <see cref="SystemSummaryResponse.WebUiUrls"/> reach it.</summary>
    public const string Network = "Network";

    /// <summary>Published only on the controller's machine (a rig not open to the network): reachable there alone.</summary>
    public const string LocalOnly = "LocalOnly";

    /// <summary>The deployment did not say (a development run, or a controller deployed before #209).</summary>
    public const string Unknown = "Unknown";

    /// <summary>The access a deployment wrote (case-insensitive), or <see cref="Unknown"/>.</summary>
    public static string Parse(string? value)
        => string.Equals(value?.Trim(), Network, StringComparison.OrdinalIgnoreCase) ? Network
            : string.Equals(value?.Trim(), LocalOnly, StringComparison.OrdinalIgnoreCase) ? LocalOnly
            : Unknown;
}
