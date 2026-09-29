using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>What the web UI knows of the supervisor.</summary>
public enum SupervisorAvailability
{
    /// <summary>The web UI runs without the supervisor (no state file is configured).</summary>
    NotSupervised,

    /// <summary>The state file was read.</summary>
    Available,

    /// <summary>A state file is configured but could not be read, or is not valid.</summary>
    Unreadable,
}

/// <summary>The supervisor's state as the web UI read it.</summary>
public sealed record SupervisorReading(SupervisorAvailability Availability, SupervisorSnapshot? Snapshot, string? Problem)
{
    public static SupervisorReading NotSupervised { get; } = new(SupervisorAvailability.NotSupervised, null, null);
}

/// <summary>Reads the supervisor's state file (<see cref="RoofWebOptions.SupervisorStatePath"/>).</summary>
public sealed class SupervisorStateReader
{
    private readonly IOptionsMonitor<RoofWebOptions> _options;

    public SupervisorStateReader(IOptionsMonitor<RoofWebOptions> options) => _options = options;

    public async Task<SupervisorReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var path = _options.CurrentValue.SupervisorStatePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return SupervisorReading.NotSupervised;
        }

        try
        {
            // The supervisor replaces the file atomically (rename), so a read never sees half of it.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var snapshot = await JsonSerializer.DeserializeAsync<SupervisorSnapshot>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (snapshot is null || string.IsNullOrEmpty(snapshot.Controller?.State) || string.IsNullOrEmpty(snapshot.Ui?.State))
            {
                return new SupervisorReading(SupervisorAvailability.Unreadable, null, $"{path} does not hold the supervisor's state.");
            }

            return new SupervisorReading(SupervisorAvailability.Available, snapshot, null);
        }
        catch (FileNotFoundException)
        {
            return new SupervisorReading(SupervisorAvailability.Unreadable, null, $"{path} does not exist: the supervisor has not written its state.");
        }
        catch (DirectoryNotFoundException)
        {
            return new SupervisorReading(SupervisorAvailability.Unreadable, null, $"{path} does not exist: the supervisor has not written its state.");
        }
        catch (JsonException ex)
        {
            return new SupervisorReading(SupervisorAvailability.Unreadable, null, $"{path} is not valid: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SupervisorReading(SupervisorAvailability.Unreadable, null, $"{path} could not be read: {ex.Message}");
        }
    }
}
