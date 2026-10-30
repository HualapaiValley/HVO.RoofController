using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// What the controller says about itself, for About: each value null while it is not known. Today only an
/// administrator's System read supplies any of them (<see cref="From"/>); a reader of the controller's information for
/// every caller fills the same record, and About shows it unchanged.
/// </summary>
public sealed record KioskControllerFacts
{
    /// <summary>Nothing known.</summary>
    public static KioskControllerFacts None { get; } = new();

    /// <summary>The controller's version, as <see cref="RoofProductVersion.Describe(string)"/> words it.</summary>
    public string? Version { get; init; }

    /// <summary>How long the controller has been running.</summary>
    public string? Uptime { get; init; }

    /// <summary>Processor use (load, temperature).</summary>
    public string? Cpu { get; init; }

    /// <summary>Memory use.</summary>
    public string? Memory { get; init; }

    /// <summary>Disk use.</summary>
    public string? Disk { get; init; }

    /// <summary>The controller host's IP addresses.</summary>
    public string? Addresses { get; init; }

    /// <summary>True when nothing is known.</summary>
    public bool IsEmpty => this == None;

    /// <summary>The facts an administrator's System read gives: the version, the uptime and the controller process's CPU and memory.</summary>
    public static KioskControllerFacts From(SystemInformationResponse information, SystemRuntimeMetricsResponse metrics)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(metrics);
        return new KioskControllerFacts
        {
            Version = RoofProductVersion.Describe(information.ApplicationVersion),
            Uptime = RoofSystemText.Duration(TimeSpan.FromSeconds(information.UptimeSeconds)),
            Cpu = $"{metrics.CpuUsagePercent.ToString("0.0", CultureInfo.InvariantCulture)} % (controller process)",
            Memory = $"{RoofSystemText.Bytes(metrics.WorkingSetBytes)} (controller process)"
        };
    }
}

/// <summary>A labelled value on About. <see cref="Name"/> is how the tests find its value.</summary>
/// <param name="IsKnown">False when <see cref="Value"/> is fallback text, shown quieter.</param>
public sealed record KioskAboutRow(string Name, string Label, string Value, bool IsKnown = true);

/// <summary>A titled card of About, with an optional note under its rows.</summary>
public sealed record KioskAboutSection(string Name, string Title, IReadOnlyList<KioskAboutRow> Rows, string? Note = null);

/// <summary>
/// About's contents, built from what is available now: this device's own version, the status feed's state and the
/// controller's name, and <see cref="KioskControllerFacts"/> when something supplied them. A value not known says why:
/// <see cref="NotConfigured"/>, <see cref="Unreachable"/> or <see cref="NotAvailable"/>. Facts are shown only while the
/// status is live, so a value from before a disconnection is never shown as current.
/// </summary>
public sealed record KioskAboutInfo(KioskAboutSection Device, KioskAboutSection Controller, KioskAboutSection Host)
{
    /// <summary>The controller refused this device's key: it has not been set up for this device (or no longer is).</summary>
    public const string NotConfigured = "Controller not configured yet";

    /// <summary>The status feed cannot reach the controller, or its status is stale.</summary>
    public const string Unreachable = "Controller unreachable";

    /// <summary>The controller is reachable but does not give this value to this session.</summary>
    public const string NotAvailable = "Not available";

    /// <summary>The PIN kiosk's access rules, under its own version.</summary>
    public const string PinPolicy = "PIN sessions are Operator-only, even an administrator's. Settings and System need a password sign-in in the Mac, web or CLI client. A PIN grants no host or terminal rights.";

    /// <summary>The Mac app's access rules, under its own version.</summary>
    public const string DesktopPolicy = "Permissions follow your password session. Settings and System keep the controller's role checks.";

    /// <summary>Builds About for <paramref name="view"/>.</summary>
    /// <param name="view">What the console shows now.</param>
    /// <param name="wording">How the device signs in: a PIN kiosk or the password app.</param>
    /// <param name="self">This device's own version, as label and value (<see cref="KioskSystemPanel.Self"/>).</param>
    /// <param name="facts">What is known of the controller's version and host.</param>
    public static KioskAboutInfo Describe(KioskView view, KioskWording wording, (string Label, string Value) self, KioskControllerFacts facts)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(wording);
        ArgumentNullException.ThrowIfNull(facts);
        var live = view.IsStarted && !view.FeedRefused && !view.IsUnreachable && !view.IsStale;
        var shown = live ? facts : KioskControllerFacts.None;
        var missing = view.FeedRefused ? NotConfigured : view.IsUnreachable || (view.IsStarted && view.StaleSince is not null) ? Unreachable : NotAvailable;
        KioskAboutRow Row(string name, string label, string? value) => value is { Length: > 0 } ? new(name, label, value) : new(name, label, missing, false);

        var connection = view.FeedRefused ? $"Refused this {wording.Device}'s key"
            : view.IsUnreachable ? "Unreachable"
            : view.Status is { } last && view.StaleSince is not null ? $"Stale: last status at {RoofStatusText.Time(last.SnapshotUtc)}"
            : !view.IsStarted || view.Status is null ? "Connecting…"
            : "Live";
        var device = new KioskAboutSection("about-device", self.Label, [new("about-client-version", "Version", self.Value)],
            wording.SignIn == KioskSignIn.Pin ? PinPolicy : DesktopPolicy);
        var controller = new KioskAboutSection("about-controller", "Controller",
        [
            new("about-controller-state", "Connection", connection, live),
            Row("about-controller-name", "Name", view.Status?.ControllerName),
            Row("about-controller-version", "Version", shown.Version),
            Row("about-controller-uptime", "Running for", shown.Uptime)
        ]);
        var host = new KioskAboutSection("about-host", "Controller host",
        [
            Row("about-cpu", "CPU", shown.Cpu),
            Row("about-memory", "Memory", shown.Memory),
            Row("about-disk", "Disk", shown.Disk),
            Row("about-addresses", "IP addresses", shown.Addresses)
        ], live && (shown.Version is null || shown.Disk is null || shown.Addresses is null) ? Explain(view, wording, shown) : null);
        return new(device, controller, host);
    }

    // Why a live controller's values are missing, and where they can be read today.
    private static string Explain(KioskView view, KioskWording wording, KioskControllerFacts facts)
        => wording.SignIn == KioskSignIn.Pin ? "The controller gives its version and resource use only to an administrator's password session: see System in the web UI or hvo-roof."
            : !view.IsAdmin ? "The controller gives its version and resource use only to an administrator: see System with an administrator's sign-in."
            : facts.IsEmpty ? "Open System to read the controller's version and resource use."
            : "Version, uptime, CPU and memory as last read in System. The controller does not report disk use or addresses yet.";
}
