using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Maui.Graphics;

namespace HVO.RoofControllerV4.iPad.Models;

/// <summary>
/// Presentation-friendly representation of a health check entry for the MAUI UI.
/// </summary>
public sealed class HealthCheckDisplay
{
    public HealthCheckDisplay(HealthCheckEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Name = entry.Name;
        Status = entry.Status;
        Description = entry.Description;
        Duration = entry.Duration;
        Exception = entry.Exception;
        Tags = entry.Tags;
        Data = FormatHealthData(entry.Data);
        HasData = !string.IsNullOrWhiteSpace(Data);
        HasDescription = !string.IsNullOrWhiteSpace(entry.Description);
        HasDuration = !string.IsNullOrWhiteSpace(entry.Duration);
        HasException = !string.IsNullOrWhiteSpace(entry.Exception);
        TagsDisplay = entry.Tags is { Count: > 0 } ? string.Join(" • ", entry.Tags) : null;
        HasTags = entry.Tags is { Count: > 0 };

        (StatusBackground, StatusTextColor) = GetStatusColors(entry.Status);
    }

    public string Name { get; }

    public string Status { get; }

    public string? Description { get; }

    public string? Duration { get; }

    public string? Exception { get; }

    public IReadOnlyList<string>? Tags { get; }

    public string? Data { get; }

    public bool HasDescription { get; }

    public bool HasDuration { get; }

    public bool HasException { get; }

    public bool HasData { get; }

    public bool HasTags { get; }

    public string? TagsDisplay { get; }

    public Color StatusBackground { get; }

    public Color StatusTextColor { get; }

    private static string? FormatHealthData(JsonElement? data)
    {
        if (!HasHealthData(data))
        {
            return null;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        return JsonSerializer.Serialize(data!.Value, options);
    }

    private static bool HasHealthData(JsonElement? data)
    {
        if (data is null)
        {
            return false;
        }

        return data.Value.ValueKind switch
        {
            JsonValueKind.Object => data.Value.EnumerateObject().Any(),
            JsonValueKind.Array => data.Value.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(data.Value.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }

    private static (Color Background, Color Text) GetStatusColors(string? status)
    {
        var lower = status?.ToLowerInvariant();
        return lower switch
        {
            "healthy" => (Color.FromArgb("#198754"), Colors.White),
            "degraded" => (Color.FromArgb("#ffc107"), Color.FromArgb("#212529")),
            "unhealthy" => (Color.FromArgb("#dc3545"), Colors.White),
            _ => (Color.FromArgb("#6c757d"), Colors.White)
        };
    }
}
