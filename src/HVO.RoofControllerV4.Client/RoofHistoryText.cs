using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for the roof command history (#206), so the web UI, the terminal and the kiosk show an
/// entry the same way. A test pins these strings; change them deliberately.
/// </summary>
public static class RoofHistoryText
{
    public const string Empty = "No roof commands are recorded.";

    public const string EmptyFiltered = "No roof commands match.";

    /// <summary>The columns of <see cref="DescribeRow"/>.</summary>
    public static IReadOnlyList<string> Headers { get; } = ["Time", "Command", "By", "Result", "Roof after", "From"];

    public static string DescribeCommand(RoofHistoryCommand command) => command switch
    {
        RoofHistoryCommand.Open => "Open",
        RoofHistoryCommand.Close => "Close",
        RoofHistoryCommand.Stop => "Stop",
        RoofHistoryCommand.ClearFault => "Clear fault",
        RoofHistoryCommand.Lease => "Lease renewal",
        _ => command.ToString()
    };

    /// <summary>
    /// The command, and for anonymous Stops kept as one entry, how many and when the last came:
    /// "Stop (12 times, last 2026-10-30 18:05:09Z)".
    /// </summary>
    public static string DescribeCommand(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var command = DescribeCommand(entry.Command);
        return entry.Count > 1
            ? $"{command} ({entry.Count.ToString(CultureInfo.InvariantCulture)} times, last {RoofStatusText.Time(entry.LastTimestampUtc ?? entry.TimestampUtc)})"
            : command;
    }

    /// <summary>Who sent it: "ada (signed in)", "ada (PIN at kiosk-1)", "ada (personal key)", "web (API key) for ada" or "anonymous".</summary>
    public static string DescribeWho(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.PrincipalKind == RoofHistoryPrincipalKind.Anonymous || string.IsNullOrEmpty(entry.User))
        {
            return "anonymous";
        }

        var who = entry.CredentialKind switch
        {
            RoofCredentialKind.Pin => entry.Device is { Length: > 0 } device ? $"{entry.User} (PIN at {device})" : $"{entry.User} (PIN)",
            RoofCredentialKind.Session => $"{entry.User} (signed in)",
            _ => entry.PrincipalKind == RoofHistoryPrincipalKind.Person ? $"{entry.User} (personal key)" : $"{entry.User} (API key)"
        };
        return entry.OnBehalfOf is { Length: > 0 } person ? $"{who} for {person}" : who;
    }

    /// <summary>
    /// "Accepted", or "Refused" with the controller's code: "Refused [FaultLatched]". An Open accepted while the safety
    /// conditions were not Safe says so: "Accepted (safety override, conditions unsafe)", or "Accepted (conditions unsafe)"
    /// when the safety interlock only advises.
    /// </summary>
    public static string DescribeResult(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.Accepted)
        {
            return entry.Code is { Length: > 0 } code ? $"Refused [{code}]" : "Refused";
        }

        var conditions = entry.SafetyState is { Length: > 0 } state ? $"conditions {state.ToLowerInvariant()}" : null;
        return (entry.SafetyOverride == true, conditions) switch
        {
            (true, { } notSafe) => $"Accepted (safety override, {notSafe})",
            (true, null) => "Accepted (safety override)",
            (false, { } notSafe) => $"Accepted ({notSafe})",
            _ => "Accepted"
        };
    }

    /// <summary>The roof after the command: its position, and a latched fault; "unknown" when the controller had no status.</summary>
    public static string DescribeOutcome(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status is not { } status)
        {
            return "unknown";
        }

        var position = RoofText.DescribePosition(status);
        return entry.IsFaultLatched == true ? $"{position}, fault latched" : position;
    }

    /// <summary>
    /// Where it came from: the program, as it named itself, and the address ("web UI, 192.168.1.20"). For a command the
    /// web UI sent, the address is the person's browser, as the web UI forwarded it.
    /// </summary>
    public static string DescribeSource(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var client = DescribeClient(entry.Client);
        return (client, entry.RemoteAddress) switch
        {
            (null, null) => "unknown",
            (null, { } address) => address,
            ({ } program, null) => program,
            ({ } program, { } address) => $"{program}, {address}"
        };
    }

    /// <summary>The program in a <c>User-Agent</c>, by the name people know it; null when there is none.</summary>
    public static string? DescribeClient(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var product = userAgent.Trim().Split(' ', 2)[0].Split('/', 2)[0];
        return product switch
        {
            "hvo-roof" => "hvo-roof",
            "hvo-roof-kiosk" => "kiosk",
            "hvo-roof-mac" => "Mac app",
            "HVO.RoofControllerV4.Web" => "web UI",
            _ => product.Length > 40 ? product[..40] : product
        };
    }

    /// <summary>One line per entry for a narrow view: "2026-10-30 12:00:01Z  Open  Accepted".</summary>
    public static string DescribeLine(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{RoofStatusText.Time(entry.TimestampUtc)}  {DescribeCommand(entry)}  {DescribeResult(entry)}";
    }

    /// <summary>An entry's cells, in the order of <see cref="Headers"/>.</summary>
    public static IReadOnlyList<string> DescribeRow(RoofCommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return
        [
            RoofStatusText.Time(entry.TimestampUtc),
            DescribeCommand(entry),
            DescribeWho(entry),
            DescribeResult(entry),
            DescribeOutcome(entry),
            DescribeSource(entry)
        ];
    }

    /// <summary>Whose commands the page holds.</summary>
    public static string DescribeScope(RoofCommandHistoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.AllUsers ? "Everyone's roof commands (admin)." : "Your own roof commands.";
    }

    /// <summary>Which entries the page holds: "1–50 of 120 commands", or none.</summary>
    public static string DescribeRange(RoofCommandHistoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Entries.Count == 0)
        {
            return page.TotalCount == 0 ? "No commands." : $"No commands on this page, of {Count(page.TotalCount)}.";
        }

        var first = page.Skip + 1;
        var last = page.Skip + page.Entries.Count;
        return $"{first.ToString(CultureInfo.InvariantCulture)}–{last.ToString(CultureInfo.InvariantCulture)} of {Count(page.TotalCount)}.";
    }

    /// <summary>How long the controller keeps the history, and whether a restart loses it.</summary>
    public static string DescribeRetention(RoofCommandHistoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var days = page.RetentionDays.ToString("0.##", CultureInfo.InvariantCulture);
        var kept = $"The controller keeps commands for {days} {(page.RetentionDays == 1 ? "day" : "days")}, up to {page.MaximumEntries.ToString("N0", CultureInfo.InvariantCulture)}.";
        return page.IsPersistent ? kept : kept + " It keeps them in memory only, so a restart loses them.";
    }

    private static string Count(int total)
        => total == 1 ? "1 command" : $"{total.ToString("N0", CultureInfo.InvariantCulture)} commands";
}
