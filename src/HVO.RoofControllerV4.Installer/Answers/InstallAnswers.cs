using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>
/// Everything the wizard asks, as a file: <c>hvo-roof-install --answers FILE</c> installs from it without asking, and
/// the wizard's review page saves one. It never holds a secret: keys are made on the machine, straight into files only
/// their user can read, and passwords and PINs are typed by a person.
/// </summary>
public sealed record InstallAnswers
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    public IReadOnlyList<InstallRole> Roles { get; init; } = [];

    /// <summary>The controller's choices (the controller, or a test rig).</summary>
    public ControllerSettings? Controller { get; init; }

    public CliSettings? Cli { get; init; }

    public MacAppSettings? MacApp { get; init; }

    /// <summary>
    /// For a test rig on a machine with the HAT's I2C bus: this machine's host name, typed to confirm that it is not the
    /// observatory's Pi. The wizard never saves it: each new rig is confirmed by a person, or by an answers file written
    /// for that machine.
    /// </summary>
    public string? RigConfirmation { get; init; }

    /// <summary>
    /// For the controller over plain HTTP: <c>http</c>, typed to confirm that API keys, session tokens and PINs will cross
    /// the network unencrypted. Like the rig's confirmation, the wizard never saves it: each machine that starts serving
    /// HTTP is confirmed by a person, or by an answers file written for it.
    /// </summary>
    public string? HttpConfirmation { get; init; }

    /// <summary>
    /// These answers with a section, with its defaults, for each role that has questions, and none for a role not
    /// chosen, so a saved file holds only what applies. A controller with no choices yet gets
    /// <paramref name="defaultController"/>'s, or the defaults.
    /// </summary>
    public InstallAnswers Normalised(ControllerSettings? defaultController = null)
    {
        var roles = InstallRoles.Ordered(Roles);
        var controller = InstallRoles.RunsController(roles) ? (Controller ?? defaultController ?? new ControllerSettings()).Normalised() : null;
        return this with
        {
            Roles = roles,
            Controller = controller,
            Cli = roles.Contains(InstallRole.Cli) ? Cli ?? new CliSettings() : null,
            MacApp = roles.Contains(InstallRole.MacApp) ? MacApp ?? new MacAppSettings() : null,
            RigConfirmation = roles.Contains(InstallRole.Rig) ? RigConfirmation : null,
            HttpConfirmation = controller?.Connection == ConnectionMode.Http ? HttpConfirmation : null
        };
    }

    /// <summary>What is wrong with the answers themselves (before the machine is looked at).</summary>
    public IEnumerable<string> Problems()
    {
        if (Schema != CurrentSchema)
        {
            yield return $"The answers file's schema is {Schema}; this installer reads schema {CurrentSchema}.";
        }

        foreach (var problem in Controller?.Problems() ?? [])
        {
            yield return problem;
        }

        if (Cli is { } cli && !CliSettings.Folders.Contains(cli.Folder))
        {
            yield return $"hvo-roof's folder must be {string.Join(" or ", CliSettings.Folders)}, not '{cli.Folder}'.";
        }

        if (MacApp is { } macApp && !MacAppSettings.Folders.Contains(macApp.Folder))
        {
            yield return $"The Mac app's folder must be {string.Join(" or ", MacAppSettings.Folders)}, not '{macApp.Folder}'.";
        }
    }

    /// <summary>The answers as a file, without the confirmations a person types.</summary>
    public string ToJson() => JsonSerializer.Serialize(Normalised() with { RigConfirmation = null, HttpConfirmation = null }, InstallerJson.Options) + "\n";

    /// <summary>Reads an answers file; a member it does not know is an error, so a misspelt one is not silently ignored.</summary>
    public static InstallAnswers Parse(string json)
    {
        InstallAnswers? answers;
        try
        {
            answers = JsonSerializer.Deserialize<InstallAnswers>(json, InstallerJson.StrictOptions);
        }
        catch (JsonException error)
        {
            throw new InstallerUsageException($"The answers file is not valid: {error.Message}");
        }

        if (answers is null)
        {
            throw new InstallerUsageException("The answers file is empty.");
        }

        if (answers.Roles.Count == 0)
        {
            throw new InstallerUsageException("The answers file names no roles: give \"roles\", for example [\"cli\"].");
        }

        var problems = answers.Problems().ToArray();
        if (problems.Length > 0)
        {
            throw new InstallerUsageException(string.Join(Environment.NewLine, problems));
        }

        return answers.Normalised();
    }
}

/// <summary>How the installer writes and reads its files: camelCase members, kebab-case names (mac-app, private-ca).</summary>
public static class InstallerJson
{
    public static JsonSerializerOptions Options { get; } = Create(strict: false);

    /// <summary>For answers files: an unknown member is an error.</summary>
    public static JsonSerializerOptions StrictOptions { get; } = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
