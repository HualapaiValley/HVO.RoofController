using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>A labelled value on About. <see cref="Name"/> is how the tests find its value.</summary>
/// <param name="IsKnown">False when <see cref="Value"/> is fallback text, shown quieter.</param>
public sealed record KioskAboutRow(string Name, string Label, string Value, bool IsKnown = true);

/// <summary>A titled card of About, with an optional note under its rows.</summary>
public sealed record KioskAboutSection(string Name, string Title, IReadOnlyList<KioskAboutRow> Rows, string? Note = null);

/// <summary>
/// About's contents: this device's own version, and the controller's summary (<c>GET System/Summary</c>, every role,
/// read with <see cref="KioskSystemPanel.LoadSummaryAsync"/> and worded by <see cref="RoofSystemText.DescribeSummary"/>)
/// in labelled cards. The controller's card has its version, then where people reach the web UI (from the host's
/// addresses, never a container's own); the host's card has the summary's other rows, the uptimes first. A value not known
/// says why: <see cref="NotConfigured"/>, <see cref="Unreachable"/>, <see cref="Reading"/> or <see cref="NotAvailable"/>.
/// The summary is shown only while the status is live, so a value from before a disconnection is never shown as current.
/// </summary>
public sealed record KioskAboutInfo(KioskAboutSection Device, KioskAboutSection Controller, KioskAboutSection Host)
{
    /// <summary>The controller refused this device's key: it has not been set up for this device (or no longer is).</summary>
    public const string NotConfigured = RoofSystemText.ControllerNotConfigured;

    /// <summary>The status feed cannot reach the controller, its status is stale, or the summary could not be reached.</summary>
    public const string Unreachable = RoofSystemText.ControllerUnreachable;

    /// <summary>The summary is on its way.</summary>
    public const string Reading = "Reading…";

    /// <summary>The controller did not give this value (<see cref="RoofSystemText.Unavailable"/>), or gave no summary.</summary>
    public const string NotAvailable = "Not available";

    /// <summary>The PIN kiosk's access rules, under its own version.</summary>
    public const string PinPolicy = "PIN sessions are Operator-only, even an administrator's, and grant no host or terminal rights.";

    /// <summary>The Mac app's access rules, under its own version.</summary>
    public const string DesktopPolicy = "Permissions follow your password session. Settings and System keep the controller's role checks.";

    // The summary's rows the controller's card shows, in this order (RoofSystemText.DescribeSummary's labels).
    private const string VersionLabel = "Controller";
    private const string WebUiLabel = "Web UI";

    // The host's card's rows before the summary is read.
    private static readonly string[] HostLabels = ["Uptime", "CPU", "Temperature", "Memory", "Disk", "Addresses"];

    /// <summary>Builds About for <paramref name="view"/>.</summary>
    /// <param name="view">What the console shows now.</param>
    /// <param name="wording">How the device signs in: a PIN kiosk or the password app.</param>
    /// <param name="self">This device's own version, as label and value (<see cref="KioskSystemPanel.Self"/>).</param>
    /// <param name="summary">The controller's summary as label and value (<see cref="KioskSystemPanel.Summary"/>), or empty.</param>
    /// <param name="problem">Why the summary could not be read (<see cref="KioskSystemPanel.SummaryProblem"/>), or null.</param>
    public static KioskAboutInfo Describe(
        KioskView view,
        KioskWording wording,
        (string Label, string Value) self,
        IReadOnlyList<(string Label, string Value)> summary,
        string? problem)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(wording);
        ArgumentNullException.ThrowIfNull(summary);
        var live = view.IsStarted && !view.FeedRefused && !view.IsUnreachable && !view.IsStale;
        var shown = live ? summary : [];
        var missing = view.FeedRefused ? NotConfigured
            : view.IsUnreachable || (view.IsStarted && view.StaleSince is not null) || problem == Unreachable ? Unreachable
            : live && shown.Count == 0 && problem is null ? Reading
            : NotAvailable;

        KioskAboutRow Row(string label, string name)
            => shown.FirstOrDefault(row => row.Label == label).Value is { Length: > 0 } value && value != RoofSystemText.Unavailable
                ? new(name, label, value)
                : new(name, label, missing, false);

        var device = new KioskAboutSection("about-device", self.Label, [new("about-client-version", "Version", self.Value)],
            wording.SignIn == KioskSignIn.Pin ? PinPolicy : DesktopPolicy);
        var controller = new KioskAboutSection("about-controller", "Controller",
        [
            Row(VersionLabel, "about-controller-version") with { Label = "Version" },
            Row(WebUiLabel, "about-web-ui")
        ]);
        var hostLabels = shown.Count > 0
            ? shown.Select(row => row.Label).Where(label => label is not (VersionLabel or WebUiLabel)).ToArray()
            : HostLabels;
        var host = new KioskAboutSection("about-host", "Controller host",
            [.. hostLabels.Select(label => Row(label, $"about-{label.ToLowerInvariant().Replace(' ', '-')}"))],
            live && problem is not null && problem != Unreachable ? problem : null);
        return new(device, controller, host);
    }
}
