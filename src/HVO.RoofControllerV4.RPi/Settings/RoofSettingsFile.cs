using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.RPi.Storage;
using Microsoft.Extensions.Configuration.Json;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>Which of the controller's own configuration files a provider reads.</summary>
internal enum RoofSettingsFileKind
{
    /// <summary><c>appsettings.Local.json</c>: the settings changed from the shipped defaults. No secrets.</summary>
    Settings = 0,

    /// <summary>The managed secrets file: secrets set through the API (0600, in a 0700 directory).</summary>
    Secrets = 1
}

/// <summary>What the settings file said when it was read: its metadata, its hash and its configuration keys.</summary>
internal sealed class RoofSettingsDocument
{
    /// <summary>Hash of a file that does not exist.</summary>
    public const string AbsentHash = "absent";

    public RoofSettingsDocument(
        bool exists,
        string hash,
        long version,
        DateTimeOffset? savedAtUtc,
        string? savedBy,
        JsonObject body,
        IReadOnlyDictionary<string, string?> data)
    {
        Exists = exists;
        Hash = hash;
        Version = version;
        SavedAtUtc = savedAtUtc;
        SavedBy = savedBy;
        Body = body;
        Data = data;
    }

    /// <summary>No file: version 1, no keys.</summary>
    public static RoofSettingsDocument Empty { get; } = new(
        exists: false,
        AbsentHash,
        version: 1,
        savedAtUtc: null,
        savedBy: null,
        new JsonObject(RoofSettingsFile.NodeOptions),
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    public bool Exists { get; }

    /// <summary>SHA-256 of the file's bytes (lower-case hex), or <see cref="AbsentHash"/>.</summary>
    public string Hash { get; }

    /// <summary>The settings version; 1 for a file without metadata.</summary>
    public long Version { get; }

    public DateTimeOffset? SavedAtUtc { get; }

    public string? SavedBy { get; }

    /// <summary>The file's JSON without the metadata. Callers must not change it; clone it first.</summary>
    public JsonObject Body { get; }

    /// <summary>Configuration keys and values, flattened as the JSON configuration provider does.</summary>
    public IReadOnlyDictionary<string, string?> Data { get; }
}

/// <summary>
/// Reads and writes the controller's own configuration files. A file is JSON like an appsettings file, with an optional
/// <c>HvoRoofSettings</c> object ({Version, SavedAtUtc, SavedBy}) that is not configuration. Writes are atomic
/// (temporary file, flush, rename, directory flush), so a reader sees the old file or the new one, never part of one.
/// Error messages name the file and a line, key or setting, never a value.
/// </summary>
internal static class RoofSettingsFile
{
    /// <summary>The metadata object's name.</summary>
    public const string MetadataProperty = "HvoRoofSettings";

    internal const string TemporarySuffix = ".tmp";

    /// <summary>Configuration keys are case-insensitive; so are the file's property names.</summary>
    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private const UnixFileMode SettingsMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode SecretsMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode SecretsDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Human name of a file kind, for messages.</summary>
    public static string Describe(RoofSettingsFileKind kind)
        => kind == RoofSettingsFileKind.Settings ? "settings file" : "managed secrets file";

    /// <summary>
    /// Reads <paramref name="path"/>. A missing file is <see cref="RoofSettingsDocument.Empty"/>. Throws
    /// <see cref="RoofSettingsFileException"/> when the file cannot be read or is not valid.
    /// </summary>
    public static RoofSettingsDocument Read(string path, RoofSettingsFileKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ReadBytes(path, kind) is { } bytes ? Parse(bytes, path, kind) : RoofSettingsDocument.Empty;
    }

    /// <summary>The hash of the file as it is now, or <see cref="RoofSettingsDocument.AbsentHash"/>.</summary>
    public static string CurrentHash(string path, RoofSettingsFileKind kind)
        => ReadBytes(path, kind) is { } bytes ? Hash(bytes) : RoofSettingsDocument.AbsentHash;

    /// <summary>
    /// The file's bytes, or null when there is no file. Only a missing file or directory counts as absent: a path that
    /// names a directory, or that cannot be searched or read, throws <see cref="RoofSettingsFileException"/>, so the
    /// controller never starts on the defaults because it could not see the file.
    /// </summary>
    public static byte[]? ReadBytes(string path, RoofSettingsFileKind kind)
    {
        if (Directory.Exists(path))
        {
            throw new RoofSettingsFileException(DirectoryMessage(path, kind));
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoofSettingsFileException($"The {Describe(kind)} '{path}' could not be read: {ex.GetType().Name}.", ex);
        }
    }

    private static string DirectoryMessage(string path, RoofSettingsFileKind kind)
        => $"The {Describe(kind)} path '{path}' is a directory. It must name the file inside it, for example " +
            $"'{Path.Combine(path, kind == RoofSettingsFileKind.Settings ? "appsettings.Local.json" : "secrets.json")}'.";

    /// <summary>Parses a file's bytes. <paramref name="path"/> is used in messages only.</summary>
    public static RoofSettingsDocument Parse(byte[] bytes, string path, RoofSettingsFileKind kind)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var name = $"The {Describe(kind)} '{path}'";
        long version = 1;
        DateTimeOffset? savedAtUtc = null;
        string? savedBy = null;
        byte[] body;

        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RoofSettingsFileException($"{name} must hold a JSON object, like appsettings.json.");
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var sawMetadata = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, MetadataProperty, StringComparison.OrdinalIgnoreCase))
                    {
                        if (sawMetadata)
                        {
                            throw new RoofSettingsFileException($"{name} has more than one {MetadataProperty} object.");
                        }

                        sawMetadata = true;
                        (version, savedAtUtc, savedBy) = ReadMetadata(property.Value, name);
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            body = buffer.ToArray();
        }
        catch (JsonException ex)
        {
            // The exception's own message can quote the file's text; only the position is reported.
            throw new RoofSettingsFileException(
                $"{name} is not valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine}). " +
                "Fix it, or move it aside to start again from the shipped defaults.",
                ex);
        }

        IDictionary<string, string?> data;
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            data = new JsonFlattener().Flatten(stream);
        }
        catch (FormatException ex)
        {
            // "A duplicate key '<key>' was found.": names a key, never a value.
            throw new RoofSettingsFileException($"{name} is not valid: {ex.Message}", ex);
        }

        foreach (var key in data.Keys)
        {
            if (kind == RoofSettingsFileKind.Settings && RoofSettingsCatalogue.IsSecretKey(key))
            {
                // API keys and certificate passwords are not settings the API manages, so only the secrets directory takes them.
                var where = RoofSettingsCatalogue.Find(key) is { Secret: true }
                    ? "set it through the API (it is kept in the managed secrets file) or as a file in the secrets directory."
                    : "set it as a file in the secrets directory.";
                throw new RoofSettingsFileException($"{name} sets {key}, which is a secret. Secrets stay out of the settings file: {where}");
            }

            if (kind == RoofSettingsFileKind.Secrets && RoofSettingsCatalogue.Find(key) is not { Secret: true })
            {
                throw new RoofSettingsFileException(
                    $"{name} sets {key}, which is not a secret setting. The managed secrets file holds only the secret " +
                    "settings the API manages; put other settings in the settings file.");
            }
        }

        var root = JsonNode.Parse(body, NodeOptions) as JsonObject ?? new JsonObject(NodeOptions);
        return new RoofSettingsDocument(
            exists: true,
            Hash(bytes),
            version,
            savedAtUtc,
            savedBy,
            root,
            new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The bytes of a file holding <paramref name="body"/> (not changed) behind the metadata.
    /// </summary>
    public static byte[] Serialize(JsonObject body, long version, DateTimeOffset savedAtUtc, string? savedBy)
    {
        ArgumentNullException.ThrowIfNull(body);
        var root = new JsonObject(NodeOptions)
        {
            [MetadataProperty] = new JsonObject(NodeOptions)
            {
                ["Version"] = version,
                ["SavedAtUtc"] = savedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["SavedBy"] = savedBy
            }
        };

        foreach (var (name, node) in (JsonObject)body.DeepClone())
        {
            root[name] = node?.DeepClone();
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions) + "\n");
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> atomically: 0644 for the settings file, 0600 for the
    /// secrets file (whose directory is created 0700 when missing). Throws <see cref="RoofSettingsFileException"/>.
    /// Returns false when the rename could not be flushed to disk (always, on systems other than Linux).
    /// </summary>
    public static bool Write(string path, RoofSettingsFileKind kind, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var temporary = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                if (kind == RoofSettingsFileKind.Secrets && !OperatingSystem.IsWindows())
                {
                    System.IO.Directory.CreateDirectory(directory, SecretsDirectoryMode);
                }
                else
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = kind == RoofSettingsFileKind.Secrets ? SecretsMode : SettingsMode;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return RoofDirectorySync.TryFlush(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new RoofSettingsFileException($"The {Describe(kind)} '{path}' could not be written: {ex.GetType().Name}.", ex);
        }
    }

    /// <summary>
    /// For the deployment check: writes and removes a probe file next to <paramref name="path"/>. Returns the problem,
    /// or null when the directory is writable.
    /// </summary>
    public static string? CheckWritable(string path, RoofSettingsFileKind kind)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        if (!System.IO.Directory.Exists(directory))
        {
            var mode = kind == RoofSettingsFileKind.Settings ? "0755" : "0700";
            return $"The {Describe(kind)} directory '{directory}' does not exist, so settings changed through the API could " +
                $"not be saved. Create it on the host with 'sudo install -d -m {mode} <directory>' (docs/deployment.md, " +
                "preparation step 4) and mount it read-write.";
        }

        if (Directory.Exists(path))
        {
            return DirectoryMessage(path, kind);
        }

        var probe = $"{path}.probe-{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The {Describe(kind)} directory '{directory}' is not writable by the controller ({ex.GetType().Name}), " +
                "so settings changed through the API could not be saved. Mount it read-write and give the user the " +
                "controller runs as (root in the controller's image) write access to it.";
        }
        finally
        {
            TryDelete(probe);
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (long Version, DateTimeOffset? SavedAtUtc, string? SavedBy) ReadMetadata(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new RoofSettingsFileException($"{name}: {MetadataProperty} must be an object.");
        }

        long version = 1;
        DateTimeOffset? savedAtUtc = null;
        string? savedBy = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, "Version", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out version) || version < 1)
                {
                    throw new RoofSettingsFileException($"{name}: {MetadataProperty}:Version must be a whole number of at least 1.");
                }
            }
            else if (string.Equals(property.Name, "SavedAtUtc", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var saved))
                {
                    throw new RoofSettingsFileException($"{name}: {MetadataProperty}:SavedAtUtc must be a date and time.");
                }

                savedAtUtc = saved.ToUniversalTime();
            }
            else if (string.Equals(property.Name, "SavedBy", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new RoofSettingsFileException($"{name}: {MetadataProperty}:SavedBy must be a string.");
                }

                savedBy = property.Value.GetString();
            }
        }

        return (version, savedAtUtc, savedBy);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is harmless.
        }
    }

    /// <summary>The JSON configuration provider's flattening (same keys, same duplicate rule), applied to a stream.</summary>
    private sealed class JsonFlattener : JsonStreamConfigurationProvider
    {
        public JsonFlattener()
            : base(new JsonStreamConfigurationSource())
        {
        }

        public IDictionary<string, string?> Flatten(Stream stream)
        {
            Load(stream);
            return Data;
        }
    }
}
