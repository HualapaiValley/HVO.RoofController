using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>What the kiosk shows, at one moment. A new view replaces it on every change.</summary>
public sealed record KioskView
{
    /// <summary>The view before the console started: nothing is offered yet, and Stop is.</summary>
    public static KioskView NotStarted { get; } = new()
    {
        OpenBlock = KioskText.Starting,
        CloseBlock = KioskText.Starting,
        ClearFaultBlock = KioskText.Starting
    };

    /// <summary>False until the console started its status feed.</summary>
    public bool IsStarted { get; init; }

    /// <summary>Who unlocked the kiosk with their PIN, or null while it is locked.</summary>
    public string? UnlockedBy { get; init; }

    /// <summary>The unlocking person's role (<see cref="RoofControllerApiContract.OperatorRole"/>…), or null while locked.</summary>
    public string? Role { get; init; }

    public bool IsUnlocked => UnlockedBy is not null;

    /// <summary>True when the unlocking person may open, close and clear a fault (the controller still decides).</summary>
    public bool IsOperator => Role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole;

    /// <summary>True when the unlocking person may restart the controller and change admin settings.</summary>
    public bool IsAdmin => Role == RoofControllerApiContract.AdminRole;

    /// <summary>The newest status, or null before the first.</summary>
    public RoofStatusResponse? Status { get; init; }

    public RoofStatusFeedState FeedState { get; init; }

    /// <summary>"live", "STALE", "connecting", "reconnecting", "unreachable", "refused" or "not connected".</summary>
    public string FeedLabel { get; init; } = "not connected";

    /// <summary>
    /// When the status shown may have stopped being the roof's: the feed went quiet or dropped, or the status came with a
    /// command's answer while the feed is not live. Null while the status is live, and before the first.
    /// </summary>
    public DateTimeOffset? StaleSince { get; init; }

    /// <summary>True when there is no status, or the one shown is only the last known state.</summary>
    public bool IsStale => Status is null || StaleSince is not null;

    /// <summary>Why the status is missing or stale, or null while it is live.</summary>
    public string? FeedBanner { get; init; }

    /// <summary>True when the controller refused the kiosk's device key.</summary>
    public bool FeedRefused { get; init; }

    /// <summary>True while the status feed cannot connect to the controller.</summary>
    public bool IsUnreachable { get; init; }

    /// <summary>The controller's warnings about the hardware it drives (emulated HAT, simulation, limits ignored).</summary>
    public IReadOnlyList<string> ModeWarnings { get; init; } = [];

    /// <summary>The command on its way ("Sending Open…"), or null.</summary>
    public string? Busy { get; init; }

    public bool CommandInFlight => Busy is not null;

    /// <summary>Why Open is not offered; null when it is.</summary>
    public string? OpenBlock { get; init; }

    /// <summary>Why Close is not offered; null when it is.</summary>
    public string? CloseBlock { get; init; }

    /// <summary>Why Clear fault is not offered; null when it is.</summary>
    public string? ClearFaultBlock { get; init; }

    /// <summary>True while the kiosk renews the operator lease of a motion it started.</summary>
    public bool HoldsLease { get; init; }

    /// <summary>The answer to the newest Stop, or <see cref="RoofStopText.AlwaysAvailable"/> before the first.</summary>
    public string StopMessage { get; init; } = RoofStopText.AlwaysAvailable;

    /// <summary>What is known of the newest Stop: <see cref="RoofStopOutcome.None"/> before the first.</summary>
    public RoofStopOutcome StopOutcome { get; init; }

    /// <summary>True while a Stop sent from the kiosk has not been answered.</summary>
    public bool StopInFlight { get; init; }

    /// <summary>True while the screen is blank: the next touch only wakes it.</summary>
    public bool IsBlank { get; init; }

    /// <summary>How long the screen stays on without a touch; zero keeps it on.</summary>
    public TimeSpan ScreenTimeout { get; init; }

    /// <summary>How long the kiosk stays unlocked without a touch.</summary>
    public TimeSpan IdleLock { get; init; }

    /// <summary>The newest notices, newest first.</summary>
    public IReadOnlyList<KioskNotice> Notices { get; init; } = [];
}
