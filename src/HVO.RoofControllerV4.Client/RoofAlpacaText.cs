using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words and rules every UI uses for the Alpaca command state (#220): which commands are on, the human Stop hold, the
/// operator lease, override grants, alerts and warnings, so the web UI, the terminal and the kiosk say the same. A test
/// pins these strings; change them deliberately.
/// </summary>
public static class RoofAlpacaText
{
    /// <summary>The panel's title.</summary>
    public const string Title = "Alpaca";

    /// <summary>The button that clears the human Stop hold.</summary>
    public const string ClearHoldLabel = "Allow Alpaca again";

    /// <summary>The button that grants an override for one Close, on a refused Close's alert.</summary>
    public const string GrantCloseLabel = "Allow this Close once";

    /// <summary>The button that revokes a grant.</summary>
    public const string RevokeLabel = "Revoke";

    /// <summary>The button that acknowledges an alert.</summary>
    public const string AcknowledgeLabel = "Acknowledge";

    /// <summary>Said once the person's operator lease lapsed for want of input.</summary>
    public const string MayNowMove = "Alpaca may now move the roof: your operator lease lapsed after no input.";

    /// <summary>How long before the operator lease idles out the UIs warn.</summary>
    public static readonly TimeSpan IdleWarning = TimeSpan.FromMinutes(1);

    /// <summary>How often a UI renews the operator lease while it is open.</summary>
    public static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>What the commands setting allows: "Alpaca commands are off", "Alpaca may close the roof".</summary>
    public static string DescribeMode(RoofAlpacaCommandMode mode) => mode switch
    {
        RoofAlpacaCommandMode.CloseOnly => "Alpaca may close the roof (never open it)",
        RoofAlpacaCommandMode.OpenAndClose => "Alpaca may open and close the roof",
        _ => "Alpaca commands are off"
    };

    /// <summary>The human Stop hold: "Alpaca held after a Stop by ada (signed in)", or null when it is clear.</summary>
    public static string? DescribeHold(RoofStopHoldState? hold)
        => hold is { Held: true }
            ? $"Alpaca commands are held after a Stop{(hold.SetBy is { Length: > 0 } by ? $" ({by})" : string.Empty)}: a person must allow them again."
            : null;

    /// <summary>The operator lease: who holds it, and how long until it idles out, or null when nobody does.</summary>
    public static string? DescribeLease(RoofOperatorLeaseState? lease)
    {
        if (lease is not { Held: true })
        {
            return null;
        }

        var idle = lease.IdleSecondsRemaining is { } seconds
            ? $"; it lapses after {Duration(seconds)} without input"
            : string.Empty;
        return $"Operator lease held by {lease.Holder ?? "a person"}: Alpaca commands are refused{idle}.";
    }

    /// <summary>
    /// The idle warning shown in the last minute before the person's lease lapses, or <see cref="MayNowMove"/> once it has
    /// lapsed for them; null otherwise.
    /// </summary>
    public static string? DescribeIdleWarning(RoofOperatorLeaseState? lease, string? person)
    {
        if (lease is null || string.IsNullOrEmpty(person))
        {
            return null;
        }

        if (!lease.Held && string.Equals(lease.IdleLapsedHolder, person, StringComparison.Ordinal))
        {
            return MayNowMove;
        }

        return lease is { Held: true, IdleSecondsRemaining: { } seconds } && seconds <= IdleWarning.TotalSeconds && string.Equals(lease.Holder, person, StringComparison.Ordinal)
            ? $"Your operator lease lapses in {Duration(seconds)} without input; then Alpaca may move the roof. Touch anything to keep it."
            : null;
    }

    /// <summary>A grant: "One Close allowed (lifts: Telescope 'Main' unknown) by ada, 4 min left".</summary>
    public static string DescribeGrant(RoofOverrideGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return $"One {grant.Kind} allowed by {grant.GrantedBy} ({grant.Ui}), {Duration(grant.SecondsRemaining)} left; it lifts {DescribeReasons(grant.ReasonKeys)}.";
    }

    /// <summary>An alert, with its count and the client the Alpaca server reported.</summary>
    public static string DescribeAlert(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var count = alert.Count > 1 ? $" ({alert.Count.ToString(CultureInfo.InvariantCulture)} times, last {RoofStatusText.Time(alert.LastUtc)})" : $" ({RoofStatusText.Time(alert.LastUtc)})";
        var client = DescribeClient(alert);
        return $"{alert.Message}{count}{(client is null ? string.Empty : $" From {client}.")}";
    }

    /// <summary>The Alpaca client the server reported for an alert: "ClientID 7 at 192.168.1.50 (reported)", or null.</summary>
    public static string? DescribeClient(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (alert.ClientId is null && alert.ClientAddress is null)
        {
            return null;
        }

        var id = alert.ClientId is { } clientId ? $"ClientID {clientId.ToString(CultureInfo.InvariantCulture)}" : "a client";
        var address = alert.ClientAddress is { Length: > 0 } at ? $" at {at}" : string.Empty;
        return $"{id}{address} (reported by the Alpaca server)";
    }

    /// <summary>True for an alert a person may grant an override from: a refused Close whose every reason a grant can lift.</summary>
    public static bool CanGrantFrom(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return !alert.Acknowledged && alert.Kind == "Refused" && alert.Command == RoofAlpacaCommandKind.Close
            && alert.ReasonKeys.Count > 0
            && alert.ReasonKeys.All(key => key.StartsWith(RoofAlpacaContract.ScopeNotClearKind + ":", StringComparison.Ordinal)
                || key.StartsWith(RoofAlpacaContract.ScopeUnknownKind + ":", StringComparison.Ordinal));
    }

    /// <summary>
    /// The grant question's lines: what the grant lifts, for which client, for how long, and that nothing else is bypassed.
    /// </summary>
    public static IReadOnlyList<string> DescribeGrantQuestion(RoofAlpacaAlert alert, double windowMinutes)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var lines = new List<string>
        {
            $"Allow one Alpaca Close despite: {DescribeReasons(alert.ReasonKeys)}.",
        };
        if (DescribeClient(alert) is { } client)
        {
            lines.Add($"It was refused for {client}; any Alpaca client's next Close uses the grant.");
        }

        lines.Add($"The grant lasts {windowMinutes.ToString("0", CultureInfo.InvariantCulture)} min and is used up by one Close, which may still be refused for a new reason. "
            + "It never bypasses the fault latch, the interlocks, the limits, the motor protections, a person's operator lease or the Stop hold. It is logged with your name.");
        return lines;
    }

    /// <summary>Reason keys in words: "Telescope 'Main' not clear; Telescope 'Guide' unknown".</summary>
    public static string DescribeReasons(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return "nothing";
        }

        return string.Join("; ", keys.Select(key =>
        {
            var colon = key.IndexOf(':', StringComparison.Ordinal);
            var (kind, source) = colon > 0 ? (key[..colon], key[(colon + 1)..]) : (key, string.Empty);
            return kind switch
            {
                RoofAlpacaContract.ScopeNotClearKind => $"Telescope '{source}' not clear of the roof",
                RoofAlpacaContract.ScopeUnknownKind => $"Telescope '{source}' state unknown",
                _ => key
            };
        }));
    }

    /// <summary>The grant question's title, above <see cref="DescribeGrantQuestion"/>'s lines.</summary>
    public const string GrantQuestionTitle = "Allow one Alpaca Close?";

    /// <summary>The grant question's button that grants the override.</summary>
    public const string GrantConfirmLabel = "Allow one Close";

    /// <summary>The grant question's button that grants nothing.</summary>
    public const string GrantCancelLabel = "Cancel";

    /// <summary>Said when the person answered the grant question with Cancel.</summary>
    public const string GrantCancelled = "No Close was allowed: the question was cancelled.";

    /// <summary>Said when a Stop, or the end of the person's session, withdrew the grant question before its answer.</summary>
    public const string GrantWithdrawn = "No Close was allowed: Stop withdrew the question.";

    /// <summary>Said after a person cleared the human Stop hold.</summary>
    public const string HoldCleared = "Alpaca commands are no longer held after a Stop; every other Alpaca rule still applies.";

    /// <summary>
    /// Said to a person who granted an override and so stepped away from the operator lease: while their grant lasts Alpaca
    /// may close the roof once, and their next click or key takes the lease back, which cancels the grant.
    /// </summary>
    public static string DescribeSteppedAway(RoofOverrideGrant? grant)
        => grant is { Kind: RoofAlpacaCommandKind.Close }
            ? $"You stepped away: Alpaca may close the roof once in the next {Duration(grant.SecondsRemaining)}. Click or type on this page to take control back; that cancels the grant."
            : "You stepped away: Alpaca may move the roof. Click or type on this page to take control back.";

    /// <summary>Said after a person revoked a grant: "The grant for one Close is revoked."</summary>
    public static string DescribeRevoked(RoofAlpacaCommandKind kind) => $"The grant for one {kind} is revoked.";

    /// <summary>Who acknowledged an alert: "Acknowledged by ada.", or null while it is not acknowledged.</summary>
    public static string? DescribeAcknowledgement(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alert.Acknowledged
            ? $"Acknowledged{(alert.AcknowledgedBy is { Length: > 0 } by ? $" by {by}" : string.Empty)}."
            : null;
    }

    private static string Duration(double seconds)
        => seconds >= 90
            ? $"{Math.Ceiling(seconds / 60).ToString(CultureInfo.InvariantCulture)} min"
            : $"{Math.Max(0, Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture)} s";
}
