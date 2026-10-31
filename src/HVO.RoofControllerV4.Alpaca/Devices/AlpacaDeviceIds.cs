using System.Text.Json;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// The devices' Alpaca <c>UniqueID</c>s, kept in <c>alpaca-devices.json</c> in <see cref="AlpacaOptions.DataPath"/>: made
/// the first time the server starts, and never changed after, so a client keeps recognising the roof and the weather
/// across restarts and upgrades. A file from before the SafetyMonitor (phase 1, the Dome's alone) gains its identity
/// the first time it is read; the Dome's stays as it is. A file that cannot be read stops the server rather than being
/// replaced.
/// </summary>
public sealed class AlpacaDeviceIds
{
    /// <summary>The file's name in the data directory.</summary>
    public const string FileName = "alpaca-devices.json";

    /// <summary>The roof's key in the file.</summary>
    public const string DomeKey = "dome/0";

    /// <summary>The weather's key in the file.</summary>
    public const string SafetyMonitorKey = "safetymonitor/0";

    private AlpacaDeviceIds(string dome, string safetyMonitor)
    {
        Dome = dome;
        SafetyMonitor = safetyMonitor;
    }

    /// <summary>The roof's (Dome 0) UniqueID.</summary>
    public string Dome { get; }

    /// <summary>The weather's (SafetyMonitor 0) UniqueID.</summary>
    public string SafetyMonitor { get; }

    /// <summary>The lock file beside it, held while the identities are read or made.</summary>
    public const string LockFileName = FileName + ".lock";

    /// <summary>How long a server waits for another one starting at the same moment to finish making the identities.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the identities from <paramref name="directory"/>, making those there are not first. Servers starting at the
    /// same moment take turns (an exclusive lock on <see cref="LockFileName"/>), so every one serves the identities that
    /// are kept. A file is written whole under another name and then put in place, so it is never seen half written; an
    /// identity already kept is never changed.
    /// </summary>
    /// <exception cref="AlpacaSettingsException">The file cannot be read or written, or holds no valid identity.</exception>
    public static AlpacaDeviceIds Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path) && Read(path) is { SafetyMonitor: { } monitor } kept)
            {
                return new AlpacaDeviceIds(kept.Dome!, monitor);
            }

            Directory.CreateDirectory(directory);
            using var turn = TakeTurn(Path.Combine(directory, LockFileName), path, Complete);
            var existing = File.Exists(path) ? Read(path) : default;
            if (existing is { Dome: { } dome, SafetyMonitor: { } safetyMonitor })
            {
                return new AlpacaDeviceIds(dome, safetyMonitor);
            }

            // A new file, or one from phase 1 with the Dome's alone: its Dome identity is kept as it is.
            var ids = new AlpacaDeviceIds(existing.Dome ?? Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
            var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Dictionary<string, string> { [DomeKey] = ids.Dome, [SafetyMonitorKey] = ids.SafetyMonitor });
                    stream.WriteByte((byte)'\n');
                    stream.Flush(flushToDisk: true);
                }

                // A new file never replaces one another server wrote meanwhile; a phase 1 file is replaced under the turn.
                File.Move(temporary, path, overwrite: existing.Dome is not null);
            }
            finally
            {
                File.Delete(temporary);
            }

            return ids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AlpacaSettingsException(
                $"The device identities in {path} could not be read or made ({ex.GetType().Name}). Check {AlpacaOptions.SectionName}:DataPath.");
        }
    }

    // An exclusive lock on the lock file (on Unix, flock), across processes and threads alike. Another server holding it is
    // waited for; one that never lets go makes this start fail rather than guess. Null when the identities were completed
    // while waiting: reading them needs no turn.
    private static FileStream? TakeTurn(string lockPath, string path, Func<string, bool> complete)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline && !complete(path))
            {
                Thread.Sleep(20);
            }
            catch (IOException) when (complete(path))
            {
                return null;
            }
        }
    }

    private static bool Complete(string path) => File.Exists(path) && Read(path).SafetyMonitor is not null;

    // The identities kept: the Dome's always (a file without it is refused), the SafetyMonitor's when the file has it.
    private static (string? Dome, string? SafetyMonitor) Read(string path)
    {
        var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (ids is null || !ids.TryGetValue(DomeKey, out var dome) || !Guid.TryParse(dome, out _))
        {
            throw new AlpacaSettingsException(
                $"{path} holds no {DomeKey} UniqueID. Restore it from a backup: a new one would make clients see a different roof.");
        }

        if (ids.TryGetValue(SafetyMonitorKey, out var monitor) && !Guid.TryParse(monitor, out _))
        {
            throw new AlpacaSettingsException(
                $"{path} holds a {SafetyMonitorKey} UniqueID that is not one. Restore it from a backup: a new one would make clients see a different weather monitor.");
        }

        return (dome, monitor);
    }
}
