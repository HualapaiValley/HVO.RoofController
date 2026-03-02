using System;

namespace HVO.RoofControllerV4.RPi.Models.System;

public sealed record SystemRuntimeMetricsResponse(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long PeakWorkingSetBytes,
    long ManagedMemoryBytes,
    int ThreadCount,
    double CpuUsagePercent,
    TimeSpan TotalProcessorTime,
    double UptimeSeconds,
    DateTimeOffset GeneratedAtUtc);
