using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Where the controller keeps the settings changed through the API, and how those files join the configuration.
/// </summary>
internal static class RoofSettingsConfiguration
{
    public const string SectionName = "RoofControllerSettings";

    /// <summary>
    /// The settings file (<c>appsettings.Local.json</c>). Unset: changes are held in memory only and lost at a restart.
    /// </summary>
    public const string FilePathKey = SectionName + ":FilePath";

    /// <summary>The managed secrets file. Unset: secrets set through the API are held in memory only.</summary>
    public const string SecretsFilePathKey = SectionName + ":SecretsFilePath";

    /// <summary>
    /// Adds the settings file and the managed secrets file just above <c>appsettings.{Environment}.json</c>: they
    /// override the shipped defaults, and the user secrets, environment, command line and secrets directory still
    /// override them. The paths are read from the configuration so far (usually the environment); a relative path is
    /// taken from <paramref name="contentRootPath"/>. Throws <see cref="RoofSettingsFileException"/> when a file cannot
    /// be used.
    /// </summary>
    public static void AddRoofSettingsFiles(this IConfigurationBuilder configuration, string environmentName, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = configuration as IConfiguration
            ?? throw new ArgumentException("The configuration must be readable while it is built.", nameof(configuration));
        var settingsPath = Resolve(root[FilePathKey], contentRootPath);
        var secretsPath = Resolve(root[SecretsFilePathKey], contentRootPath);
        if (settingsPath is not null && secretsPath is not null
            && string.Equals(settingsPath, secretsPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new RoofSettingsFileException($"{FilePathKey} and {SecretsFilePathKey} must name different files.");
        }

        var sources = configuration.Sources;
        var index = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource { Path: { } path } && IsShippedFile(path, environmentName))
            {
                index = i;
            }
        }

        // Each insert rebuilds the providers, so the secrets file goes in first and the settings file below it.
        sources.Insert(index + 1, new RoofSettingsFileSource(RoofSettingsFileKind.Secrets, secretsPath));
        sources.Insert(index + 1, new RoofSettingsFileSource(RoofSettingsFileKind.Settings, settingsPath));
    }

    private static bool IsShippedFile(string path, string environmentName)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "appsettings.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, $"appsettings.{environmentName}.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The full path of a configured file, or null when none is configured.</summary>
    internal static string? Resolve(string? path, string contentRootPath)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim(), contentRootPath);
}
