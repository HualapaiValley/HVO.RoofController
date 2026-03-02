namespace HVO.RoofControllerV4.iPad.Models;

/// <summary>
/// Represents a time-ordered notification displayed in the UI banner list.
/// </summary>
public sealed class NotificationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public NotificationLevel Level { get; init; } = NotificationLevel.Info;

    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public enum NotificationLevel
{
    Info,
    Success,
    Warning,
    Error
}
