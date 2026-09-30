using System.Globalization;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>What the installer keeps an API key for.</summary>
public enum ApiKeyUse
{
    /// <summary>The deploy script's authenticated Status and verified Stop, and the installer's own idle check.</summary>
    Operator,

    /// <summary>Adding the first admin through the API.</summary>
    Admin,

    /// <summary>The web UI's own key for Stop (RoofWeb__StopKeyFile).</summary>
    WebStop,

    /// <summary>The kiosk's key, for PIN sign-in at the touchscreen.</summary>
    Kiosk
}

/// <summary>
/// An API key the controller reads from its secrets folder, as its files (<c>RoofControllerSecurity__ApiKeys__N__Name</c>,
/// <c>__Role</c>, <c>__Key</c>…) say: its name, role and flags, and whether its key is there. Never the key itself.
/// </summary>
public sealed record ConfiguredApiKey(int Index, string? Name, string? Role, bool Kiosk, bool Local, bool HasKey)
{
    /// <summary>True when the installer can use it: it has a name, a role and its key (not only the key's hash).</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Name) && Role is not null && HasKey;

    public bool Is(string role) => string.Equals(Role, role, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A key the installer uses: one already in the secrets folder, which it reuses, or a new one at the next free entry.
/// <see cref="Index"/> is -1 when the secrets folder could not be read (a plan made without root).
/// </summary>
public sealed record ApiKeyAllocation(ApiKeyUse Use, int Index, string Name, string Role, bool Kiosk, bool Local, bool Reused)
{
    public bool IsKnown => Index >= 0;
}

/// <summary>The controller's API keys as files in its secrets folder, one file per setting, as the deploy script mounts them.</summary>
public static partial class ApiKeyFiles
{
    public const string Prefix = "RoofControllerSecurity__ApiKeys__";

    /// <summary>Where the secrets folder is in the controller's container.</summary>
    public const string ContainerSecrets = "/run/secrets";

    public const string OperatorName = "installer-operator";
    public const string AdminName = "installer-admin";
    public const string WebStopName = "web-ui-stop";
    public const string KioskName = "kiosk";

    public static string FileName(int index, string field)
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{index}__{field}");

    /// <summary>The key's file on this machine; with an unknown index, the pattern (…__N__Key).</summary>
    public static string KeyFile(string secrets, int index)
        => Path.Join(secrets, index >= 0 ? FileName(index, "Key") : $"{Prefix}N__Key");

    /// <summary>The key's file as the controller's container sees it.</summary>
    public static string ContainerKeyFile(int index) => $"{ContainerSecrets}/{FileName(index, "Key")}";

    /// <summary>The entry a key file in the container names (/run/secrets/RoofControllerSecurity__ApiKeys__2__Key), or null.</summary>
    public static int? IndexOfContainerKeyFile(string? path)
        => path is not null && ContainerKeyFilePattern().Match(path) is { Success: true } match
            && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index
                : null;

    /// <summary>
    /// The API keys in <paramref name="secrets"/>, by entry: their names, roles and flags (none of them secret), and
    /// whether each has its key. Throws <see cref="UnauthorizedAccessException"/> when only root may read the folder.
    /// </summary>
    public static IReadOnlyList<ConfiguredApiKey> Read(InstallerMachine machine, string secrets)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var fields = new Dictionary<int, Dictionary<string, string>>();
        foreach (var file in machine.ListFiles(secrets))
        {
            if (FilePattern().Match(Path.GetFileName(file)) is not { Success: true } match
                || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var field = match.Groups[2].Value;
            if (!fields.TryGetValue(index, out var entry))
            {
                fields[index] = entry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            // The key is read only to know that it is there; it is never kept.
            var value = machine.ReadText(file) ?? string.Empty;
            entry[field] = field.Equals("Key", StringComparison.OrdinalIgnoreCase) ? (value.Length > 0 ? "yes" : string.Empty) : value.Trim();
        }

        return [.. fields.OrderBy(entry => entry.Key).Select(entry => new ConfiguredApiKey(
            entry.Key,
            entry.Value.GetValueOrDefault("Name") is { Length: > 0 } name ? name : null,
            entry.Value.GetValueOrDefault("Role") is { Length: > 0 } role ? role : null,
            IsTrue(entry.Value.GetValueOrDefault("Kiosk")),
            IsTrue(entry.Value.GetValueOrDefault("Local")),
            entry.Value.GetValueOrDefault("Key") is { Length: > 0 }))];
    }

    /// <summary>
    /// The keys the installer uses for <paramref name="uses"/>: each one a key already there that serves (so a controller
    /// the deploy script started keeps its keys), or a new one, named for what it is for, at the lowest entries free.
    /// <paramref name="webStopIndex"/> is the entry the running controller's web UI already reads its Stop key from, and
    /// <paramref name="managedNames"/> the names of keys added through the API, which a new key's name must not hide.
    /// </summary>
    public static IReadOnlyList<ApiKeyAllocation> Allocate(IReadOnlyList<ConfiguredApiKey> existing, IEnumerable<ApiKeyUse> uses, int? webStopIndex, IEnumerable<string>? managedNames = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(uses);
        var taken = existing.Select(key => key.Index).ToHashSet();
        var names = existing.Where(key => key.Name is not null).Select(key => key.Name!.Trim())
            .Concat(managedNames ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usable = existing.Where(key => key.IsUsable).ToArray();
        var allocations = new List<ApiKeyAllocation>();
        foreach (var use in uses.Distinct())
        {
            var reused = use switch
            {
                ApiKeyUse.Operator => usable.FirstOrDefault(key => Named(key, OperatorName) && !key.Kiosk && (key.Is(RoofControllerApiContract.OperatorRole) || key.Is(RoofControllerApiContract.AdminRole)))
                    ?? usable.FirstOrDefault(key => !key.Kiosk && key.Is(RoofControllerApiContract.OperatorRole))
                    ?? usable.FirstOrDefault(key => !key.Kiosk && key.Is(RoofControllerApiContract.AdminRole)),
                ApiKeyUse.Admin => usable.FirstOrDefault(key => Named(key, AdminName) && !key.Kiosk && key.Is(RoofControllerApiContract.AdminRole))
                    ?? usable.FirstOrDefault(key => !key.Kiosk && key.Is(RoofControllerApiContract.AdminRole)),
                ApiKeyUse.WebStop => usable.FirstOrDefault(key => key.Index == webStopIndex && !key.Kiosk)
                    ?? usable.FirstOrDefault(key => Named(key, WebStopName) && !key.Kiosk && key.Is(RoofControllerApiContract.ViewerRole)),
                ApiKeyUse.Kiosk => usable.FirstOrDefault(key => Named(key, KioskName) && key.Kiosk && key.Is(RoofControllerApiContract.ViewerRole))
                    ?? usable.FirstOrDefault(key => key.Kiosk && key.Is(RoofControllerApiContract.ViewerRole)),
                _ => throw new ArgumentOutOfRangeException(nameof(uses), use, null)
            };

            if (reused is not null)
            {
                allocations.Add(new ApiKeyAllocation(use, reused.Index, reused.Name!.Trim(), reused.Role!.Trim(), reused.Kiosk, reused.Local, Reused: true));
                continue;
            }

            var index = Enumerable.Range(0, int.MaxValue).First(candidate => !taken.Contains(candidate));
            taken.Add(index);
            var (name, role, kiosk, local) = use switch
            {
                ApiKeyUse.Operator => (OperatorName, RoofControllerApiContract.OperatorRole, false, false),
                ApiKeyUse.Admin => (AdminName, RoofControllerApiContract.AdminRole, false, false),
                ApiKeyUse.WebStop => (WebStopName, RoofControllerApiContract.ViewerRole, false, false),
                _ => (KioskName, RoofControllerApiContract.ViewerRole, true, true)
            };
            name = Unique(name, names);
            names.Add(name);
            allocations.Add(new ApiKeyAllocation(use, index, name, role, kiosk, local, Reused: false));
        }

        return allocations;
    }

    /// <summary>A new key: 32 random bytes, base64url, as long as the controller wants and no longer than it takes.</summary>
    public static string NewKey() => ControllerCertificates.NewPassword();

    private static bool Named(ConfiguredApiKey key, string name) => string.Equals(key.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    // The controller ignores a key added through the API that is named as a configured key is, so a new key never takes a
    // name already there.
    private static string Unique(string name, HashSet<string> names)
    {
        var candidate = name;
        for (var n = 2; names.Contains(candidate); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name}-{n}");
        }

        return candidate;
    }

    private static bool IsTrue(string? value) => bool.TryParse(value, out var flag) && flag;

    [GeneratedRegex(@"^RoofControllerSecurity__ApiKeys__([0-9]{1,6})__(Name|Role|Key|KeySha256|Kiosk|Local)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"^/run/secrets/RoofControllerSecurity__ApiKeys__([0-9]{1,6})__Key$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContainerKeyFilePattern();
}

/// <summary>
/// An API key in the controller's secrets folder: one already there, reused as it is (its file's mode tightened to 0600
/// when it is looser), or a new random one, written straight to files only root reads, the key last. A key is never shown
/// or logged. The controller reads a new key when it next starts: the controller's step redeploys it after a new one.
/// </summary>
public sealed class ApiKeyStep(ControllerLayout layout, ApiKeyAllocation key) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => ApiKeyFiles.KeyFile(layout.Secrets, key.Index);

    public override string Purpose => key.Use switch
    {
        ApiKeyUse.Operator => "the installer's operator key: the deploy script's Status and verified Stop (never shown)",
        ApiKeyUse.Admin => "the installer's admin key: adds the first admin (never shown)",
        ApiKeyUse.WebStop => "the web UI's own key for Stop (never shown)",
        _ => "the kiosk's key, for PIN sign-in at the touchscreen (never shown)"
    };

    public ApiKeyAllocation Key => key;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.FromResult(Check(context.Machine));

    private StepCheck Check(InstallerMachine machine)
    {
        if (!key.IsKnown)
        {
            return new StepCheck(StepChange.Info, "only root can read the secrets folder: run with sudo to check it");
        }

        IReadOnlyList<ConfiguredApiKey> existing;
        try
        {
            existing = ApiKeyFiles.Read(machine, layout.Secrets);
        }
        catch (UnauthorizedAccessException)
        {
            return new StepCheck(StepChange.Info, "only root can read the secrets folder: run with sudo to check it");
        }

        var entry = existing.FirstOrDefault(candidate => candidate.Index == key.Index);
        if (entry is null || (!key.Reused && Matches(entry) && !entry.HasKey))
        {
            return key.Reused
                ? new StepCheck(StepChange.Blocked, $"{Target} is gone since the plan was made: run the installer again")
                : new StepCheck(StepChange.Create, $"{Describe()}: a new random key, never shown");
        }

        if (!Matches(entry) || !entry.HasKey)
        {
            return new StepCheck(StepChange.Blocked, $"entry {key.Index} changed since the plan was made: run the installer again");
        }

        var mode = machine.GetMode(Target);
        return mode != Modes.PrivateFile
            ? new StepCheck(StepChange.Change, $"{Describe()}: {Modes.Octal(mode!.Value)} → {Modes.Octal(Modes.PrivateFile)}")
            : StepCheck.Unchanged(key.Reused ? $"reuses {Describe()}" : Describe());
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (check.Change == StepChange.Change)
        {
            machine.SetMode(Target, Modes.PrivateFile);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.PrivateFile)}.");
            return Task.CompletedTask;
        }

        // What the key is first, its key last: an entry with a key always says whose it is. No newline: the controller
        // reads each file whole.
        Write(machine, "Name", key.Name);
        Write(machine, "Role", key.Role);
        if (key.Kiosk)
        {
            Write(machine, "Kiosk", "true");
        }

        if (key.Local)
        {
            Write(machine, "Local", "true");
        }

        var newKey = ApiKeyFiles.NewKey();
        context.Log.AddSecret(newKey);
        Write(machine, "Key", newKey);
        context.Log.Write($"Wrote a new API key, {Describe()}, to {layout.Secrets} as entry {key.Index.ToString(CultureInfo.InvariantCulture)} (the key is never shown).");
        return Task.CompletedTask;
    }

    private void Write(InstallerMachine machine, string field, string value)
        => machine.WriteAtomically(Path.Join(layout.Secrets, ApiKeyFiles.FileName(key.Index, field)), value, Modes.PrivateFile);

    private string Describe()
        => $"{key.Name} ({key.Role}{(key.Kiosk ? ", kiosk" : string.Empty)}{(key.Local ? ", local" : string.Empty)})";

    private bool Matches(ConfiguredApiKey entry)
        => string.Equals(entry.Name?.Trim(), key.Name, StringComparison.OrdinalIgnoreCase)
            && entry.Is(key.Role)
            && entry.Kiosk == key.Kiosk;
}
