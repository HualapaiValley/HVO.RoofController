using System;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// The controller's version and the basic resource use of the machine it runs on, from <c>GET api/v4.0/System/Summary</c>
/// (#209). Every role may read it: a viewer, an operator or an admin, a kiosk key and a PIN session. It names no host,
/// user or file; the detailed host information (<see cref="SystemInformationResponse"/>,
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
    /// The controller's IPv4 and IPv6 addresses, IPv4 first, without loopback and link-local ones. In a container
    /// (<see cref="RunningInContainer"/>) on Docker's bridge network these are the container's addresses, not the
    /// host's.
    /// </summary>
    public string[] IpAddresses { get; init; } = [];

    /// <summary>True when the controller runs in a container (the .NET images set <c>DOTNET_RUNNING_IN_CONTAINER</c>).</summary>
    public bool RunningInContainer { get; init; }
}
