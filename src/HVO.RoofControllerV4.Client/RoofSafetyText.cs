using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for the safety conditions (telescope park and weather, docs/safety-service.md): the
/// indicator, each source, and the override question asked before Open while the conditions are not Safe. Unknown is
/// worded as unsafe, never as safe. The reasons are the controller's, shown as they are.
/// </summary>
public static class RoofSafetyText
{
    /// <summary>The override question's title.</summary>
    public const string OverrideTitle = "Safety override";

    /// <summary>The answer that sends Open with the override.</summary>
    public const string OverrideConfirm = "Open anyway";

    /// <summary>The answer that sends nothing.</summary>
    public const string OverrideCancel = "Cancel";

    /// <summary>Said when the person answered the override question with Cancel.</summary>
    public const string OverrideCancelled = "Open not sent: the safety override was cancelled.";

    /// <summary>The question's last line: what the override does, and what it does not touch.</summary>
    public const string OverrideConsequence = "Open the roof anyway? The override is logged with your name. Close and Stop are never affected.";

    /// <summary>What Unknown means, said with it: it is not read as safe.</summary>
    public const string UnknownCountsAsUnsafe = "Unknown counts as unsafe: nothing confirms that the telescopes are parked and the weather is safe.";

    /// <summary>
    /// The override question's lines, before its answers: the conditions with their reasons, what Unknown means when
    /// they are Unknown, and <see cref="OverrideConsequence"/>.
    /// </summary>
    public static IReadOnlyList<string> DescribeOverrideQuestion(RoofSafetyStatus? safety)
    {
        var lines = new List<string> { DescribeConditions(safety) };
        if (safety is null or { State: RoofSafetyState.Unknown })
        {
            lines.Add(UnknownCountsAsUnsafe);
        }

        lines.Add(OverrideConsequence);
        return lines;
    }

    /// <summary>
    /// The conditions in one sentence: "The safety conditions are UNSAFE: Telescope 'Main' is not parked." or "The
    /// safety conditions are safe."
    /// </summary>
    public static string DescribeConditions(RoofSafetyStatus? safety)
    {
        if (safety is null)
        {
            return "The safety conditions are UNKNOWN: the controller did not report them.";
        }

        var state = safety.State switch
        {
            RoofSafetyState.Safe => "safe",
            RoofSafetyState.Unsafe => "UNSAFE",
            _ => "UNKNOWN"
        };
        var reasons = safety.State != RoofSafetyState.Safe && safety.Reasons.Count > 0 ? $": {string.Join("; ", safety.Reasons)}" : string.Empty;
        return $"The safety conditions are {state}{reasons}.";
    }

    /// <summary>
    /// What Open does with the conditions now, for the indicator: "Open asks for a safety override (logged)", "Open is
    /// blocked by the safety interlock", "Open goes ahead", or "Advisory: the safety interlock is off".
    /// </summary>
    public static string DescribeOpen(RoofSafetyStatus safety)
    {
        ArgumentNullException.ThrowIfNull(safety);
        return (safety.InterlockMode, safety.State) switch
        {
            (RoofSafetyInterlockMode.Off, _) => "Advisory: the safety interlock is off",
            (_, RoofSafetyState.Safe) => "Open goes ahead",
            (RoofSafetyInterlockMode.Block, _) => "Open is blocked by the safety interlock",
            _ => safety.OverrideRole == RoofControllerApiContract.AdminRole
                ? "Open asks an admin for a safety override (logged)"
                : "Open asks for a safety override (logged)"
        };
    }

    /// <summary>Said of a weather source set Ignored: shown, but it never decides the conditions.</summary>
    public const string IgnoredSource = "ignored";

    /// <summary>Said of a telescope that cannot block the roof: shown, but it never decides the conditions.</summary>
    public const string DoesNotBlockRoof = "does not block the roof";

    /// <summary>
    /// One source: its state and reason, the source's extra words when it gave any, and why it does not count when it
    /// does not: "Unsafe: Weather monitor 'Cloud' reports unsafe weather (rain)", "Unsafe: Telescope 'Solar' is not parked
    /// [does not block the roof]".
    /// </summary>
    public static string DescribeSource(RoofSafetySourceStatus source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var detail = string.IsNullOrWhiteSpace(source.Detail) ? string.Empty : $" ({source.Detail})";
        var counts = source.Counts ? string.Empty : $" [{(source.Kind == RoofSafetySourceKind.TelescopePark ? DoesNotBlockRoof : IgnoredSource)}]";
        return $"{RoofStatusText.DescribeSafetyState(source.State)}: {source.Reason}{detail}{counts}";
    }

    /// <summary>
    /// The indicator's short reason line: the first reason, and how many more there are ("Telescope 'Main' is not parked
    /// (+1 more)"); empty when Safe.
    /// </summary>
    public static string DescribeFirstReason(RoofSafetyStatus safety)
    {
        ArgumentNullException.ThrowIfNull(safety);
        if (safety.State == RoofSafetyState.Safe || safety.Reasons.Count == 0)
        {
            return string.Empty;
        }

        return safety.Reasons.Count == 1 ? safety.Reasons[0] : $"{safety.Reasons[0]} (+{safety.Reasons.Count - 1} more)";
    }
}

/// <summary>
/// The question a client asks before it sends Open with the safety override: the conditions as they were when it asked
/// (from the status, or from the controller's refusal), which <see cref="Lines"/> describe.
/// </summary>
/// <param name="Safety">The conditions; null when the controller did not report them.</param>
public sealed record RoofSafetyOverrideQuestion(RoofSafetyStatus? Safety)
{
    /// <summary>The question's title.</summary>
    public string Title => RoofSafetyText.OverrideTitle;

    /// <summary>The question's lines (<see cref="RoofSafetyText.DescribeOverrideQuestion"/>).</summary>
    public IReadOnlyList<string> Lines => RoofSafetyText.DescribeOverrideQuestion(Safety);
}
