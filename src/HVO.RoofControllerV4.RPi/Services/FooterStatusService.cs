using System;
using System.Collections.Generic;
using System.Linq;

namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// Provides shared footer status state for the application. Components can update the
/// left, center, or right regions of the global footer and the layout will refresh when
/// changes occur.
/// </summary>
public class FooterStatusService
{
    private readonly object _sync = new();
    private FooterStatusSnapshot _snapshot = FooterStatusSnapshot.Empty;

    public event Action? StatusChanged;

    /// <summary>
    /// Gets the current footer status snapshot.
    /// </summary>
    public FooterStatusSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Replaces the left notification list.
    /// </summary>
    public void SetLeftNotifications(IEnumerable<FooterNotification> notifications)
    {
        var items = notifications?.ToArray() ?? Array.Empty<FooterNotification>();
        UpdateSnapshot(static (current, data) => current with { LeftNotifications = data }, items);
    }

    /// <summary>
    /// Updates the center footer message.
    /// </summary>
    public void SetCenterMessage(FooterStatusMessage? message)
        => UpdateSnapshot(static (current, data) => current with { Center = data }, message);

    /// <summary>
    /// Updates the right footer message.
    /// </summary>
    public void SetRightMessage(FooterStatusMessage? message)
        => UpdateSnapshot(static (current, data) => current with { Right = data }, message);

    /// <summary>
    /// Resets the footer to an empty state.
    /// </summary>
    public void Reset()
        => UpdateSnapshot(static (_, _) => FooterStatusSnapshot.Empty, (object?)null);

    private void UpdateSnapshot<T>(Func<FooterStatusSnapshot, T, FooterStatusSnapshot> updater, T payload)
    {
        FooterStatusSnapshot updated;
        lock (_sync)
        {
            updated = updater(_snapshot, payload);
            _snapshot = updated;
        }

        StatusChanged?.Invoke();
    }
}

/// <summary>
/// Immutable snapshot of the footer state.
/// </summary>
public record class FooterStatusSnapshot
{
    public IReadOnlyList<FooterNotification> LeftNotifications { get; init; } = Array.Empty<FooterNotification>();
    public FooterStatusMessage? Center { get; init; }
    public FooterStatusMessage? Right { get; init; }

    public static FooterStatusSnapshot Empty { get; } = new();
}

/// <summary>
/// Represents a message rendered in the footer.
/// </summary>
public record class FooterStatusMessage(string Text, FooterStatusLevel Level = FooterStatusLevel.Info);

/// <summary>
/// Individual notification displayed in the footer.
/// </summary>
public record class FooterNotification(string Title, string Message, FooterStatusLevel Level, DateTime Timestamp);

/// <summary>
/// Severity levels supported by the footer badges.
/// </summary>
public enum FooterStatusLevel
{
    Info,
    Success,
    Warning,
    Error
}
