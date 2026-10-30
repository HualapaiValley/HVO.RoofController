using System.Globalization;
using System.Reflection;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for the controller itself: its version, host and resource use, and a restart, so the web
/// UI, the CLI and the terminal UI say the same.
/// </summary>
public static class RoofSystemText
{
    public const string RestartQuestion = "Restart the controller? It stops the roof first, and does not answer until it has started again.";

    /// <summary>Precedes a pending hand edit when a restart is asked for: the restart would load it.</summary>
    public const string RestartLoadsHandEdit = "The restart loads the settings file as it is now:";

    /// <summary>Follows the controller's answer to a restart.</summary>
    public const string RestartComingBack = "Readiness says when it is back; the status comes back by itself.";

    /// <summary>In place of the controller's information when the client has no controller to ask yet.</summary>
    public const string ControllerNotConfigured = "Controller not configured yet.";

    /// <summary>In place of the controller's information when the controller cannot be reached.</summary>
    public const string ControllerUnreachable = "Controller unreachable.";

    /// <summary>A value of the summary the controller could not read.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Why the controller's information is not shown: <see cref="ControllerUnreachable"/> for a controller that cannot be
    /// reached or did not answer in time, otherwise the shared wording for the failure (a refusal says why).
    /// </summary>
    public static string DescribeUnavailable(Exception error) => error switch
    {
        HttpRequestException or TimeoutException or TaskCanceledException { InnerException: TimeoutException } => ControllerUnreachable,
        _ => RoofText.DescribeFailure(error)
    };

    /// <summary>
    /// The controller's summary (#209), as label and value, for every role: its version, the uptimes, CPU, temperature,
    /// memory, disk and addresses. A value the controller could not read shows as <see cref="Unavailable"/>.
    /// </summary>
    public static IEnumerable<(string Label, string Value)> DescribeSummary(SystemSummaryResponse summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        yield return ("Controller", RoofProductVersion.Describe(summary.ApplicationVersion));
        var controllerUp = Duration(TimeSpan.FromSeconds(summary.UptimeSeconds));
        yield return ("Uptime", summary.HostUptimeSeconds is { } host
            ? $"controller {controllerUp}, host {Duration(TimeSpan.FromSeconds(host))}"
            : $"controller {controllerUp}");
        yield return ("CPU", DescribeCpu(summary));
        yield return ("Temperature", summary.CpuTemperatureCelsius is { } celsius
            ? $"{celsius.ToString("0.0", CultureInfo.InvariantCulture)} °C"
            : Unavailable);
        yield return ("Memory", summary is { MemoryTotalBytes: { } total and > 0, MemoryAvailableBytes: { } available }
            ? $"{Bytes(Math.Max(total - available, 0))} used of {Bytes(total)} ({Percent((total - available) * 100d / total)})"
            : Unavailable);
        yield return ("Disk", summary is { DiskTotalBytes: { } size and > 0, DiskFreeBytes: { } free }
            ? $"{Bytes(free)} free of {Bytes(size)}"
            : Unavailable);
        yield return ("Addresses", summary.IpAddresses is not { Length: > 0 } addresses
            ? "none found"
            : summary.RunningInContainer
                ? $"{string.Join(", ", addresses)} (the controller's container)"
                : string.Join(", ", addresses));
    }

    /// <summary>The controller's version, host and resource use, as label and value.</summary>
    public static IEnumerable<(string Label, string Value)> DescribeInformation(SystemInformationResponse information, SystemRuntimeMetricsResponse metrics)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(metrics);
        yield return ("Application", $"{information.ApplicationName} {RoofProductVersion.Describe(information.ApplicationVersion)} ({information.EnvironmentName})");
        yield return ("Host", information.MachineName);
        yield return ("System", information.OperatingSystemDescription);
        yield return ("Runtime", information.FrameworkDescription);
        yield return ("Started", $"{RoofStatusText.Time(information.ProcessStartTimeUtc)} (up {Duration(TimeSpan.FromSeconds(information.UptimeSeconds))})");
        yield return ("Memory", $"{Bytes(metrics.WorkingSetBytes)} working set, {Bytes(metrics.ManagedMemoryBytes)} managed");
        yield return ("CPU", $"{metrics.CpuUsagePercent.ToString("0.0", CultureInfo.InvariantCulture)} %, {metrics.ThreadCount} threads");
    }

    /// <summary>
    /// A client's own version, as label and value: the web UI, the terminal UI, the kiosk or the Mac app, which may be
    /// older or newer than the controller it talks to.
    /// </summary>
    public static (string Label, string Value) DescribeClient(string name, Assembly assembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(assembly);
        return (name, RoofProductVersion.Describe(assembly));
    }

    public static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
        : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{value.Minutes}m {value.Seconds}s";

    public static string Bytes(long bytes) => bytes >= 1L << 30
        ? $"{(bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture)} GiB"
        : bytes >= 1 << 20
        ? $"{(bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture)} MiB"
        : $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KiB";

    private static string DescribeCpu(SystemSummaryResponse summary)
    {
        var parts = new List<string> { summary.CpuUsagePercent is { } percent ? Percent(percent) : Unavailable };
        if (summary is { LoadAverage1Minute: { } one, LoadAverage5Minutes: { } five, LoadAverage15Minutes: { } fifteen })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"load {one:0.00} {five:0.00} {fifteen:0.00}"));
        }

        if (summary.ProcessorCount > 0)
        {
            parts.Add(summary.ProcessorCount == 1 ? "1 processor" : $"{summary.ProcessorCount} processors");
        }

        return string.Join(", ", parts);
    }

    private static string Percent(double value) => $"{value.ToString("0.0", CultureInfo.InvariantCulture)} %";
}
