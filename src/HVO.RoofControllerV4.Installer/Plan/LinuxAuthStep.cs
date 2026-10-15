using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>Opt-in host worker, fixed helper and explicit native enrollment. Never creates Linux accounts.</summary>
public sealed class LinuxAuthStep(LinuxAuthSettings settings) : PlanStep
{
    public const string ConfigurationFolder = "/etc/hvo-roof/linux-auth";
    public const string ProgramFolder = "/opt/hvo-roof-linux-auth";
    public const string Program = ProgramFolder + "/HVO.RoofControllerV4.LinuxAuth";
    public const string Helper = "/usr/local/libexec/hvo-roof-local-auth";
    public const string Service = "hvo-roof-linux-auth.service";
    public const string Unit = "/etc/systemd/system/" + Service;
    public const string DockerOrder = "/etc/systemd/system/docker.service.d/hvo-roof-linux-auth.conf";
    public const string Pam = "/etc/pam.d/hvo-roof-linux-auth";
    public const string Sudoers = "/etc/sudoers.d/hvo-roof-linux-auth";
    public const string KeyFile = ConfigurationFolder + "/assertion.key";
    public const string EnrollmentFile = ConfigurationFolder + "/enrollments.json";
    public const string WorkerFile = ConfigurationFolder + "/worker.json";
    public const string StateFile = ConfigurationFolder + "/installer-state.json";
    public const string RuntimeFolder = "/run/hvo-roof-linux-auth";
    public const string ControllerSocketFolder = RuntimeFolder + "/controller";
    public const string Audience = "hvo-roof-controller";
    internal static readonly string[] OwnedFiles = [Program, Helper, Unit, DockerOrder, Pam, Sudoers, WorkerFile, EnrollmentFile, KeyFile];
    public override StepKind Kind => StepKind.Service;
    public override string Target => Service;
    public override string Purpose => "Linux authentication: " + string.Join(", ", settings.Users.Select(user => $"{user.Name} ({user.Role}, local helper: {user.LocalAdmin})"));

    internal sealed record Enrollment(uint Uid, string UserName, string Subject, string EnrollmentStamp, bool Enabled);
    internal sealed record OwnedState(Dictionary<string, string> Sha256);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string Resource(string name)
    {
        using var stream = typeof(LinuxAuthStep).Assembly.GetManifestResourceStream("linux-auth/" + name)
            ?? throw new InstallerException("The installer lacks the Linux authentication deployment template.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static List<Enrollment> Enrollments(InstallerMachine machine)
        => machine.ReadText(EnrollmentFile) is { } text
            ? JsonSerializer.Deserialize<List<Enrollment>>(text, Json) ?? throw new InstallerException("The native enrollment file is invalid.")
            : [];

    internal static OwnedState? State(InstallerMachine machine)
        => machine.ReadText(StateFile) is { } text ? JsonSerializer.Deserialize<OwnedState>(text, Json) : null;

    internal static async Task<string?> TrustProblemAsync(InstallerMachine machine, CancellationToken token)
    {
        var checkedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in OwnedFiles.Append(StateFile))
        {
            for (var part = path; part is not null; part = Path.GetDirectoryName(part))
            {
                if (checkedPaths.Add(part))
                {
                    if (new FileInfo(machine.OnDisk(part)).LinkTarget is not null)
                        return $"Refusing the linked authentication path {part}.";
                    if (machine.FileExists(part) || machine.DirectoryExists(part))
                    {
                        if ((machine.GetMode(part) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                            return $"Authentication path {part} is writable by another user or group.";
                        // Root redirects and command fakes are a test abstraction, never a CLI install prefix.
                        if (machine.Root == "/")
                        {
                            var owner = await machine.Commands.RunAsync(new CommandLine("stat", "-c", "%u", "--", part), token).ConfigureAwait(false);
                            if (!owner.Succeeded || owner.Output.Trim() != "0")
                                return $"Authentication path {part} is not owned by root.";
                        }
                    }
                }
                if (part == "/") break;
            }
        }
        return null;
    }

    internal static string? OwnershipProblem(InstallerMachine machine, OwnedState? state)
    {
        foreach (var path in OwnedFiles.Append(StateFile))
        {
            for (var part = path; part is not null; part = Path.GetDirectoryName(part))
            {
                var info = new FileInfo(machine.OnDisk(part));
                if (info.LinkTarget is not null)
                    return $"Refusing the linked authentication path {part}.";
                if (part == "/") break;
            }
            if (path == StateFile || !machine.FileExists(path)) continue;
            if (state?.Sha256.TryGetValue(path, out var expected) != true)
                return $"Refusing unowned authentication file {path}; keep a backup and resolve it explicitly.";
            if (Convert.ToHexStringLower(SHA256.HashData(machine.ReadBytes(path)!)) != expected)
                return $"Authentication file {path} changed outside the installer; refusing to overwrite or remove it.";
        }
        return null;
    }

    private async Task<List<Enrollment>> DesiredEnrollmentAsync(InstallContext context, CancellationToken token)
    {
        var existing = Enrollments(context.Machine);
        var desired = existing.Select(entry => entry with { Enabled = false }).ToList();
        foreach (var user in settings.Users)
        {
            var lookup = await context.Machine.Commands.RunAsync(new CommandLine("getent", "passwd", user.Name), token).ConfigureAwait(false);
            var fields = lookup.Output.Trim().Split(':');
            if (!lookup.Succeeded || fields.Length != 7 || fields[0] != user.Name
                || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0)
                throw new InstallerException($"Linux authentication requires an existing non-root account named {user.Name}.");
            var prior = existing.SingleOrDefault(entry => entry.UserName == user.Name);
            if (prior is not null && prior.Uid != uid || existing.Any(entry => entry.Uid == uid && entry.UserName != user.Name))
                throw new InstallerException("A Linux enrollment no longer matches its account UID/name. Re-enroll explicitly; identity is never transferred automatically.");
            var entry = prior is null
                ? new Enrollment(uid, user.Name, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), true)
                : prior with { Enabled = true };
            desired.RemoveAll(item => item.UserName == user.Name);
            desired.Add(entry);
        }
        return [.. desired.OrderBy(item => item.UserName, StringComparer.Ordinal)];
    }

    private Dictionary<string, (string Text, UnixFileMode Mode)> Configuration(List<Enrollment> enrollments)
    {
        var users = settings.Users.Where(user => user.LocalAdmin).Select(user => user.Name).Order(StringComparer.Ordinal);
        var sudoers = "# Managed by hvo-roof-install. No arguments are permitted.\n"
            + string.Concat(users.Select(name => $"{name} ALL=(root) NOPASSWD: {Helper} \"\"\n"));
        return new()
        {
            [Helper] = (Resource("hvo-roof-local-auth"), Modes.Program),
            [Unit] = (Resource("hvo-roof-linux-auth.service"), Modes.File),
            [DockerOrder] = ("[Unit]\nWants=hvo-roof-linux-auth.service\nAfter=hvo-roof-linux-auth.service\n", Modes.File),
            [Pam] = (Resource("hvo-roof-linux-auth.pam"), Modes.File),
            [Sudoers] = (sudoers, UnixFileMode.UserRead | UnixFileMode.GroupRead),
            [EnrollmentFile] = (JsonSerializer.Serialize(enrollments, Json) + "\n", Modes.PrivateFile),
            [WorkerFile] = (JsonSerializer.Serialize(new
            {
                controllerUid = 0, controllerGroupId = 0, audience = Audience,
                assertionKeyPath = KeyFile, enrollmentPath = EnrollmentFile,
                controllerSocketPath = ControllerSocketFolder + "/worker.sock",
                localSocketPath = RuntimeFolder + "/local.sock", pamService = "hvo-roof-linux-auth"
            }, Json) + "\n", Modes.PrivateFile)
        };
    }

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (context.Machine.Os != InstallerOs.Linux)
            return new(StepChange.Blocked, "Host authentication is supported only on Linux.");
        try
        {
            var machine = context.Machine;
            if (!machine.IsRoot) return new(StepChange.Info, "Run with sudo to check private native authentication files.");
            if (await TrustProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } unsafePath)
                return new(StepChange.Blocked, unsafePath);
            var state = State(machine);
            if (OwnershipProblem(machine, state) is { } problem) return new(StepChange.Blocked, problem);
            var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            var asset = ReleaseFiles.Find(release, "linux-auth", machine.RuntimeIdentifier);
            if (asset is null) return new(StepChange.Blocked, "This release has no native Linux authentication asset for this processor.");
            var desired = Configuration(await DesiredEnrollmentAsync(context, cancellationToken).ConfigureAwait(false));
            if (state is null || !machine.FileExists(KeyFile)) return new(StepChange.Create, "Create private native configuration, a new key and explicit account enrollments.");
            if (machine.ReadBytes(KeyFile)?.Length != 32)
                return new(StepChange.Blocked, "The native assertion key must contain exactly 32 bytes.");
            if (await machine.Sha256Async(Program, cancellationToken).ConfigureAwait(false) != asset.Sha256
                || desired.Any(file => machine.ReadText(file.Key) != file.Value.Text || machine.GetMode(file.Key) != file.Value.Mode)
                || machine.GetMode(KeyFile) != Modes.PrivateFile)
                return new(StepChange.Change, "Update owned native files; retain the existing key and enrollment identities.");
            var enabled = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-enabled", Service), cancellationToken).ConfigureAwait(false);
            var active = await machine.Commands.RunAsync(new CommandLine("systemctl", "is-active", Service), cancellationToken).ConfigureAwait(false);
            return enabled.Succeeded && active.Succeeded
                ? StepCheck.Unchanged("The worker, helper and explicit enrollments match; the service is active and enabled.")
                : new(StepChange.Change, "Enable and start the installed authentication worker.");
        }
        catch (Exception error) when (error is InstallerException or JsonException or InvalidOperationException)
        {
            return new(StepChange.Blocked, error.Message);
        }
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (await TrustProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } unsafePath)
            throw new InstallerException(unsafePath);
        var state = State(machine);
        if (OwnershipProblem(machine, state) is { } problem) throw new InstallerException(problem);
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var asset = ReleaseFiles.Find(release, "linux-auth", machine.RuntimeIdentifier)
            ?? throw new InstallerException("The release lacks the Linux authentication worker.");
        var desired = Configuration(await DesiredEnrollmentAsync(context, cancellationToken).ConfigureAwait(false));
        // Validate policy before changing any installed file.
        using var work = new DeployScript.WorkFolder(machine);
        var candidate = Path.Join(work.Path, "sudoers");
        machine.WriteAtomically(candidate, desired[Sudoers].Text, Modes.PrivateFile);
        await Run(context, new CommandLine("visudo", "-cf", candidate), cancellationToken).ConfigureAwait(false);
        var downloaded = await ReleaseFiles.GetAsync(context, release, asset, work, cancellationToken).ConfigureAwait(false);
        var originals = OwnedFiles.Append(StateFile).ToDictionary(path => path,
            path => (Bytes: machine.ReadBytes(path), Mode: machine.GetMode(path)));
        var wasEnabled = (await machine.Commands.RunAsync(new CommandLine("systemctl", "is-enabled", Service), cancellationToken).ConfigureAwait(false)).Succeeded;
        var wasActive = (await machine.Commands.RunAsync(new CommandLine("systemctl", "is-active", Service), cancellationToken).ConfigureAwait(false)).Succeeded;
        try
        {
            foreach (var folder in new[] { ConfigurationFolder, ProgramFolder, "/usr/local/libexec", "/etc/sudoers.d" })
                machine.CreateDirectory(folder, folder == ConfigurationFolder ? Modes.PrivateFolder : Modes.Folder);
            machine.CreateDirectory(RuntimeFolder, Modes.PrivateFolder);
            machine.CreateDirectory(ControllerSocketFolder, Modes.PrivateFolder);
            foreach (var (path, (text, mode)) in desired)
            {
                machine.CreateDirectory(Path.GetDirectoryName(path)!, path.StartsWith(ConfigurationFolder, StringComparison.Ordinal) ? Modes.PrivateFolder : Modes.Folder);
                if (machine.ReadText(path) != text && machine.FileExists(path))
                    await machine.CopyFileAsync(path, path + ".previous", mode, cancellationToken).ConfigureAwait(false);
                machine.WriteAtomically(path, text, mode);
            }
            if (!machine.FileExists(KeyFile))
                machine.WriteAtomically(KeyFile, RandomNumberGenerator.GetBytes(32), Modes.PrivateFile);
            machine.SetMode(KeyFile, Modes.PrivateFile);
            await ProgramFile.ApplyAsync(context, Program, asset.Sha256, "native authentication worker",
                (target, token) => machine.CopyFileAsync(downloaded, target, Modes.Program, token), cancellationToken).ConfigureAwait(false);
            if (machine.Root == "/")
                await Run(context, new CommandLine("chown", ["root:root", .. OwnedFiles, ConfigurationFolder, ProgramFolder, RuntimeFolder, ControllerSocketFolder]), cancellationToken).ConfigureAwait(false);
            await Run(context, new CommandLine("systemctl", "daemon-reload"), cancellationToken).ConfigureAwait(false);
            await Run(context, new CommandLine("systemctl", "enable", Service), cancellationToken).ConfigureAwait(false);
            await Run(context, new CommandLine("systemctl", "restart", Service), cancellationToken).ConfigureAwait(false);
            await Run(context, new CommandLine("systemctl", "is-active", Service), cancellationToken).ConfigureAwait(false);
            var hashes = OwnedFiles.ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(machine.ReadBytes(path)!)));
            machine.WriteAtomically(StateFile, JsonSerializer.Serialize(new OwnedState(hashes), Json) + "\n", Modes.PrivateFile);
        }
        catch
        {
            // Roll back only files touched by this step; preserve enrolled identities and existing credentials.
            await machine.Commands.RunAsync(new CommandLine("systemctl", "stop", Service), CancellationToken.None).ConfigureAwait(false);
            foreach (var (path, original) in originals)
            {
                if (original.Bytes is null) machine.DeleteFile(path);
                else machine.WriteAtomically(path, original.Bytes, original.Mode ?? Modes.PrivateFile);
            }
            await machine.Commands.RunAsync(new CommandLine("systemctl", "daemon-reload"), CancellationToken.None).ConfigureAwait(false);
            await machine.Commands.RunAsync(new CommandLine("systemctl", wasEnabled ? "enable" : "disable", Service), CancellationToken.None).ConfigureAwait(false);
            if (wasActive && originals[Unit].Bytes is not null)
                await machine.Commands.RunAsync(new CommandLine("systemctl", "start", Service), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task Run(InstallContext context, CommandLine command, CancellationToken token)
    {
        var result = await context.Machine.Commands.RunAsync(command, token).ConfigureAwait(false);
        if (!result.Succeeded) throw new InstallerException($"Native authentication setup failed: {command.Program} exited {result.ExitCode}.");
    }
}

/// <summary>Enrolls explicitly named Linux principals through the controller's actual API after deployment.</summary>
public sealed class LinuxAuthEnrollmentStep(ControllerLayout layout, LinuxAuthSettings settings, ApiKeyAllocation adminKey) : PlanStep
{
    public override StepKind Kind => StepKind.User;
    public override string Target => "explicit Linux application principals";
    public override string Purpose => "enroll native subjects with independent controller roles; never create a Linux account";

    private List<LinuxAuthStep.Enrollment> Pending(InstallerMachine machine)
    {
        var enrollments = LinuxAuthStep.Enrollments(machine);
        var root = machine.ReadText(layout.IdentityFile) is { } text ? JsonNode.Parse(text) : null;
        var principals = root?["linuxPrincipals"]?.AsArray() ?? [];
        var pending = new List<LinuxAuthStep.Enrollment>();
        foreach (var user in settings.Users)
        {
            var native = enrollments.SingleOrDefault(entry => entry.UserName == user.Name && entry.Enabled);
            if (native is null) continue; // The preceding step creates it; Apply rechecks.
            var existing = principals.FirstOrDefault(item => (string?)item?["subject"] == native.Subject);
            if (existing is null) pending.Add(native);
            else if ((string?)existing["name"] != user.Name || (string?)existing["role"] != user.Role
                || (bool?)existing["enabled"] != true || (string?)existing["enrollmentStamp"] != native.EnrollmentStamp)
                throw new InstallerException("An existing application principal differs or is disabled. Use explicit identity administration; installation never restores roles or revoked access.");
        }
        return pending;
    }

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        try
        {
            var pending = Pending(context.Machine);
            var incomplete = settings.Users.Any(user => !LinuxAuthStep.Enrollments(context.Machine).Any(entry => entry.UserName == user.Name && entry.Enabled));
            return Task.FromResult(pending.Count > 0 || incomplete
                ? new StepCheck(StepChange.Create, "Enroll explicitly listed Linux subjects through the controller API.")
                : StepCheck.Unchanged("Explicit Linux principals are already enrolled with their requested roles."));
        }
        catch (Exception error) when (error is JsonException or InstallerException or InvalidOperationException)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, error.Message));
        }
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        if (settings.Users.Any(user => !LinuxAuthStep.Enrollments(context.Machine).Any(entry => entry.UserName == user.Name && entry.Enabled)))
            throw new InstallerException("The native enrollment step did not complete; no application principals were added.");
        var key = ControllerProbe.ReadKey(context.Machine, ApiKeyFiles.KeyFile(layout.Secrets, adminKey.Index))
            ?? throw new InstallerException("The installer's private administration key is missing.");
        foreach (var native in Pending(context.Machine))
        {
            var user = settings.Users.Single(item => item.Name == native.UserName);
            var body = JsonSerializer.Serialize(new { provider = "linux", subject = native.Subject, name = native.UserName, enrollmentStamp = native.EnrollmentStamp, role = user.Role });
            var result = await ControllerApi.SendAsync(context, MachineSurveyor.ControllerContainer, key,
                "api/v4.0/Identity/Principals", body, cancellationToken).ConfigureAwait(false);
            if (result.Status is not (200 or 201))
                throw new InstallerException($"The controller refused explicit Linux enrollment (HTTP {result.Status}); no role was overwritten.");
        }
    }
}

/// <summary>Removes only verified installer-owned runtime files. Private configuration remains in normal backups.</summary>
public sealed class LinuxAuthRemovalStep : PlanStep
{
    public override StepKind Kind => StepKind.Service;
    public override string Target => LinuxAuthStep.Service;
    public override string Purpose => "disable optional host authentication and remove its owned helper/policy; retain private data";
    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (!machine.FileExists(LinuxAuthStep.StateFile)) return StepCheck.Unchanged();
        if (await LinuxAuthStep.TrustProblemAsync(machine, cancellationToken).ConfigureAwait(false) is { } unsafePath)
            return new StepCheck(StepChange.Blocked, unsafePath);
        if (!new[] { LinuxAuthStep.Program, LinuxAuthStep.Helper, LinuxAuthStep.Unit, LinuxAuthStep.DockerOrder, LinuxAuthStep.Pam, LinuxAuthStep.Sudoers }.Any(machine.FileExists))
            return StepCheck.Unchanged("The optional native runtime and policy are removed; private data is retained.");
        var problem = LinuxAuthStep.OwnershipProblem(machine, LinuxAuthStep.State(machine));
        return problem is null ? new StepCheck(StepChange.Remove) : new StepCheck(StepChange.Blocked, problem);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        if (await LinuxAuthStep.TrustProblemAsync(context.Machine, cancellationToken).ConfigureAwait(false) is { } unsafePath)
            throw new InstallerException(unsafePath);
        if (LinuxAuthStep.OwnershipProblem(context.Machine, LinuxAuthStep.State(context.Machine)) is { } problem)
            throw new InstallerException(problem);
        await LinuxAuthStep.Run(context, new CommandLine("systemctl", "disable", "--now", LinuxAuthStep.Service), cancellationToken).ConfigureAwait(false);
        foreach (var path in new[] { LinuxAuthStep.Program, LinuxAuthStep.Helper, LinuxAuthStep.Unit, LinuxAuthStep.DockerOrder, LinuxAuthStep.Pam, LinuxAuthStep.Sudoers })
        {
            context.Machine.DeleteFile(path);
            context.Machine.DeleteFile(path + ".previous");
        }
        await LinuxAuthStep.Run(context, new CommandLine("systemctl", "daemon-reload"), cancellationToken).ConfigureAwait(false);
        // Retain ownership state so a reinstall validates the retained key and enrollment before reuse.
    }
}
