using System;

namespace HVO.RoofControllerV4.Common.Models;

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
