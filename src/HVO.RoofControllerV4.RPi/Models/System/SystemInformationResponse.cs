using System;

namespace HVO.RoofControllerV4.RPi.Models.System;

public sealed record SystemInformationResponse(
    string ApplicationName,
    string EnvironmentName,
    string MachineName,
    string OperatingSystemDescription,
    string FrameworkDescription,
    string ApplicationVersion,
    DateTimeOffset ProcessStartTimeUtc,
    double UptimeSeconds,
    DateTimeOffset GeneratedAtUtc);
