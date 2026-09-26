using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace HVO.RoofControllerV4.RPi.Logging;

public sealed record ConsoleLogEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception,
    IReadOnlyList<string> Scopes)
{
    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
