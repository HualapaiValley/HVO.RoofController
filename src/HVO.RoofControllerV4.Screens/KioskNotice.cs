namespace HVO.RoofControllerV4.Screens;

/// <summary>How much a notice matters: it colours the notice.</summary>
public enum KioskNoticeLevel
{
    Info,
    Warning,
    Danger
}

/// <summary>Something the kiosk tells the person: the answer to a command, or what the status feed reported.</summary>
public sealed record KioskNotice(string Text, KioskNoticeLevel Level, DateTimeOffset At);
