using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Configuration source for the settings file (<c>appsettings.Local.json</c>) or the managed secrets file. Without a
/// path the provider starts empty and changes are held in memory only (development and tests).
/// </summary>
internal sealed class RoofSettingsFileSource : IConfigurationSource
{
    public RoofSettingsFileSource(RoofSettingsFileKind kind, string? path)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    public RoofSettingsFileKind Kind { get; }

    /// <summary>The file, or null when changes are held in memory only.</summary>
    public string? Path { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new RoofSettingsFileProvider(Kind, Path);
}

/// <summary>
/// Serves the settings file's keys. It reads the file once and never reloads on its own: an edit made outside the API
/// takes effect only through <c>POST Settings/Reload</c> (validated and confirmed like a change through the API) or a
/// restart. <see cref="Replace"/> is how the settings store publishes a saved change.
/// </summary>
internal sealed class RoofSettingsFileProvider : ConfigurationProvider
{
    private bool _loaded;

    public RoofSettingsFileProvider(RoofSettingsFileKind kind, string? path)
    {
        Kind = kind;
        Path = path;
    }

    public RoofSettingsFileKind Kind { get; }

    /// <summary>The file, or null when changes are held in memory only.</summary>
    public string? Path { get; }

    /// <summary>The document the provider serves.</summary>
    public RoofSettingsDocument Document { get; private set; } = RoofSettingsDocument.Empty;

    /// <summary>
    /// Reads the file the first time; later calls (a reload of the whole configuration) keep what is served, so a hand
    /// edit never bypasses validation. Throws <see cref="RoofSettingsFileException"/> when the file cannot be used.
    /// </summary>
    public override void Load()
    {
        if (_loaded)
        {
            return;
        }

        if (Path is not null)
        {
            SetDocument(RoofSettingsFile.Read(Path, Kind));
        }

        _loaded = true;
    }

    /// <summary>A provider outside any configuration that serves <paramref name="document"/>, to read a candidate.</summary>
    public static RoofSettingsFileProvider Detached(RoofSettingsFileKind kind, RoofSettingsDocument document)
    {
        var provider = new RoofSettingsFileProvider(kind, path: null);
        provider.SetDocument(document);
        provider._loaded = true;
        return provider;
    }

    /// <summary>
    /// Serves <paramref name="document"/>. With <paramref name="notify"/>, tells the configuration (and every options
    /// monitor) it changed; a caller replacing two providers notifies on the second only, so no reader sees half.
    /// </summary>
    public void Replace(RoofSettingsDocument document, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        SetDocument(document);
        _loaded = true;
        if (notify)
        {
            OnReload();
        }
    }

    private void SetDocument(RoofSettingsDocument document)
    {
        Document = document;
        Data = new Dictionary<string, string?>(document.Data, StringComparer.OrdinalIgnoreCase);
    }
}
