using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Reads the device key of the kiosk or the Mac app (the file its <c>DeviceKeyFile</c> setting names) once, at startup.
/// It is a secret: it never appears in a message, a log or on the screen.
/// </summary>
public static class KioskDeviceKey
{
    /// <summary>The kiosk's setting, as messages name it.</summary>
    public const string KioskSetting = "Kiosk:DeviceKeyFile";

    /// <summary>Reads the key from <paramref name="path"/>. A trailing line break is ignored.</summary>
    /// <param name="path">The key file.</param>
    /// <param name="setting">The setting that names the file, as messages name it.</param>
    /// <param name="reader">Who must be able to read the file, as messages say it.</param>
    /// <exception cref="KioskSettingsException">The file cannot be read, is empty, or holds more than a key.</exception>
    public static string Load(string path, string setting = KioskSetting, string reader = "the kiosk's user")
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
                $"{setting} names {path}, which could not be read ({ex.GetType().Name}). It must be readable by {reader}.");
        }

        if (key.Length == 0 || key.Any(c => c is < ' ' or > '~') || key.Trim().Length != key.Length)
        {
            throw new KioskSettingsException(
                $"{setting} names {path}, which must hold one API key on one line. {RoofCredential.InvalidHeaderValue}");
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
