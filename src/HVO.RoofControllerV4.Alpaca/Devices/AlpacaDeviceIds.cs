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

    /// <summary>Reads the identities from <paramref name="directory"/>, making them first if there are none.</summary>
    /// <exception cref="AlpacaSettingsException">The file cannot be read or written, or holds no valid identity.</exception>
    public static AlpacaDeviceIds Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (ids is null || !ids.TryGetValue(DomeKey, out var dome) || !Guid.TryParse(dome, out _))
                {
                    throw new AlpacaSettingsException(
                        $"{path} holds no {DomeKey} UniqueID. Restore it from a backup: a new one would make clients see a different roof.");
                }

                return new AlpacaDeviceIds(dome);
            }

            Directory.CreateDirectory(directory);
            var created = new AlpacaDeviceIds(Guid.NewGuid().ToString("D"));
            var temporary = path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, string> { [DomeKey] = created.Dome }) + "\n");
            // Never replaces a file another process wrote meanwhile.
            File.Move(temporary, path, overwrite: false);
            return created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AlpacaSettingsException(
                $"The device identities in {path} could not be read or made ({ex.GetType().Name}). Check {AlpacaOptions.SectionName}:DataPath.");
        }
    }
}
