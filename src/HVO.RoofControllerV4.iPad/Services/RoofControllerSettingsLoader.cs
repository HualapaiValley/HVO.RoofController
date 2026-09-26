using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.iPad.Configuration;

namespace HVO.RoofControllerV4.iPad.Services;

public enum SettingsLoadStatus
{
    /// <summary>No settings file exists; built-in defaults apply.</summary>
    Missing,

    /// <summary>The settings file was read and validated.</summary>
    Loaded,

    /// <summary>The settings file was unreadable or invalid. It was moved aside and built-in defaults apply.</summary>
    Recovered
}

/// <summary>
/// Outcome of reading the local settings file at startup.
/// </summary>
public sealed class SettingsLoadResult
{
    public SettingsLoadResult(SettingsLoadStatus status, RoofControllerApiOptions? options = null, string? problem = null, string? backupPath = null, string? embeddedApiKey = null)
    {
        Status = status;
        Options = options;
        Problem = problem;
        BackupPath = backupPath;
        EmbeddedApiKey = embeddedApiKey;
    }

    public SettingsLoadStatus Status { get; }

    /// <summary>Validated options from the file when <see cref="Status"/> is <see cref="SettingsLoadStatus.Loaded"/>.</summary>
    public RoofControllerApiOptions? Options { get; }

    public string? Problem { get; }

    public string? BackupPath { get; }

    /// <summary>
    /// An API key found in the file (hand-edited or from an older build). It is moved to secure storage and removed
    /// from the file; it is never written back.
    /// </summary>
    public string? EmbeddedApiKey { get; }

    public override string ToString() => $"{Status}{(Problem is null ? string.Empty : $": {Problem}")}";
}

/// <summary>
/// Reads, validates and writes the <c>{ "RoofControllerApi": { ... } }</c> settings document.
/// </summary>
public static class RoofControllerSettingsLoader
{
    public const string SectionName = "RoofControllerApi";
    private const string EmbeddedApiKeyProperty = "ApiKey";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads the settings file. A file that cannot be read, parsed or validated is moved aside (never deleted) and
    /// reported as <see cref="SettingsLoadStatus.Recovered"/>, so a bad file can never stop the app from launching.
    /// </summary>
    public static SettingsLoadResult Load(SettingsFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!store.Exists)
        {
            return new SettingsLoadResult(SettingsLoadStatus.Missing);
        }

        string text;
        try
        {
            text = store.ReadAllText();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Recover(store, $"The settings file could not be read ({ex.Message}).");
        }

        if (!TryParse(text, out var options, out var embeddedApiKey, out var error))
        {
            // A key found in an otherwise invalid file is still moved to secure storage.
            return Recover(store, error, embeddedApiKey);
        }

        return new SettingsLoadResult(SettingsLoadStatus.Loaded, options, embeddedApiKey: embeddedApiKey);
    }

    public static bool TryParse(string json, [NotNullWhen(true)] out RoofControllerApiOptions? options, out string? embeddedApiKey, [NotNullWhen(false)] out string? error)
    {
        options = null;
        embeddedApiKey = null;

        try
        {
            if (JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) is not JsonObject root)
            {
                error = "The settings file does not contain a JSON object.";
                return false;
            }

            // Older builds wrote the options either under the section name or at the root.
            var section = root[SectionName] as JsonObject ?? root;

            if (section[EmbeddedApiKeyProperty] is JsonValue keyValue && keyValue.TryGetValue<string>(out var key) && !string.IsNullOrWhiteSpace(key))
            {
                embeddedApiKey = key.Trim();
            }

            options = section.Deserialize<RoofControllerApiOptions>(ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            options = null;
            error = $"The settings file could not be parsed ({ex.Message}).";
            return false;
        }

        if (options is null)
        {
            error = "The settings file is empty.";
            return false;
        }

        NormalizeLegacyValues(options);

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true))
        {
            options = null;
            error = "The settings file has invalid values: " + string.Join(" ", validationResults.Select(r => r.ErrorMessage));
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Serializes options for the settings file. The API key is not part of <see cref="RoofControllerApiOptions"/>
    /// and so can never be written here.
    /// </summary>
    public static string Serialize(RoofControllerApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = new JsonObject
        {
            [SectionName] = JsonSerializer.SerializeToNode(options, WriteOptions)
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Older builds accepted any positive retry count and pulse length. Bring such values into today's range instead
    /// of discarding the whole file (and with it the controller URL).
    /// </summary>
    private static void NormalizeLegacyValues(RoofControllerApiOptions options)
    {
        if (options.RequestRetryCount > RoofControllerApiOptions.MaxRequestAttempts)
        {
            options.RequestRetryCount = RoofControllerApiOptions.MaxRequestAttempts;
        }

        if (options.ClearFaultPulseMs > 0)
        {
            options.ClearFaultPulseMs = Math.Clamp(options.ClearFaultPulseMs, RoofControllerLimits.MinClearFaultPulseMilliseconds, RoofControllerLimits.MaxClearFaultPulseMilliseconds);
        }
    }

    private static SettingsLoadResult Recover(SettingsFileStore store, string problem, string? embeddedApiKey = null)
    {
        try
        {
            var backupPath = store.Quarantine();
            return new SettingsLoadResult(SettingsLoadStatus.Recovered, problem: problem, backupPath: backupPath, embeddedApiKey: embeddedApiKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(SettingsLoadStatus.Recovered, problem: $"{problem} The file could not be moved aside ({ex.Message}) and will be replaced on the next save.", embeddedApiKey: embeddedApiKey);
        }
    }
}
