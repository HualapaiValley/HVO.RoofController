using System;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Configuration section <c>RoofControllerUi</c>: preferences shared by every client (web UI, kiosk, CLI), read through
/// <c>GET Settings</c>. Operators may change them.
/// </summary>
public sealed record class RoofControllerUiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RoofControllerUi";

    /// <summary>Longest camera name.</summary>
    public const int MaximumCameraNameLength = 64;

    /// <summary>Longest kiosk screen timeout.</summary>
    public static readonly TimeSpan MaximumKioskScreenTimeout = TimeSpan.FromHours(4);

    /// <summary>The Blue Iris camera the clients show first (its short name), or null for the first one.</summary>
    public string? DefaultCamera { get; set; }

    /// <summary>How long the kiosk screen stays on without a touch. Zero keeps it on.</summary>
    public TimeSpan KioskScreenTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
