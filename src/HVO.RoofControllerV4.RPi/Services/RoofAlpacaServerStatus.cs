using System;
using System.IO;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.IO;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// Reads only the separate Alpaca process's sanitized heartbeat. It never contacts a device, changes configuration or
/// participates in motion admission. A crashed, hung or unreadable server has no current client counts.
/// </summary>
public sealed class RoofAlpacaServerStatus(IConfiguration configuration, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The deployed server's state, with stale counts withheld and only validated identities returned.</summary>
    public AlpacaServerSummary Read()
    {
        if (!string.Equals(configuration["Alpaca:Enabled"], "true", StringComparison.Ordinal))
        {
            return new AlpacaServerSummary();
        }

        var portText = configuration["Alpaca:Discovery:AdvertisedPort"] ?? configuration["Alpaca:Port"] ?? "11111";
        var port = int.TryParse(portText, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : (int?)null;
        var summary = new AlpacaServerSummary { Enabled = true, Port = port };
        var path = configuration["Alpaca:StatusFilePath"] ?? AlpacaServerHeartbeat.DefaultPath;
        try
        {
            // One no-follow, nonblocking descriptor is checked and read within 4096 bytes, even if the path changes.
            var heartbeat = JsonSerializer.Deserialize<AlpacaServerHeartbeat>(UnixRegularFileReader.ReadAllText(path), Json);
            if (heartbeat is null || !Guid.TryParse(heartbeat.DomeUniqueId, out var dome)
                || !Guid.TryParse(heartbeat.SafetyMonitorUniqueId, out var weather)) return summary;
            summary = summary with { DomeUniqueId = dome.ToString("D"), SafetyMonitorUniqueId = weather.ToString("D") };
            var age = time.GetUtcNow() - heartbeat.GeneratedAtUtc;
            if (age < TimeSpan.Zero || age > AlpacaServerHeartbeat.MaximumAge || heartbeat.Port != port
                || heartbeat.DomeConnectedClients is < 0 or > 1024 || heartbeat.SafetyMonitorConnectedClients is < 0 or > 1024)
                return summary;
            return summary with
            {
                Running = true,
                DomeConnectedClients = heartbeat.DomeConnectedClients,
                SafetyMonitorConnectedClients = heartbeat.SafetyMonitorConnectedClients
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return summary;
        }
    }
}
