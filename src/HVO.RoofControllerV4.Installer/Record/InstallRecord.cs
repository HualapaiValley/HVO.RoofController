using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Record;

/// <summary>Which HAT the controller on this machine drives.</summary>
public enum HatMode
{
    /// <summary>The real HAT: this is the observatory's controller.</summary>
    Real,

    /// <summary>The HAT emulator: this is a test rig.</summary>
    Emulated
}

/// <summary>
/// What the installer set up, and with which choices and versions: <c>/etc/hvo-roof/install.json</c> for the machine's
/// roles, <c>~/.config/hvo-roof/install.json</c> for the person's. Every later run reads it: to offer the same choices,
/// to refuse a test rig on the real HAT's machine, and to upgrade, roll back or uninstall. It holds no secret.
/// </summary>
public sealed record InstallRecord
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    public required InstallScope Scope { get; init; }

    public required IReadOnlyList<InstallRole> Roles { get; init; }

    /// <summary>The HAT the controller drives: real for the controller, emulated for a rig, null for neither.</summary>
    public HatMode? Hat { get; init; }

    /// <summary>The release installed.</summary>
    public required string Version { get; init; }

    /// <summary>The installer that last wrote this record.</summary>
    public required string InstallerVersion { get; init; }

    public DateTimeOffset InstalledAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public ControllerSettings? Controller { get; init; }

    public CliSettings? Cli { get; init; }

    public MacAppSettings? MacApp { get; init; }

    /// <summary>True when the record says this machine drives the real HAT.</summary>
    [JsonIgnore]
    public bool DrivesRealHat => Hat == HatMode.Real || Roles.Contains(InstallRole.Controller);

    /// <summary>The choices recorded, as answers (the starting point for the wizard's questions).</summary>
    public InstallAnswers ToAnswers() => new InstallAnswers
    {
        Roles = Roles,
        Controller = Controller,
        Cli = Cli,
        MacApp = MacApp
    }.Normalised();

    public string ToJson() => JsonSerializer.Serialize(this, InstallerJson.Options) + "\n";

    /// <summary>True when the two say the same, whenever each was written.</summary>
    public bool SameAs(InstallRecord? other)
        => other is not null
            && (this with { InstalledAt = default, UpdatedAt = default }).ToJson()
                == (other with { InstalledAt = default, UpdatedAt = default }).ToJson();

    public static InstallRecord Parse(string json)
    {
        // The schema first: a newer installer's record may not have what this one's needs.
        if (SchemaIn(json) is > CurrentSchema and var schema)
        {
            throw NewerSchema(schema);
        }

        var record = JsonSerializer.Deserialize<InstallRecord>(json, InstallerJson.Options)
            ?? throw new JsonException("The record is empty.");
        return record.Schema > CurrentSchema ? throw NewerSchema(record.Schema) : record;
    }

    private static JsonException NewerSchema(int schema)
        => new($"It was written by a newer installer (schema {schema}); this one reads schema {CurrentSchema}.");

    /// <summary>
    /// The schema of the record at <paramref name="path"/>, read on its own: null when there is no record, or it has no
    /// schema this installer can read.
    /// </summary>
    public static int? SchemaOf(InstallerMachine machine, string path)
    {
        try
        {
            return machine.ReadText(path) is { } json ? SchemaIn(json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? SchemaIn(string json)
    {
        try
        {
            // Read as the record itself is (InstallerJson.Options): with comments skipped.
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out var schema)
                && schema.TryGetInt32(out var number)
                    ? number
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The record at <paramref name="path"/>: null when there is none, or its problem when it cannot be read.</summary>
    public static (InstallRecord? Record, string? Problem) Load(InstallerMachine machine, string path)
    {
        string? json;
        try
        {
            json = machine.ReadText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, $"{path} could not be read: {error.Message}");
        }

        if (json is null)
        {
            return (null, null);
        }

        try
        {
            return (Parse(json), null);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return (null, $"{path} is not a valid install record: {error.Message}");
        }
    }
}
