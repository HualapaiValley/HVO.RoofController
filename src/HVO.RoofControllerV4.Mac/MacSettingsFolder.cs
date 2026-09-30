namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// Where the Mac app keeps its settings file and device key: ~/Library/Application Support/HVO Roof on a Mac,
/// $XDG_CONFIG_HOME/hvo-roof-mac (or ~/.config/hvo-roof-mac) elsewhere, or the folder <see cref="Variable"/> names.
/// </summary>
public static class MacSettingsFolder
{
    /// <summary>The environment variable that names another folder, as for a second controller or a test.</summary>
    public const string Variable = "HVO_ROOF_MAC_SETTINGS";

    /// <summary>The folder for this user on this computer.</summary>
    public static string Find() => Find(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsMacOS());

    /// <summary>The folder, from the environment, the home folder and whether this is a Mac.</summary>
    public static string Find(Func<string, string?> environment, string home, bool isMac)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var chosen = environment(Variable);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return Path.GetFullPath(chosen);
        }

        if (isMac)
        {
            return Path.Combine(home, "Library", "Application Support", "HVO Roof");
        }

        // XDG: a relative XDG_CONFIG_HOME is invalid and is ignored.
        var config = environment("XDG_CONFIG_HOME");
        return Path.Combine(config is { Length: > 0 } && Path.IsPathRooted(config) ? config : Path.Combine(home, ".config"), "hvo-roof-mac");
    }
}
