using System;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// The separately supervised Alpaca server, for System/About and the CLI. No listener address, credential or path is
/// returned. Device client counts are separate: the same astronomy program may connect to both devices.
/// </summary>
public sealed record AlpacaServerSummary
{
    /// <summary>Whether deployment enabled the server; false by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether a valid heartbeat was read within the last three seconds. This is not controller readiness.</summary>
    public bool Running { get; init; }

    /// <summary>The published port, when enabled. This is the port discovery advertises.</summary>
    public int? Port { get; init; }

    /// <summary>Dome 0's persisted UniqueID, when a heartbeat reported it.</summary>
    public string? DomeUniqueId { get; init; }

    /// <summary>SafetyMonitor 0's persisted UniqueID, when a heartbeat reported it.</summary>
    public string? SafetyMonitorUniqueId { get; init; }

    /// <summary>Dome clients at the last heartbeat; null when the heartbeat is missing or stale.</summary>
    public int? DomeConnectedClients { get; init; }

    /// <summary>SafetyMonitor clients at the last heartbeat; null when the heartbeat is missing or stale.</summary>
    public int? SafetyMonitorConnectedClients { get; init; }
}

/// <summary>The sanitized heartbeat written atomically by the Alpaca process, never a source of safety decisions.</summary>
public sealed record AlpacaServerHeartbeat(
    DateTimeOffset GeneratedAtUtc,
    int Port,
    string DomeUniqueId,
    string SafetyMonitorUniqueId,
    int DomeConnectedClients,
    int SafetyMonitorConnectedClients)
{
    /// <summary>The supervised container's heartbeat path, in a directory writable only by the Alpaca account.</summary>
    public const string DefaultPath = "/run/hvo-roof/alpaca-status/status.json";

    /// <summary>Past this age the process and its client counts are unavailable.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(3);
}
