using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// How the command line and the terminal interface show the roof. Every claim comes from a field the controller sent:
/// a verified relay register is not reported as contacts that moved, and an unknown input is shown as unknown.
/// </summary>
internal static class RoofCliFormat
{
    /// <summary>The status as label and value rows.</summary>
    public static IReadOnlyList<(string Label, string Value)> DescribeStatus(RoofStatusResponse status)
    {
        var rows = new List<(string, string)>
        {
            ("Roof", DescribeRoof(status)),
            ("Last stop", DescribeLastStop(status)),
            ("Fault", DescribeFault(status)),
            ("Limits", $"open {DescribeInput(status.IsOpenLimitActive)}, closed {DescribeInput(status.IsClosedLimitActive)}"),
            ("Drive fault", DescribeInput(status.IsDriveFaultActive)),
            ("Inputs", status.InputsHealthy ? "read OK" : DescribeFailures("reads failing", status.ConsecutiveInputReadFailures)),
            ("Relays", DescribeRelays(status))
        };

        if (status.LeaseSecondsRemaining is { } lease)
        {
            rows.Add(("Lease", $"{Seconds(lease)} left"));
        }

        if (status.IsWatchdogActive && status.WatchdogSecondsRemaining is { } watchdog)
        {
            rows.Add(("Watchdog", $"{Seconds(watchdog)} left"));
        }

        rows.Add(("Controller", DescribeController(status)));
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            rows.Add(("Last error", status.LastError!));
        }

        return rows;
    }

    public static string DescribeRoof(RoofStatusResponse status)
    {
        var position = RoofText.DescribePosition(status.Status);
        var motion = status.CommandedMotion switch
        {
            RoofMotionDirection.Opening => ", commanded to open",
            RoofMotionDirection.Closing => ", commanded to close",
            _ => string.Empty
        };
        var state = !status.IsInitialized ? " (controller not initialized)"
            : status.IsShuttingDown ? " (controller shutting down)"
            : string.Empty;
        return position + motion + state;
    }

    public static string DescribeLastStop(RoofStatusResponse status)
    {
        var text = RoofText.DescribeStopReason(status.LastStopReason);
        return status.LastTransitionUtc is { } at ? $"{text}, {Time(at)}" : text;
    }

    public static string DescribeFault(RoofStatusResponse status)
    {
        if (status.IsClearFaultInProgress)
        {
            return "clearing";
        }

        if (!status.IsFaultLatched)
        {
            return "none";
        }

        return status.LatchedFaultReason is { } reason ? $"LATCHED: {RoofText.DescribeStopReason(reason)}" : "LATCHED";
    }

    public static string DescribeRelays(RoofStatusResponse status)
    {
        var state = status.RelayRegisterState switch
        {
            RoofRelayRegisterState.Verified => "register read-back matched",
            RoofRelayRegisterState.Unverified => "UNVERIFIED: confirm at the roof that the motor has stopped",
            _ => "not read back yet"
        };
        return status.RelayRegisterReadsHealthy
            ? state
            : $"{state}; {DescribeFailures("register reads failing", status.ConsecutiveRelayReadFailures)}";
    }

    public static string DescribeController(RoofStatusResponse status)
    {
        var name = string.IsNullOrWhiteSpace(status.ControllerName) ? "controller" : status.ControllerName;
        var hat = status.HatMode switch
        {
            RoofHatMode.Physical => "physical HAT",
            RoofHatMode.Emulated => "emulated HAT",
            RoofHatMode.Simulation => "simulated registers",
            _ => "HAT not reported"
        };
        return $"{name}, {hat}, snapshot {status.StatusVersion} at {Time(status.SnapshotUtc)}";
    }

    public static string DescribeInput(bool? active) => active switch
    {
        true => "active",
        false => "inactive",
        null => "unknown"
    };

    /// <summary>One line for <c>status --watch</c>.</summary>
    public static string DescribeLine(RoofStatusResponse status)
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
        return $"{Time(status.SnapshotUtc)}  {string.Join("; ", parts)}";
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

    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Seconds(double seconds) => $"{Math.Max(0, seconds).ToString("0", CultureInfo.InvariantCulture)} s";

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

    private static string DescribeFailures(string text, int count) => count > 0 ? $"{text} ({count} in a row)" : text;
}
