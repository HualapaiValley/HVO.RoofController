using System.Text.Json;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// The devices' Alpaca <c>UniqueID</c>s, kept in <c>alpaca-devices.json</c> in <see cref="AlpacaOptions.DataPath"/>: made
/// the first time the server starts, and never changed after, so a client keeps recognising the roof across restarts
/// and upgrades. A file that cannot be read stops the server rather than being replaced.
/// </summary>
public sealed class AlpacaDeviceIds
{
    /// <summary>The file's name in the data directory.</summary>
    public const string FileName = "alpaca-devices.json";

    /// <summary>The roof's key in the file.</summary>
    public const string DomeKey = "dome/0";

    private AlpacaDeviceIds(string dome) => Dome = dome;

    /// <summary>The roof's (Dome 0) UniqueID.</summary>
    public string Dome { get; }

    /// <summary>The lock file beside it, held while the identities are read or made.</summary>
    public const string LockFileName = FileName + ".lock";

    /// <summary>How long a server waits for another one starting at the same moment to finish making the identities.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the identities from <paramref name="directory"/>, making them first if there are none. Servers starting at
    /// the same moment take turns (an exclusive lock on <see cref="LockFileName"/>), so every one serves the identity that
    /// is kept. A new file is written whole under another name and then put in place, so it is never seen half written.
    /// </summary>
    /// <exception cref="AlpacaSettingsException">The file cannot be read or written, or holds no valid identity.</exception>
    public static AlpacaDeviceIds Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path))
            {
                return Read(path);
            }

            Directory.CreateDirectory(directory);
            using var turn = TakeTurn(Path.Combine(directory, LockFileName), path);
            if (File.Exists(path))
            {
                return Read(path);
            }

            var created = Guid.NewGuid().ToString("D");
            var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Dictionary<string, string> { [DomeKey] = created });
                    stream.WriteByte((byte)'\n');
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }

            return new AlpacaDeviceIds(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AlpacaSettingsException(
                $"The device identities in {path} could not be read or made ({ex.GetType().Name}). Check {AlpacaOptions.SectionName}:DataPath.");
        }
    }

    // An exclusive lock on the lock file (on Unix, flock), across processes and threads alike. Another server holding it is
    // waited for; one that never lets go makes this start fail rather than guess. Null when the identities appeared while
    // waiting: reading them needs no turn.
    private static FileStream? TakeTurn(string lockPath, string path)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline && !File.Exists(path))
            {
                Thread.Sleep(20);
            }
            catch (IOException) when (File.Exists(path))
            {
                return null;
            }
        }
    }

    private static AlpacaDeviceIds Read(string path)
    {
        var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (ids is null || !ids.TryGetValue(DomeKey, out var dome) || !Guid.TryParse(dome, out _))
        {
            throw new AlpacaSettingsException(
                $"{path} holds no {DomeKey} UniqueID. Restore it from a backup: a new one would make clients see a different roof.");
        }

        return new AlpacaDeviceIds(dome);
    }
}
