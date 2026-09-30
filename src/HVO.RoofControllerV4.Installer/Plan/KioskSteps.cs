using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The kiosk on the controller's Pi, set up as docs/kiosk.md's Install section does it by hand: its packages, its user,
/// its program from the release (the one it replaces kept for a rollback), its device key, its settings, its unit and
/// backlight rule, and its service; then the console's cursor hidden, and PINs for the people chosen.
/// </summary>
public static class KioskSteps
{
    public const string User = "hvo-kiosk";

    /// <summary>Who owns the kiosk's configuration folder: root, with the kiosk's group, which may open it.</summary>
    public const string FolderOwner = "root:" + User;

    /// <summary>Who owns the device key: the kiosk's user alone.</summary>
    public const string KeyOwner = User + ":" + User;

    public const string ProgramFolder = "/opt/hvo-roof-kiosk";
    public const string ProgramName = "hvo-roof-kiosk";
    public const string Program = ProgramFolder + "/" + ProgramName;

    /// <summary>The program an update replaced: what a rollback puts back (docs/kiosk.md).</summary>
    public const string PreviousProgram = Program + ".previous";

    public const string SettingsFile = ProgramFolder + "/appsettings.Local.json";
    public const string ConfigurationFolder = "/etc/hvo-roof-kiosk";
    public const string DeviceKeyFile = ConfigurationFolder + "/device-key";
    public const string BacklightRuleFile = "/etc/udev/rules.d/99-hvo-roof-kiosk-backlight.rules";

    /// <summary>The release's kiosk: its asset's kind and platform in release.json.</summary>
    public const string AssetKind = "kiosk";
    public const string AssetPlatform = "linux-arm64";

    /// <summary>The kernel setting that hides the console's cursor, which shows through the kiosk's screen otherwise.</summary>
    public const string HideCursorSetting = "vt.global_cursor_default=0";

    /// <summary>The display and input libraries, and fontconfig (docs/kiosk.md).</summary>
    public static IReadOnlyList<string> Packages { get; } = ["libdrm2", "libgbm1", "libegl1", "libgles2", "libinput10", "libfontconfig1"];

    /// <summary>The display (and the backlight, with the udev rule), the touchscreen, and the GPU.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["video", "input", "render"];

    /// <summary>The kernel's command line: Raspberry Pi OS's since bookworm, then the older place.</summary>
    public static IReadOnlyList<string> CommandLineFiles { get; } = ["/boot/firmware/cmdline.txt", "/boot/cmdline.txt"];

    /// <summary>
    /// The kiosk's steps, after the controller's: <paramref name="kioskKey"/> is the controller's key the kiosk gets a
    /// copy of, and <paramref name="adminKey"/> the installer's admin key, which gives the people chosen their PINs.
    /// <paramref name="certificate"/> and <paramref name="authority"/> are the plan's steps that may issue the
    /// controller a new certificate or make a new CA in the same run: the kiosk pins the one, and is started again to
    /// read the other.
    /// </summary>
    public static IReadOnlyList<PlanStep> For(
        ControllerLayout layout,
        ControllerSettings controller,
        KioskSettings kiosk,
        ApiKeyAllocation kioskKey,
        ApiKeyAllocation adminKey,
        FirstAdminSettings? firstAdmin,
        CertificateStep? certificate = null,
        CertificateAuthorityStep? authority = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(kiosk);
        var program = new KioskProgramStep();
        var key = new KioskDeviceKeyStep(layout, kioskKey);
        var settings = new KioskSettingsStep(layout, controller, certificate);
        var unit = new KioskFileStep(MachineSurveyor.KioskUnitFile, "hvo-roof-kiosk.service", "the kiosk's systemd unit");
        var rule = new KioskFileStep(BacklightRuleFile, "99-hvo-roof-kiosk-backlight.rules", "lets the kiosk turn the screen's backlight off and on");
        var steps = new List<PlanStep>
        {
            new KioskPackagesStep(),
            new KioskUserStep(),
            new FolderStep(ProgramFolder, Modes.Folder, "the kiosk's program and settings"),
            program,
            new FolderStep(ConfigurationFolder, Modes.GroupFolder, "the kiosk's device key", FolderOwner),
            key,
            settings,
            unit,
            rule,
            new KioskServiceStep(layout, controller, [program, key, settings, unit, rule], unit, rule, authority)
        };

        if (kiosk.HideCursor)
        {
            steps.Add(new KioskCursorStep());
        }

        steps.AddRange(kiosk.Normalised().Pins.Select(name => new KioskPinStep(layout, name, adminKey, firstAdmin)));
        return steps;
    }

    /// <summary>
    /// What <c>hvo-roof-install cert</c> changes for a kiosk installed here: its settings (the controller's certificate's
    /// pin, or its CA), and its service, started again to read them and a new CA.
    /// </summary>
    public static IReadOnlyList<PlanStep> ForCertificate(ControllerLayout layout, ControllerSettings controller)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(controller);
        var settings = new KioskSettingsStep(layout, controller);
        return [settings, new KioskServiceStep(layout, controller, [settings], unit: null, rule: null)];
    }

    /// <summary>One of the kiosk's files from its deploy folder, as the installer carries it.</summary>
    public static string Resource(string name)
    {
        using var stream = typeof(KioskSteps).Assembly.GetManifestResourceStream($"kiosk/{name}")
            ?? throw new InstallerException($"The installer was built without the kiosk's {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Runs <paramref name="command"/>, and throws with its last words when it fails.</summary>
    internal static async Task<CommandResult> Run(InstallContext context, string doing, CommandLine command, CancellationToken cancellationToken)
    {
        var result = await context.Machine.Commands.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result : throw new InstallerException($"Could not {doing}: {result.Reason}");
    }
}

/// <summary>
/// The kiosk's libraries (<see cref="KioskSteps.Packages"/>), installed with apt when any is missing. Raspberry Pi OS
/// Lite has most of them already.
/// </summary>
public sealed class KioskPackagesStep : PlanStep
{
    private IReadOnlyList<string> _missing = [];

    public override StepKind Kind => StepKind.Package;

    public override string Target => string.Join(", ", KioskSteps.Packages);

    public override string Purpose => "the kiosk's display, touch and font libraries";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var query = await context.Machine.Commands.RunAsync(
            new CommandLine("dpkg-query", ["-W", "-f", "${Package} ${db:Status-Status}\n", .. KioskSteps.Packages]),
            cancellationToken).ConfigureAwait(false);
        if (query.ExitCode == CommandResult.NotFound)
        {
            return new StepCheck(StepChange.Blocked, "the kiosk needs Raspberry Pi OS, which installs packages with apt: dpkg-query is not here");
        }

        // It exits 1 when a package is not known, and still lists the ones that are.
        var installed = query.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2 && parts[1] == "installed")
            .Select(parts => parts[0])
            .ToHashSet(StringComparer.Ordinal);
        _missing = [.. KioskSteps.Packages.Where(package => !installed.Contains(package))];
        return _missing.Count == 0
            ? StepCheck.Unchanged("installed")
            : new StepCheck(StepChange.Create, $"installs {string.Join(", ", _missing)} with apt");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive" };
        await KioskSteps.Run(
            context,
            "update apt's package lists",
            new CommandLine("apt-get", "update") { Environment = environment, Timeout = TimeSpan.FromMinutes(10), OnOutputLine = context.Progress },
            cancellationToken).ConfigureAwait(false);
        await KioskSteps.Run(
            context,
            $"install {string.Join(", ", _missing)}",
            new CommandLine("apt-get", ["install", "-y", "--no-install-recommends", .. _missing]) { Environment = environment, Timeout = TimeSpan.FromMinutes(30), OnOutputLine = context.Progress },
            cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Installed {string.Join(", ", _missing)}.");
    }
}

/// <summary>
/// The kiosk's user, <c>hvo-kiosk</c>: a system user with no home or login, in its own group (which owns the
/// configuration folder it reads its key from) and in <see cref="KioskSteps.Groups"/>. One already there is kept, and
/// added to any of those groups it is not in; its group is made when only the user is there.
/// </summary>
public sealed class KioskUserStep : PlanStep
{
    private bool _groupExists;
    private IReadOnlyList<string> _missingGroups = [];

    public override StepKind Kind => StepKind.User;

    public override string Target => KioskSteps.User;

    public override string Purpose => "runs the kiosk: the display, the touchscreen and the GPU, and nothing else";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var groups = string.Join(", ", KioskSteps.Groups);
        var group = await context.Machine.Commands.RunAsync(new CommandLine("getent", "group", KioskSteps.User), cancellationToken).ConfigureAwait(false);
        _groupExists = group.Succeeded;
        var user = await context.Machine.Commands.RunAsync(new CommandLine("getent", "passwd", KioskSteps.User), cancellationToken).ConfigureAwait(false);
        if (!user.Succeeded)
        {
            _missingGroups = KioskSteps.Groups;
            return new StepCheck(
                StepChange.Create,
                $"a system user with no home or login, in {groups}{(_groupExists ? $", and the {KioskSteps.User} group already here" : string.Empty)}");
        }

        var member = await context.Machine.Commands.RunAsync(new CommandLine("id", "-nG", KioskSteps.User), cancellationToken).ConfigureAwait(false);
        var memberOf = member.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        _missingGroups = [.. KioskSteps.Groups.Prepend(KioskSteps.User).Where(name => !memberOf.Contains(name))];
        var missing = string.Join(", ", _missingGroups);
        return _missingGroups.Count == 0 ? StepCheck.Unchanged($"in {KioskSteps.User}, {groups}")
            : _groupExists ? new StepCheck(StepChange.Change, $"added to {missing}")
            : new StepCheck(StepChange.Change, $"the {KioskSteps.User} group made, and the user added to {missing}");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (check.Change == StepChange.Create)
        {
            // A group of that name left behind is the user's group; useradd --user-group refuses to make it again.
            string[] group = _groupExists ? ["--gid", KioskSteps.User] : ["--user-group"];
            string[] arguments = ["--system", .. group, "--no-create-home", "--shell", "/usr/sbin/nologin", KioskSteps.User];
            await KioskSteps.Run(
                context,
                $"add the {KioskSteps.User} user",
                new CommandLine("useradd", arguments),
                cancellationToken).ConfigureAwait(false);
            context.Log.Write($"Added the {KioskSteps.User} user.");
        }
        else if (!_groupExists)
        {
            await KioskSteps.Run(context, $"add the {KioskSteps.User} group", new CommandLine("groupadd", "--system", KioskSteps.User), cancellationToken).ConfigureAwait(false);
            context.Log.Write($"Added the {KioskSteps.User} group.");
        }

        await KioskSteps.Run(
            context,
            $"add {KioskSteps.User} to {string.Join(", ", _missingGroups)}",
            new CommandLine("usermod", "-aG", string.Join(',', _missingGroups), KioskSteps.User),
            cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Added {KioskSteps.User} to {string.Join(", ", _missingGroups)}.");
    }
}

/// <summary>
/// The kiosk's program, from the release's kiosk (<c>hvo-roof-kiosk-VERSION-linux-arm64.tar.gz</c>): the tarball is
/// checked against release.json's size and SHA-256, and the program against its own SHA-256 there, before it goes in
/// place in one step. The one it replaces is kept as <see cref="KioskSteps.PreviousProgram"/>.
/// </summary>
public sealed class KioskProgramStep : PlanStep
{
    // Longer than any kiosk the release could carry: a tarball's program is never read past it.
    private const long MaximumProgramBytes = 1L << 30;

    public override StepKind Kind => StepKind.File;

    public override string Target => KioskSteps.Program;

    public override string Purpose => "the kiosk's program";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (Wanted(release) is not { } wanted)
        {
            return new StepCheck(StepChange.Blocked, $"release {release.Version} has no kiosk for {KioskSteps.AssetPlatform} with its program's SHA-256 in {ReleaseManifest.FileName}");
        }

        var current = await context.Machine.Sha256Async(Target, cancellationToken).ConfigureAwait(false);
        return current is null ? new StepCheck(StepChange.Create, $"release {release.Version}'s, {Modes.Octal(Modes.Program)}")
            : current != wanted.Sha256 ? new StepCheck(StepChange.Change, $"release {release.Version}'s; the one there now is kept as {Path.GetFileName(KioskSteps.PreviousProgram)}")
            : context.Machine.GetMode(Target) is { } mode && mode != Modes.Program ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.Program)}")
            : StepCheck.Unchanged($"release {release.Version}'s");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var (asset, sha256) = Wanted(release) ?? throw new InstallerException($"Release {release.Version} has no kiosk for {KioskSteps.AssetPlatform}.");
        if (await machine.Sha256Async(Target, cancellationToken).ConfigureAwait(false) == sha256)
        {
            machine.SetMode(Target, Modes.Program);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.Program)}.");
            return;
        }

        using var work = new DeployScript.WorkFolder(machine);
        var tarball = await ReleaseFiles.GetAsync(context, release, asset, work, cancellationToken).ConfigureAwait(false);
        var staged = $"{Target}.new";
        try
        {
            await machine.WriteAtomicallyAsync(staged, (stream, token) => ExtractAsync(machine, tarball, asset, sha256, stream, token), Modes.Program, cancellationToken).ConfigureAwait(false);
            if (machine.FileExists(Target))
            {
                await machine.CopyFileAsync(Target, KioskSteps.PreviousProgram, Modes.Program, cancellationToken).ConfigureAwait(false);
                context.Log.Write($"Kept the kiosk's program it replaces as {KioskSteps.PreviousProgram}.");
            }

            machine.MoveFile(staged, Target);
        }
        finally
        {
            if (machine.FileExists(staged))
            {
                machine.DeleteFile(staged);
            }
        }

        context.Log.Write($"Installed the kiosk's program from {asset.Name} (release {release.Version}) as {Target}.");
    }

    private static (ReleaseAsset Asset, string Sha256)? Wanted(ReleaseManifest release)
    {
        var asset = release.Assets.FirstOrDefault(candidate => candidate.Kind == KioskSteps.AssetKind && candidate.Platform == KioskSteps.AssetPlatform);
        return asset is not null && asset.Files.TryGetValue(KioskSteps.ProgramName, out var sha256) ? (asset, sha256) : null;
    }

    // The program from the tarball, checked against its SHA-256 as it is written: a program that differs is never used.
    private static async Task ExtractAsync(InstallerMachine machine, string tarball, ReleaseAsset asset, string sha256, Stream output, CancellationToken cancellationToken)
    {
        var entryName = $"{Folder(asset)}/{KioskSteps.ProgramName}";
        var file = machine.OpenRead(tarball) ?? throw new InstallerException($"{tarball} is not there.");
        await using (file.ConfigureAwait(false))
        {
            var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                var reader = new TarReader(gzip);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
                    {
                        if (entry.Name != entryName)
                        {
                            continue;
                        }

                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null || entry.Length > MaximumProgramBytes)
                        {
                            break;
                        }

                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }

                        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256)
                        {
                            throw new InstallerException($"The kiosk's program in {asset.Name} is not the one {ReleaseManifest.FileName} names: its SHA-256 differs.");
                        }

                        return;
                    }
                }
            }
        }

        throw new InstallerException($"{asset.Name} has no program at {entryName}.");
    }

    // The tarball's one folder: its name without .tar.gz.
    private static string Folder(ReleaseAsset asset)
        => asset.Name.EndsWith(".tar.gz", StringComparison.Ordinal) ? asset.Name[..^".tar.gz".Length] : asset.Name;
}

/// <summary>
/// The kiosk's device key: a copy of the controller's kiosk key (a viewer's, marked Kiosk and Local) that only
/// <c>hvo-kiosk</c> reads. It is written in one step as root's alone, then given to the kiosk's user, and never shown
/// or logged.
/// </summary>
public sealed class KioskDeviceKeyStep(ControllerLayout layout, ApiKeyAllocation kioskKey) : PlanStep
{
    private static readonly string Wanted = $"{Modes.Octal(Modes.OwnerReadOnly)}, {KioskSteps.User}'s alone";

    public override StepKind Kind => StepKind.File;

    public override string Target => KioskSteps.DeviceKeyFile;

    public override string Purpose => $"the kiosk's API key ({kioskKey.Name}): signs the kiosk in to the controller";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        if (!kioskKey.IsKnown || !machine.IsRoot)
        {
            return new StepCheck(StepChange.Info, "only root can read the controller's keys: run with sudo to check it");
        }

        var key = ControllerProbe.ReadKey(machine, ApiKeyFiles.KeyFile(layout.Secrets, kioskKey.Index));
        var current = machine.ReadText(Target);
        if (current is null)
        {
            return new StepCheck(StepChange.Create, Wanted);
        }

        if (key is null)
        {
            // The installer makes the controller's kiosk key in this run.
            return new StepCheck(StepChange.Change, "the controller's new kiosk key");
        }

        context.Log.AddSecret(key);
        if (current != key)
        {
            return new StepCheck(StepChange.Change, "the controller's kiosk key");
        }

        var changes = new List<string>();
        if (machine.GetMode(Target) is { } mode && mode != Modes.OwnerReadOnly)
        {
            changes.Add($"{Modes.Octal(mode)} → {Modes.Octal(Modes.OwnerReadOnly)}");
        }

        if (await Ownership.GetAsync(machine, Target, cancellationToken).ConfigureAwait(false) is var owner && owner != KioskSteps.KeyOwner)
        {
            changes.Add($"{owner ?? "an unknown owner"} → {KioskSteps.KeyOwner}");
        }

        return changes.Count > 0 ? new StepCheck(StepChange.Change, string.Join(", ", changes)) : StepCheck.Unchanged(Wanted);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var keyFile = ApiKeyFiles.KeyFile(layout.Secrets, kioskKey.Index);
        var key = ControllerProbe.ReadKey(machine, keyFile)
            ?? throw new InstallerException($"The controller's kiosk key ({keyFile}) is gone: run the installer again.");
        context.Log.AddSecret(key);

        // Root's alone until it is the kiosk's: the key is never readable by anyone else.
        machine.WriteAtomically(Target, key, Modes.OwnerReadOnly);
        await Ownership.SetAsync(context, Target, KioskSteps.KeyOwner, cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Wrote {Target} ({Wanted}): a copy of {kioskKey.Name}.");
    }
}

/// <summary>
/// The kiosk's settings (appsettings.Local.json): the example's, or the ones there, with the controller's address and
/// how to trust it (its CA, or its certificate's pin; neither under plain HTTP) and the device key's file. Settings a
/// person changed by hand are kept.
/// </summary>
public sealed class KioskSettingsStep(ControllerLayout layout, ControllerSettings controller, CertificateStep? issuing = null) : PlanStep
{
    private const string Section = "Kiosk";

    public override StepKind Kind => StepKind.File;

    public override string Target => KioskSteps.SettingsFile;

    public override string Purpose => "the kiosk's settings: the controller's address, how it trusts it, and its key";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (current, wanted, check) = Settings(context);
        if (check is not null)
        {
            return Task.FromResult(check);
        }

        var mode = context.Machine.GetMode(Target);
        return Task.FromResult(
            current is null ? new StepCheck(StepChange.Create, Describe())
            : !JsonNode.DeepEquals(current, wanted) ? new StepCheck(StepChange.Change, Describe())
            : mode is { } found && found != Modes.File ? new StepCheck(StepChange.Change, $"{Modes.Octal(found)} → {Modes.Octal(Modes.File)}")
            : StepCheck.Unchanged(Describe()));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (_, wanted, problem) = Settings(context);
        if (problem is not null || wanted is null)
        {
            throw new InstallerException($"The kiosk's settings could not be written: {problem?.Detail ?? "the controller's certificate is not there"}.");
        }

        context.Machine.WriteAtomically(Target, wanted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Modes.File);
        context.Log.Write($"Wrote {Target}: {Describe()}.");
        return Task.CompletedTask;
    }

    private string Describe() => controller.Connection switch
    {
        ConnectionMode.Http => $"{Address()} over plain HTTP",
        ConnectionMode.PrivateCa => $"{Address()}, trusting the CA {layout.CaCertificate}",
        _ => $"{Address()}, pinning the controller's certificate"
    };

    private string Address() => string.Create(CultureInfo.InvariantCulture, $"{(controller.UsesHttps ? "https" : "http")}://localhost:{controller.ApiPort}/");

    // The settings there (null when none), the settings wanted, or why they cannot be told (a check to return).
    private (JsonObject? Current, JsonObject? Wanted, StepCheck? Check) Settings(InstallContext context)
    {
        var text = context.Machine.ReadText(Target);
        JsonObject? current = null;
        if (text is not null)
        {
            try
            {
                current = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (current is null || (current[Section] is { } section && section is not JsonObject))
            {
                return (null, null, new StepCheck(StepChange.Blocked, $"it is not settings the kiosk reads: correct it, or move it aside, then run the installer again"));
            }
        }

        string? pin = null;
        if (controller.Connection is ConnectionMode.SelfSigned or ConnectionMode.OwnCertificate)
        {
            var (certificate, _) = MachineSurveyor.SurveyCertificates(context.Machine, controller);
            if (certificate is { NeedsRoot: true })
            {
                return (current, null, new StepCheck(StepChange.Info, "only root can read the controller's certificate: run with sudo to check the kiosk's pin"));
            }

            // The installer makes it, puts it in place or issues a new one in this run: the kiosk pins that one.
            if (certificate?.Fingerprint is not { } fingerprint || (issuing is not null && !context.HasApplied(issuing) && issuing.WillWrite(context)))
            {
                return (current, null, new StepCheck(current is null ? StepChange.Create : StepChange.Change, $"{Address()}, pinning the controller's certificate once it is in place"));
            }

            pin = fingerprint;
        }

        var wanted = (JsonObject)(current ?? JsonNode.Parse(KioskSteps.Resource("appsettings.Local.example.json"))!).DeepClone();
        if (wanted[Section] is not JsonObject kiosk)
        {
            kiosk = [];
            wanted[Section] = kiosk;
        }

        var address = Address();
        if (!(kiosk["ControllerUrl"] is JsonValue url && url.TryGetValue<string>(out var existing)
            && Uri.TryCreate(existing, UriKind.Absolute, out var uri) && uri == new Uri(address)))
        {
            kiosk["ControllerUrl"] = address;
        }

        Set(kiosk, "ServerCaCertificateFile", controller.Connection == ConnectionMode.PrivateCa ? layout.CaCertificate : null);
        Set(kiosk, "ServerCertificateSha256", pin);
        Set(kiosk, "DeviceKeyFile", KioskSteps.DeviceKeyFile);
        return (current, wanted, null);
    }

    // A setting to have (a value), or not to have: one there already is set to null, which the kiosk reads as not set.
    private static void Set(JsonObject section, string name, string? value)
    {
        if (value is not null)
        {
            if (!(section[name] is JsonValue there && there.TryGetValue<string>(out var current) && current == value))
            {
                section[name] = value;
            }
        }
        else if (section[name] is not null)
        {
            section[name] = null;
        }
    }
}

/// <summary>One of the kiosk's files the installer carries (its unit, its backlight rule), as the release has it.</summary>
public sealed class KioskFileStep(string path, string resource, string purpose) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => path;

    public override string Purpose => purpose;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var current = context.Machine.ReadText(path);
        var mode = context.Machine.GetMode(path);
        return Task.FromResult(
            current is null ? new StepCheck(StepChange.Create, Modes.Octal(Modes.File))
            : current != KioskSteps.Resource(resource) ? new StepCheck(StepChange.Change, "this release's")
            : mode is { } found && found != Modes.File ? new StepCheck(StepChange.Change, $"{Modes.Octal(found)} → {Modes.Octal(Modes.File)}")
            : StepCheck.Unchanged(Modes.Octal(Modes.File)));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = Path.GetDirectoryName(path)!;
        if (!context.Machine.DirectoryExists(folder))
        {
            context.Machine.CreateDirectory(folder, Modes.Folder);
        }

        context.Machine.WriteAtomically(path, KioskSteps.Resource(resource), Modes.File);
        context.Log.Write($"Wrote {path}.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The kiosk's service: enabled, so it starts when the Pi does, and running. It is started again when its program,
/// settings, device key, unit, or the CA it trusts changed since it started, in this run or one that stopped before it
/// got here. A new unit is loaded first (<c>daemon-reload</c>), and a new backlight rule applied once.
/// </summary>
public sealed class KioskServiceStep(
    ControllerLayout layout,
    ControllerSettings controller,
    IReadOnlyList<PlanStep> inputs,
    KioskFileStep? unit,
    KioskFileStep? rule,
    CertificateAuthorityStep? authority = null) : PlanStep
{
    private bool _enabled;
    private bool _needsReload;

    public override StepKind Kind => StepKind.Service;

    public override string Target => MachineSurveyor.KioskUnit;

    public override string Purpose => "starts the kiosk on the touchscreen, and at each boot";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var enabled = await Systemctl(machine, cancellationToken, "is-enabled", Target).ConfigureAwait(false);
        var active = await Systemctl(machine, cancellationToken, "is-active", Target).ConfigureAwait(false);
        _enabled = enabled == "enabled";
        _needsReload = await Systemctl(machine, cancellationToken, "show", "-p", "NeedDaemonReload", "--value", Target).ConfigureAwait(false) == "yes";
        if (!machine.FileExists(MachineSurveyor.KioskUnitFile) && (unit is null || !(context.HasApplied(unit) || (await unit.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange)))
        {
            return new StepCheck(StepChange.Blocked, $"{MachineSurveyor.KioskUnitFile} is not there: run the installer to install the kiosk");
        }

        // Enabled and activating: systemd is starting it again after it stopped (RestartSec), as it does by itself.
        var restarting = _enabled && active == "activating";
        if (active != "active" && !restarting)
        {
            var state = string.IsNullOrEmpty(active) ? "not there" : active;
            return new StepCheck(
                _enabled || (enabled.Length > 0 && enabled != "not-found") ? StepChange.Change : StepChange.Create,
                _enabled ? $"started: it is {state}" : "enabled and started");
        }

        if (!_enabled)
        {
            return new StepCheck(StepChange.Change, "enabled, so it starts at boot, and started again");
        }

        if (await ChangedAsync(context, cancellationToken).ConfigureAwait(false) is { } changed)
        {
            return new StepCheck(StepChange.Change, $"started again to read {changed}");
        }

        return StepCheck.Unchanged(restarting
            ? $"enabled, and systemd is starting it again: if it keeps stopping, see journalctl -u {Target}"
            : "enabled and running");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_needsReload || (unit is not null && context.HasApplied(unit)))
        {
            await KioskSteps.Run(context, "reload systemd's units", new CommandLine("systemctl", "daemon-reload"), cancellationToken).ConfigureAwait(false);
        }

        // The rule acts on the add event a boot sends: one now applies it without a reboot.
        if ((rule is not null && context.HasApplied(rule)) || check.Change == StepChange.Create)
        {
            await KioskSteps.Run(
                context,
                "apply the backlight rule",
                new CommandLine("udevadm", "trigger", "--action=add", "--subsystem-match=backlight"),
                cancellationToken).ConfigureAwait(false);
        }

        if (!_enabled)
        {
            await KioskSteps.Run(context, $"enable {Target}", new CommandLine("systemctl", "enable", Target), cancellationToken).ConfigureAwait(false);
        }

        await KioskSteps.Run(context, $"start {Target}", new CommandLine("systemctl", "restart", Target), cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Started {Target}: {check.Detail}.");
    }

    // What the running kiosk has not read: a file a step changes or changed in this run, or one written since it started.
    private async Task<string?> ChangedAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (_needsReload)
        {
            return "its new unit";
        }

        // A CA the installer makes in this run: the kiosk reads its CA when it starts.
        if (authority is not null && (context.HasApplied(authority) ? authority.Wrote : authority.WillWrite(context)))
        {
            return $"its new {Path.GetFileName(authority.Target)}";
        }

        foreach (var input in inputs)
        {
            if (context.HasApplied(input) || (await input.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange)
            {
                return $"its new {Path.GetFileName(input.Target)}";
            }
        }

        var started = await StartedAsync(context.Machine, cancellationToken).ConfigureAwait(false);
        IEnumerable<string> files = [.. inputs.Select(input => input.Target)];
        if (controller.Connection == ConnectionMode.PrivateCa)
        {
            files = files.Append(layout.CaCertificate);
        }

        return files.FirstOrDefault(file => CameraSteps.WrittenSince(context, file, started)) is { } written
            ? $"its new {Path.GetFileName(written)}"
            : null;
    }

    // When the service last started, as systemd says; null when it does not say.
    private async Task<DateTimeOffset?> StartedAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var value = await Systemctl(machine, cancellationToken, "show", "-p", "ActiveEnterTimestamp", "--timestamp=us+utc", "--value", Target).ConfigureAwait(false);
        return DateTimeOffset.TryParseExact(
            value,
            "ddd yyyy-MM-dd HH:mm:ss.ffffff 'UTC'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var started) ? started : null;
    }

    private static async Task<string> Systemctl(InstallerMachine machine, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await machine.Commands.RunAsync(new CommandLine("systemctl", arguments), cancellationToken).ConfigureAwait(false);
        return result.Output.Trim();
    }
}

/// <summary>
/// The console's cursor hidden behind the kiosk (<see cref="KioskSteps.HideCursorSetting"/> on the kernel's command
/// line), which takes effect at the next reboot. The rest of the line is kept as it is.
/// </summary>
public sealed class KioskCursorStep : PlanStep
{
    private const string Name = "vt.global_cursor_default=";

    public override StepKind Kind => StepKind.File;

    public override string Target => KioskSteps.CommandLineFiles[0];

    public override string Purpose => "hides the console's cursor behind the kiosk, from the next reboot";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (File(context.Machine) is not { } file)
        {
            return Task.FromResult(new StepCheck(
                StepChange.Info,
                $"no {Path.GetFileName(Target)} in /boot/firmware or /boot: add {KioskSteps.HideCursorSetting} to the kernel's command line to hide the console's cursor"));
        }

        var words = Words(context.Machine.ReadText(file)!);
        return Task.FromResult(words.Contains(KioskSteps.HideCursorSetting) && !words.Any(word => word.StartsWith(Name, StringComparison.Ordinal) && word != KioskSteps.HideCursorSetting)
            ? StepCheck.Unchanged($"{KioskSteps.HideCursorSetting} in {file}")
            : new StepCheck(StepChange.Change, $"adds {KioskSteps.HideCursorSetting} to {file}; it takes effect at the next reboot"));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var file = File(context.Machine) ?? throw new InstallerException($"There is no {Path.GetFileName(Target)} in /boot/firmware or /boot.");
        var words = Words(context.Machine.ReadText(file)!).Where(word => !word.StartsWith(Name, StringComparison.Ordinal)).Append(KioskSteps.HideCursorSetting);

        // One line, as the Pi's firmware reads it. /boot/firmware is FAT, which keeps no mode: none is set.
        context.Machine.ReplaceText(file, string.Join(' ', words) + "\n");
        context.Log.Write($"Added {KioskSteps.HideCursorSetting} to {file}: the console's cursor is hidden from the next reboot.");
        return Task.CompletedTask;
    }

    /// <summary>Whether the running kernel hides the cursor already: when not, hiding it needs a reboot.</summary>
    public static bool Hidden(InstallerMachine machine)
        => machine?.ReadText("/proc/cmdline") is { } running && Words(running).Contains(KioskSteps.HideCursorSetting);

    private static string? File(InstallerMachine machine) => KioskSteps.CommandLineFiles.FirstOrDefault(machine.FileExists);

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A PIN for signing in at the kiosk, for a person already on the controller who has none: typed, or given in a file
/// (<c>--pin-file NAME=FILE</c>), set through the controller's API with the installer's admin key, and never saved,
/// logged or shown. Someone who has a PIN keeps it: it is changed in the web UI.
/// </summary>
public sealed class KioskPinStep(ControllerLayout layout, string name, ApiKeyAllocation adminKey, FirstAdminSettings? firstAdmin) : PlanStep
{
    public override StepKind Kind => StepKind.User;

    public override string Target => name;

    public override string Purpose => "a PIN, for signing in at the kiosk";

    private bool IsFirstAdmin => firstAdmin is not null && string.Equals(firstAdmin.Name, name, StringComparison.OrdinalIgnoreCase);

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IdentityEntry? person;
        try
        {
            person = Find(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, "only root can read the controller's people: run with sudo to check them"));
        }
        catch (InstallerException error)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, $"{error.Message} Restore it from a backup, then run the installer again."));
        }

        return Task.FromResult(person switch
        {
            null when IsFirstAdmin && firstAdmin!.Pin => new StepCheck(StepChange.Info, "the first admin's PIN, given with them"),
            null when IsFirstAdmin => new StepCheck(StepChange.Create, "once the first admin is added"),
            null => new StepCheck(StepChange.Blocked, $"{name} is not on the controller: add them on the web UI's People page, or leave them out of the kiosk's PINs, then run the installer again"),
            _ when person.Role.Equals(RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase)
                => new StepCheck(StepChange.Blocked, $"{person.Name} is a viewer, and only operators and admins sign in at the kiosk: leave them out of the kiosk's PINs, or change their role on the web UI's People page"),
            { HasPin: true } => StepCheck.Unchanged("has one: change it on the web UI's People page"),
            _ => new StepCheck(StepChange.Create, $"set through the controller's API ({person.Role})")
        });
    }

    public override IReadOnlyList<InstallSecret> SecretsNeeded(StepCheck check)
        => check.Change == StepChange.Create ? [InstallSecret.PinFor(name)] : [];

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var secret = InstallSecret.PinFor(name);
        var pin = context.Secrets[secret] ?? throw new InstallerException($"The install needs {InstallSecrets.Describe(secret)}, which was not given.");
        var person = Find(context) ?? throw new InstallerException($"{name} is not on the controller, so they were not given a PIN.");
        var keyFile = ApiKeyFiles.KeyFile(layout.Secrets, adminKey.Index);
        var key = ControllerProbe.ReadKey(context.Machine, keyFile)
            ?? throw new InstallerException($"The installer's admin key ({keyFile}) is gone: run the installer again.");
        context.Log.AddSecret(pin);
        var request = new RoofUserUpdateRequest { Role = person.Role, Pin = pin };
        var response = await ControllerApi.SendAsync(
            context,
            MachineSurveyor.ControllerContainer,
            key,
            HttpMethod.Put,
            $"{FirstAdminStep.UsersPath}/{Uri.EscapeDataString(person.Name)}",
            JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
            cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 200:
                context.Log.Write($"Gave {person.Name} a PIN for the kiosk, with {adminKey.Name}.");
                return;
            case 404:
                throw new InstallerException($"{person.Name} is no longer on the controller, so they were not given a PIN.");
            case null:
                throw new InstallerException($"The controller did not answer ({response.Reason}), so {person.Name} was not given a PIN. Run the installer again once it runs.");
            case 401 or 403:
                throw new InstallerException($"The controller refused the installer's admin key ({keyFile}, HTTP {response.Status}), so {person.Name} was not given a PIN. Run the installer again: it redeploys a controller that does not know its keys.");
            default:
                throw new InstallerException($"The controller did not give {person.Name} a PIN: HTTP {response.Status}{(response.Problem is { } problem ? $", {context.Log.Redact(problem)}" : string.Empty)}");
        }
    }

    private IdentityEntry? Find(InstallContext context)
        => ControllerIdentity.Read(context.Machine, layout.IdentityFile).Users
            .FirstOrDefault(user => string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase));
}
