using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The controller's settings (<c>appsettings.Local.json</c>) from a backup: copied into the settings folder as they are,
/// only while the controller has none, so an install never changes the settings of one that runs. Wiring settings
/// change only through commissioning.md. The installer checks that the file is a JSON object; the deploy script's
/// deployment check reads it as the controller will before the controller is replaced.
/// </summary>
public sealed class SettingsImportStep(ControllerLayout layout, string source) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => layout.SettingsFile;

    public override string Purpose => $"the controller's settings, from {source}";

    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Check(context));
    }

    private StepCheck Check(InstallContext context)
    {
        string? current;
        try
        {
            current = context.Machine.ReadText(Target);
        }
        catch (UnauthorizedAccessException)
        {
            return new StepCheck(StepChange.Info, "only root can read the controller's settings: run with sudo to check them");
        }

        if (current is not null)
        {
            return Imported(context, current)
                ? StepCheck.Unchanged($"imported from {source}")
                : new StepCheck(StepChange.Info, "not imported: the controller has settings already, and an import only starts one that has none (change them in the web UI, or with hvo-roof settings)");
        }

        return Read(context, out _) is { } problem
            ? new StepCheck(StepChange.Blocked, problem)
            : new StepCheck(StepChange.Create, $"copied from {source}, as it is");
    }

    /// <summary>Whether the settings file holds the backup as it is (and the backup can still be read).</summary>
    public bool Imported(InstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return context.Machine.ReadText(Target) is { } current && Imported(context, current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool Imported(InstallContext context, string current)
        => Read(context, out var backup) is null && string.Equals(backup, current, StringComparison.Ordinal);

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Read(context, out var backup) is { } problem)
        {
            throw new InstallerException($"The controller's settings were not imported: {problem}");
        }

        context.Machine.WriteAtomically(Target, backup, Modes.File);
        context.Log.Write($"Imported the controller's settings from {source} to {Target}.");
        return Task.CompletedTask;
    }

    // The backup's text, or why it cannot be imported. Never its content: a message names the file and a line.
    private string? Read(InstallContext context, out string text)
    {
        text = string.Empty;
        string? read;
        try
        {
            read = context.Machine.ReadText(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{source} could not be read ({error.GetType().Name}), so there are no settings to import";
        }

        if (read is null)
        {
            return $"there is no file {source} to import the controller's settings from";
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(read), Reading);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return $"{source} is not the controller's settings: it must hold a JSON object, like appsettings.json";
            }

            // The settings file is one anyone on the machine reads, and the controller refuses one that sets a secret.
            if (Settings(document.RootElement, string.Empty).FirstOrDefault(IsSecret) is { } secret)
            {
                return $"{source} sets {secret}, which is a secret: the controller refuses a settings file with one, and anyone on this machine "
                    + $"can read that file. Take it out of the backup and put it in a file in {layout.Secrets} instead (the installer asks for "
                    + "the camera's user and password itself), then run the installer again";
            }
        }
        catch (JsonException error)
        {
            // The exception's own message can quote the file's text; only the position is reported.
            return $"{source} is not the controller's settings: it is not valid JSON (line {error.LineNumber + 1})";
        }

        text = read;
        return null;
    }

    // The settings in a JSON object, named as configuration names them (Section:Name, an array's items by index), as the
    // controller reads its settings file: each value, and each empty object or array.
    private static IEnumerable<string> Settings(JsonElement element, string prefix)
    {
        var children = element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Select(property => (Name: property.Name, Value: property.Value)).ToList(),
            JsonValueKind.Array => element.EnumerateArray().Select((item, index) => (Name: index.ToString(System.Globalization.CultureInfo.InvariantCulture), Value: item)).ToList(),
            _ => null
        };

        if (children is null || children.Count == 0)
        {
            if (prefix.Length > 0)
            {
                yield return prefix;
            }

            yield break;
        }

        foreach (var (name, value) in children)
        {
            foreach (var setting in Settings(value, prefix.Length > 0 ? $"{prefix}:{name}" : name))
            {
                yield return setting;
            }
        }
    }

    // As the controller's RoofSettingsCatalogue.IsSecretKey: the camera's user and password, and any key or password.
    private static bool IsSecret(string setting)
    {
        var name = setting[(setting.LastIndexOf(':') + 1)..];
        return name.Equals("Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Password", StringComparison.OrdinalIgnoreCase)
            || setting.Equals("BlueIris:UserName", StringComparison.OrdinalIgnoreCase);
    }
}
