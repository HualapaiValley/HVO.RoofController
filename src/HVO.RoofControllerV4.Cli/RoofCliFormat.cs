using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// How the command line and the terminal interface show the roof. The words for a status's parts are
/// <see cref="RoofStatusText"/>'s, shared with the other clients; this adds the rows, the times and the tables.
/// </summary>
internal static class RoofCliFormat
{
    /// <summary>The status as label and value rows.</summary>
    public static IReadOnlyList<(string Label, string Value)> DescribeStatus(RoofStatusResponse status) => RoofStatusText.DescribeRows(status);

    public static string DescribeRoof(RoofStatusResponse status) => RoofStatusText.DescribeRoof(status);

    public static string DescribeLastStop(RoofStatusResponse status) => RoofStatusText.DescribeLastStop(status);

    public static string DescribeFault(RoofStatusResponse status) => RoofStatusText.DescribeFault(status);

    public static string DescribeRelays(RoofStatusResponse status) => RoofStatusText.DescribeRelays(status);

    public static string DescribeController(RoofStatusResponse status) => RoofStatusText.DescribeController(status);

    public static string DescribeInput(bool? active) => RoofStatusText.DescribeInput(active);

    /// <summary>One line for <c>status --watch</c>: the snapshot's time, then <see cref="DescribeState"/>.</summary>
    public static string DescribeLine(RoofStatusResponse status) => $"{Time(status.SnapshotUtc)}  {DescribeState(status)}";

    /// <summary>
    /// The roof's state in one line, without the snapshot's time: the position and motion, a fault, relays that could
    /// not be verified, the lease, and the last stop. <c>status --watch</c> writes a line when this changes.
    /// </summary>
    public static string DescribeState(RoofStatusResponse status)
    {
        var parts = new List<string> { DescribeRoof(status) };
        if (status.IsFaultLatched || status.IsClearFaultInProgress)
        {
            parts.Add("fault " + DescribeFault(status));
        }

        if (status.RelayRegisterState == RoofRelayRegisterState.Unverified)
        {
            parts.Add("relays UNVERIFIED");
        }

        if (status.LeaseSecondsRemaining is { } lease)
        {
            parts.Add($"lease {Seconds(lease)}");
        }

        parts.Add($"last stop: {RoofText.DescribeStopReason(status.LastStopReason)}");
        return string.Join("; ", parts);
    }

    /// <summary>A role as the CLI shows it: viewer, operator or admin; any other role as the controller named it.</summary>
    public static string Role(string role) => role switch
    {
        RoofControllerApiContract.ViewerRole => "viewer",
        RoofControllerApiContract.OperatorRole => "operator",
        RoofControllerApiContract.AdminRole => "admin",
        _ => role
    };

    /// <summary>viewer, operator or admin (or the controller's own names), as the controller's role; null for anything else.</summary>
    public static string? ParseRole(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "viewer" or "roofviewer" => RoofControllerApiContract.ViewerRole,
        "operator" or "roofoperator" => RoofControllerApiContract.OperatorRole,
        "admin" or "roofadmin" => RoofControllerApiContract.AdminRole,
        _ => null
    };

    public static string Time(DateTimeOffset value) => RoofStatusText.Time(value);

    public static string Seconds(double seconds) => RoofStatusText.Seconds(seconds);

    public static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
        : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{value.Minutes}m {value.Seconds}s";

    public static string Bytes(long bytes) => bytes >= 1 << 20
        ? $"{(bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture)} MiB"
        : $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KiB";

    /// <summary>Writes rows as <c>Label: value</c>, aligned.</summary>
    public static void WriteRows(TextWriter output, IEnumerable<(string Label, string Value)> rows)
    {
        var list = rows.ToList();
        var width = list.Count == 0 ? 0 : list.Max(row => row.Label.Length) + 1;
        foreach (var (label, value) in list)
        {
            output.WriteLine($"{(label + ":").PadRight(width)} {value}");
        }
    }

    /// <summary>Writes a table with a header row; columns are as wide as their widest cell.</summary>
    public static void WriteTable(TextWriter output, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var list = rows.ToList();
        var widths = headers.Select((header, column) => list.Select(row => row[column].Length).Append(header.Length).Max()).ToArray();
        output.WriteLine(Line(headers));
        foreach (var row in list)
        {
            output.WriteLine(Line(row));
        }

        string Line(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd();
    }
}
