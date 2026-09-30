using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Kiosk;

/// <summary>
/// Reads the kiosk's device key (<see cref="KioskOptions.DeviceKeyFile"/>) once, at startup. It is a secret: it never
/// appears in a message, a log or on the screen.
/// </summary>
public static class KioskDeviceKey
{
    /// <summary>Reads the key from <paramref name="path"/>. A trailing line break is ignored.</summary>
    /// <exception cref="KioskSettingsException">The file cannot be read, is empty, or holds more than a key.</exception>
    public static string Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string key;
        try
        {
            key = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KioskSettingsException(
                $"{KioskOptions.SectionName}:DeviceKeyFile names {path}, which could not be read ({ex.GetType().Name}). It must be readable by the kiosk's user.");
        }

        if (key.Length == 0 || key.Any(c => c is < ' ' or > '~') || key.Trim().Length != key.Length)
        {
            throw new KioskSettingsException(
                $"{KioskOptions.SectionName}:DeviceKeyFile names {path}, which must hold one API key on one line. {RoofCredential.InvalidHeaderValue}");
        }

        return key;
    }

    /// <summary>
    /// Whether anyone but the file's owner may read, write or run <paramref name="path"/>: the key file should be 0400 or
    /// 0600. Always false where Unix permissions do not apply.
    /// </summary>
    public static bool IsOpenToOthers(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & others) != 0;
    }
}
