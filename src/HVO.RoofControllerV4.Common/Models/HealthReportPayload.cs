using System.Collections.Generic;
using System.Text.Json;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Represents the payload returned by the roof controller health endpoint.
/// </summary>
public sealed class HealthReportPayload
{
    /// <summary>
    /// Gets or sets the overall health status of the system.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets the collection of individual health check entries.
    /// </summary>
    public List<HealthCheckEntry> Checks { get; set; } = new();

    /// <summary>
    /// Gets or sets the total duration of the health check execution.
    /// </summary>
    public string? TotalDuration { get; set; }
}

/// <summary>
/// Represents a single health check result within the aggregated report.
/// </summary>
public sealed class HealthCheckEntry
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement? Data { get; set; }
    public string? Duration { get; set; }
    public string? Exception { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
}
