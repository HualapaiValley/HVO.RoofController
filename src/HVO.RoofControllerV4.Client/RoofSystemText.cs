using System.Globalization;
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

    /// <summary>The controller's version, host and resource use, as label and value.</summary>
    public static IEnumerable<(string Label, string Value)> DescribeInformation(SystemInformationResponse information, SystemRuntimeMetricsResponse metrics)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(metrics);
        yield return ("Application", $"{information.ApplicationName} {information.ApplicationVersion} ({information.EnvironmentName})");
        yield return ("Host", information.MachineName);
        yield return ("System", information.OperatingSystemDescription);
        yield return ("Runtime", information.FrameworkDescription);
        yield return ("Started", $"{RoofStatusText.Time(information.ProcessStartTimeUtc)} (up {Duration(TimeSpan.FromSeconds(information.UptimeSeconds))})");
        yield return ("Memory", $"{Bytes(metrics.WorkingSetBytes)} working set, {Bytes(metrics.ManagedMemoryBytes)} managed");
        yield return ("CPU", $"{metrics.CpuUsagePercent.ToString("0.0", CultureInfo.InvariantCulture)} %, {metrics.ThreadCount} threads");
    }

    public static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
        : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{value.Minutes}m {value.Seconds}s";

    public static string Bytes(long bytes) => bytes >= 1 << 20
        ? $"{(bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture)} MiB"
        : $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KiB";
}
