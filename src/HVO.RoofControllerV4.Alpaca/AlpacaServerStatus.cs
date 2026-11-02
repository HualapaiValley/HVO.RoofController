using System.Text.Json;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca;

/// <summary>
/// Publishes only port, persisted identities and per-device connection counts for the controller's System/About API.
/// The file is optional, atomically replaced every second, and never used to decide whether the roof may move.
/// </summary>
internal sealed class AlpacaServerStatus(
    AlpacaOptions options,
    AlpacaDeviceIds ids,
    AlpacaDome dome,
    AlpacaSafetyMonitor weather,
    TimeProvider time,
    ILogger<AlpacaServerStatus> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.StatusFilePath)) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Publish();
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try { File.Delete(options.StatusFilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The Alpaca status heartbeat could not be removed ({ErrorType}); it expires after three seconds", ex.GetType().Name);
            }
        }
    }

    private void Publish()
    {
        var temporary = options.StatusFilePath + ".tmp";
        try
        {
            var heartbeat = new AlpacaServerHeartbeat(time.GetUtcNow(), options.AdvertisedPort, ids.Dome, ids.SafetyMonitor,
                dome.Connections.Count, weather.Connections.Count);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, heartbeat);
            }
            // Only public metadata; the root controller has no DAC_OVERRIDE and must be able to read this file.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, options.StatusFilePath!, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The Alpaca status heartbeat could not be written ({ErrorType}); System/About reports it unavailable", ex.GetType().Name);
        }
    }
}
