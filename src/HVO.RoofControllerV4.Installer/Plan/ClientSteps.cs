using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// hvo-roof and the Mac app on a person's own machine, set up against a running controller: the program from the
/// release, then the controller's address and how it is trusted. A private CA is fetched from the controller and saved
/// only when its fingerprint is the one given (from the controller's installer's Done page, or
/// <c>hvo-roof-install cert show</c>). The Mac app also gets a device key of its own, made through the API by an admin
/// who signs in once; the key is never shown.
/// </summary>
public static class ClientSteps
{
    /// <summary>The release's hvo-roof: its asset's kind in release.json (its platform is the machine's).</summary>
    public const string CliAssetKind = "cli";
    public const string CliProgramName = "hvo-roof";

    /// <summary>The release's Mac app: its asset's kind and platform in release.json.</summary>
    public const string MacAssetKind = "mac-app";
    public const string MacAssetPlatform = "osx-arm64";

    /// <summary>The Mac app's program, in <c>Contents/MacOS</c>, under whose name release.json has its SHA-256.</summary>
    public const string MacProgramName = "hvo-roof-mac";

    /// <summary>The variable that points the Mac app at another settings folder (docs/mac.md).</summary>
    public const string MacSettingsVariable = "HVO_ROOF_MAC_SETTINGS";

    public const string MacSettingsFile = "appsettings.Local.json";
    public const string MacCaFile = "ca.crt";
    public const string MacDeviceKeyFile = "device-key";

    /// <summary>What macOS marks a download with, so Gatekeeper stops an app that is not notarised.</summary>
    public const string QuarantineAttribute = "com.apple.quarantine";

    /// <summary>hvo-roof's credentials file, where its connection is saved.</summary>
    public static string CredentialsFile(InstallerMachine machine)
        => Path.Join(InstallPaths.UserConfigFolder(machine), "credentials.json");

    /// <summary>
    /// Where the Mac app reads its settings when it is opened from the Finder: <c>~/Library/Application Support/HVO Roof</c>.
    /// </summary>
    public static string MacSettingsFolder(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Path.Join(machine.Home, "Library", "Application Support", "HVO Roof");
    }

    /// <summary>The program inside the app <paramref name="app"/>.</summary>
    public static string MacProgram(string app) => Path.Join(app, "Contents", "MacOS", MacProgramName);

    /// <summary>The person's login keychain.</summary>
    public static string LoginKeychain(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Path.Join(machine.Home, "Library", "Keychains", "login.keychain-db");
    }

    /// <summary>
    /// hvo-roof's steps: its folder when it is the person's own, the program, then its connection. The person then signs
    /// in with <c>hvo-roof login</c>.
    /// </summary>
    public static IReadOnlyList<PlanStep> ForCli(InstallerMachine machine, CliSettings cli, ClientSettings client)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(client);
        var folder = InstallPaths.Expand(machine, cli.Folder);
        var own = cli.Folder == CliSettings.HomeFolder;
        var steps = new List<PlanStep>();
        if (own)
        {
            steps.Add(new FolderStep(folder, Modes.Folder, "your programs", keepsMode: true));
        }

        steps.Add(new CliProgramStep(folder, own, Other(CliSettings.Folders, cli.Folder)));
        steps.Add(new CliConnectionStep(CredentialsFile(machine), client));
        return steps;
    }

    /// <summary>
    /// The Mac app's steps: its settings folder, the app without macOS's quarantine mark, the controller's CA, its device
    /// key and its settings; then the app opened once with <c>--check</c>, when the Mac's own screen is there to open it on.
    /// </summary>
    public static IReadOnlyList<PlanStep> ForMacApp(InstallerMachine machine, MacAppSettings mac, ClientSettings client)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(mac);
        ArgumentNullException.ThrowIfNull(client);
        var folder = InstallPaths.Expand(machine, mac.Folder);
        var own = mac.Folder == MacAppSettings.HomeFolder;
        var settings = MacSettingsFolder(machine);
        var steps = new List<PlanStep> { new FolderStep(settings, Modes.PrivateFolder, "the Mac app's settings and device key: yours alone") };
        if (own)
        {
            steps.Add(new FolderStep(folder, Modes.Folder, "your apps", keepsMode: true));
        }

        var app = new MacAppStep(folder, settings, own, Other(MacAppSettings.Folders, mac.Folder));
        var inputs = new List<PlanStep> { app };
        if (client.CaSha256 is not null)
        {
            inputs.Add(new MacCaStep(settings, client));
        }

        inputs.Add(new MacDeviceKeyStep(settings, mac.Admin, client));
        inputs.Add(new MacSettingsStep(settings, client));
        steps.AddRange(inputs);
        steps.Add(new MacCheckStep(app.Target, settings, inputs, client));
        return steps;
    }

    /// <summary>The controller's CA trusted in the login keychain, when the person chose it (on a Mac).</summary>
    public static IReadOnlyList<PlanStep> ForKeychain(InstallerMachine machine, ClientSettings client)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(client);
        return client is { TrustInKeychain: true, CaSha256: not null } ? [new KeychainTrustStep(LoginKeychain(machine), client)] : [];
    }

    /// <summary>
    /// Why the installer cannot put a program in <paramref name="folder"/>, or null when it can: it runs without sudo, so
    /// the folder must be there (or made by the plan, when <paramref name="planned"/>) and the person's to write to.
    /// </summary>
    internal static string? FolderProblem(InstallerMachine machine, string folder, bool planned, string instead)
    {
        if (!machine.DirectoryExists(folder))
        {
            return planned ? null : $"{folder} is not there: choose {instead}";
        }

        return machine.IsWritable(folder)
            ? null
            : $"you cannot write to {folder} without sudo, and the installer never runs as root for your own programs: choose {instead}";
    }

    /// <summary>
    /// True when the installer runs in the Mac's own session, where a window can open and macOS can ask for a password;
    /// false over SSH.
    /// </summary>
    internal static async Task<bool> OnOwnScreenAsync(InstallerMachine machine, CancellationToken cancellationToken)
    {
        var manager = await machine.Commands.RunAsync(new CommandLine("launchctl", "managername"), cancellationToken).ConfigureAwait(false);
        return manager.Succeeded && manager.Output.Trim() == "Aqua";
    }

    /// <summary>True when any file of <paramref name="app"/> carries macOS's quarantine mark.</summary>
    internal static async Task<bool> QuarantinedAsync(InstallerMachine machine, string app, CancellationToken cancellationToken)
    {
        // xattr -r lists "file: attribute" for each file's attributes; nothing for a file that has none.
        var listing = await machine.Commands.RunAsync(new CommandLine("xattr", "-r", app), cancellationToken).ConfigureAwait(false);
        return listing.Output.Split('\n', StringSplitOptions.TrimEntries).Any(line => line.EndsWith($": {QuarantineAttribute}", StringComparison.Ordinal));
    }

    private static string Other(IReadOnlyList<string> folders, string folder) => folders.First(candidate => candidate != folder);
}

/// <summary>
/// How a client on this machine trusts the controller, and its connection to it. The CA the controller serves is fetched
/// once a run (<see cref="InstallContext.ControllerCaAsync"/>), and used only when its fingerprint is the one given.
/// </summary>
internal static class ClientTrust
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The address and how it is trusted, for the plan.</summary>
    public static string Describe(ClientSettings client)
        => client.CaSha256 is { } authority ? $"{client.Controller}, trusting its CA ({Short(authority)})"
            : client.CertificateSha256 is { } pin ? $"{client.Controller}, pinning its certificate ({Short(pin)})"
            : client.Address?.Scheme == Uri.UriSchemeHttp ? $"{client.Controller} over plain HTTP"
            : $"{client.Controller}, trusted as this machine trusts any website";

    /// <summary>True when <paramref name="saved"/> trusts the controller as <paramref name="client"/> says to, and no other way.</summary>
    public static bool Trusts(RoofStoredCredentials saved, ClientSettings client)
    {
        if (client.CaSha256 is { } fingerprint)
        {
            if (saved.CertificateSha256 is not null || saved.CaCertificate is null)
            {
                return false;
            }

            try
            {
                using var authority = RoofCertificateAuthority.FromPem(saved.CaCertificate);
                return RoofCertificateAuthority.HasFingerprint(authority, fingerprint);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return client.CertificateSha256 is { } pin
            ? saved.CaCertificate is null && ClientSettings.Colons(saved.CertificateSha256) == pin
            : saved.CaCertificate is null && saved.CertificateSha256 is null;
    }

    /// <summary>
    /// The CA the controller serves, when its fingerprint is the one given; else why it is not used, ending a sentence.
    /// The certificate is the run's (see <see cref="InstallContext.ControllerCaAsync"/>): it is never disposed here.
    /// </summary>
    public static async Task<(X509Certificate2? Authority, string? Problem)> AuthorityAsync(InstallContext context, ClientSettings client, CancellationToken cancellationToken)
    {
        var address = client.Address!;
        X509Certificate2 authority;
        try
        {
            authority = await context.ControllerCaAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or RoofCaFetchException or RoofCertificateRefusedException or HttpRequestException or TimeoutException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, $"the installer could not fetch the controller's CA from {address}: {Failure(error)}");
        }

        return RoofCertificateAuthority.HasFingerprint(authority, client.CaSha256!)
            ? (authority, null)
            : (null, $"the CA {address} serves ({RoofCertificateAuthority.Describe(authority)}, {RoofCertificateAuthority.Fingerprint(authority)}) is not the one whose "
                + "fingerprint was given: compare it with the controller's installer's Done page, or hvo-roof-install cert show on the controller");
    }

    /// <summary>A client of the controller that trusts it as <paramref name="client"/> says, signed in with <paramref name="credential"/>.</summary>
    public static RoofControllerClient Connect(InstallContext context, ClientSettings client, X509Certificate2? authority, RoofCredential? credential)
        => new(new RoofConnectionOptions
        {
            BaseAddress = client.Address!,
            Credential = credential,
            RequestTimeout = Timeout,
            ServerCaCertificate = authority,
            ServerCertificateSha256 = authority is null ? client.CertificateSha256 : null,
            CreateHandler = context.Machine.ApiHandler
        });

    /// <summary>True for a failure to reach the controller (as opposed to its answer).</summary>
    public static bool IsUnreachable(Exception error, CancellationToken cancellationToken)
        => error is HttpRequestException or TimeoutException or RoofProtocolException
            || RoofCertificateRefusedException.Find(error) is not null
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Why the controller could not be reached or used, in a few words.</summary>
    public static string Failure(Exception error)
        => error is RoofCaFetchException ? error.Message : RoofCertificateRefusedException.Find(error)?.Message ?? error switch
        {
            ArgumentException => "it is not an https address",
            HttpRequestException => $"{error.Message.TrimEnd('.')}.",
            _ => RoofText.DescribeFailure(error is OperationCanceledException ? new TimeoutException() : error)
        };

    private static string Short(string fingerprint) => fingerprint.Length > 11 ? $"{fingerprint[..11]}…" : fingerprint;
}

/// <summary>
/// hvo-roof, the release's for this machine (<c>hvo-roof-RID</c>): checked against release.json's size and SHA-256
/// before it goes in place in one step. The one it replaces is kept as <c>hvo-roof.previous</c>.
/// </summary>
public sealed class CliProgramStep(string folder, bool planned, string instead) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => Path.Join(folder, ClientSteps.CliProgramName);

    public override string Purpose => "hvo-roof: the roof from the command line, and its terminal UI";

    private string Previous => Target + ".previous";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (ReleaseFiles.Find(release, ClientSteps.CliAssetKind, machine.RuntimeIdentifier) is not { } asset)
        {
            return new StepCheck(StepChange.Blocked, $"release {release.Version} has no hvo-roof for {machine.RuntimeIdentifier}");
        }

        if (ClientSteps.FolderProblem(machine, folder, planned, instead) is { } problem)
        {
            return new StepCheck(StepChange.Blocked, problem);
        }

        if (machine.DirectoryExists(Target))
        {
            return new StepCheck(StepChange.Blocked, $"{Target} is a folder: move it aside, then run the installer again");
        }

        var current = await machine.Sha256Async(Target, cancellationToken).ConfigureAwait(false);
        return current is null ? new StepCheck(StepChange.Create, $"release {release.Version}'s, {Modes.Octal(Modes.Program)}")
            : current != asset.Sha256 ? new StepCheck(StepChange.Change, $"release {release.Version}'s; the one there now is kept as {Path.GetFileName(Previous)}")
            : machine.GetMode(Target) is { } mode && mode != Modes.Program ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.Program)}")
            : StepCheck.Unchanged($"release {release.Version}'s");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var asset = ReleaseFiles.Find(release, ClientSteps.CliAssetKind, machine.RuntimeIdentifier)
            ?? throw new InstallerException($"Release {release.Version} has no hvo-roof for {machine.RuntimeIdentifier}.");
        if (await machine.Sha256Async(Target, cancellationToken).ConfigureAwait(false) == asset.Sha256)
        {
            machine.SetMode(Target, Modes.Program);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.Program)}.");
            return;
        }

        using var work = new DeployScript.WorkFolder(machine);
        var file = await ReleaseFiles.GetAsync(context, release, asset, work, cancellationToken).ConfigureAwait(false);
        var staged = $"{Target}.new";
        try
        {
            // A copy, not the download itself: it carries no extended attributes, so macOS has no quarantine mark to act on.
            await machine.CopyFileAsync(file, staged, Modes.Program, cancellationToken).ConfigureAwait(false);
            if (machine.FileExists(Target))
            {
                await machine.CopyFileAsync(Target, Previous, Modes.Program, cancellationToken).ConfigureAwait(false);
                context.Log.Write($"Kept the hvo-roof it replaces as {Previous}.");
            }

            // A new file in its place, not the old one written over: macOS checks a program's signature by its file.
            machine.MoveFile(staged, Target);
        }
        finally
        {
            if (machine.FileExists(staged))
            {
                machine.DeleteFile(staged);
            }
        }

        context.Log.Write($"Installed hvo-roof from {asset.Name} (release {release.Version}) as {Target}.");
    }
}

/// <summary>
/// hvo-roof's connection, in its credentials file (<c>0600</c>, in a <c>0700</c> folder): the controller's address and
/// how it is trusted, as <c>hvo-roof setup</c> saves them. A sign-in saved for the same controller is kept; one saved for
/// another controller is removed, so it is never sent to this one.
/// </summary>
public sealed class CliConnectionStep(string path, ClientSettings client) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => path;

    public override string Purpose => "hvo-roof's connection: the controller and how it is trusted (you sign in with hvo-roof login)";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (saved, problem) = Load(context.Machine);
        if (problem is not null)
        {
            return new StepCheck(StepChange.Blocked, $"{problem} Correct it, or move it aside, then run the installer again");
        }

        var address = client.Address!;
        if (saved is not null && saved.Controller == address && ClientTrust.Trusts(saved, client))
        {
            return StepCheck.Unchanged(ClientTrust.Describe(client));
        }

        if (client.CaSha256 is not null && (await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false)).Problem is { } fetch)
        {
            return new StepCheck(StepChange.Blocked, fetch);
        }

        var detail = ClientTrust.Describe(client);
        if (saved?.Controller is { } old && old != address && (saved.ApiKey is not null || saved.Session is not null))
        {
            detail += $"; the sign-in saved for {old} is removed";
        }

        return new StepCheck(saved is null ? StepChange.Create : StepChange.Change, detail);
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (saved, problem) = Load(context.Machine);
        if (problem is not null)
        {
            throw new InstallerException($"hvo-roof's connection was not saved: {problem}");
        }

        string? authority = null;
        if (client.CaSha256 is not null)
        {
            var (certificate, fetch) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
            authority = certificate is not null
                ? RoofCertificateAuthority.ToPem(certificate)
                : throw new InstallerException($"hvo-roof's connection was not saved: {fetch}.");
        }

        var address = client.Address!;
        var kept = saved is not null && saved.Controller == address ? saved : new RoofStoredCredentials();
        try
        {
            RoofCredentialStore.Save(context.Machine.OnDisk(Target), kept with
            {
                Controller = address,
                CertificateSha256 = client.CertificateSha256,
                CaCertificate = authority
            });
        }
        catch (Exception error) when (error is RoofCredentialFileException or IOException or UnauthorizedAccessException)
        {
            throw new InstallerException($"hvo-roof's connection was not saved: {error.Message}", error);
        }

        context.Log.Write($"Saved hvo-roof's connection in {Target}: {check.Detail}.");
    }

    private (RoofStoredCredentials? Saved, string? Problem) Load(InstallerMachine machine)
    {
        try
        {
            return (RoofCredentialStore.Load(machine.OnDisk(Target)), null);
        }
        catch (Exception error) when (error is RoofCredentialFileException or IOException or UnauthorizedAccessException)
        {
            return (null, error.Message);
        }
    }
}

/// <summary>
/// The Mac app (<c>HVO Roof.app</c>), from the release's zip: unpacked beside where it goes, checked against its
/// program's SHA-256 in release.json, cleared of macOS's quarantine mark (it is signed, but not notarised), then put in
/// place. The app it replaces is kept in the settings folder as <c>HVO Roof.app.previous</c>, out of Launchpad's sight.
/// </summary>
public sealed class MacAppStep(string folder, string settingsFolder, bool planned, string instead) : PlanStep
{
    private bool _onlyQuarantine;

    public override StepKind Kind => StepKind.File;

    public override string Target => Path.Join(folder, MachineSurveyor.MacAppBundle);

    public override string Purpose => "the Mac app";

    private string Previous => Path.Join(settingsFolder, $"{MachineSurveyor.MacAppBundle}.previous");

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        _onlyQuarantine = false;
        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (Wanted(release) is not { } wanted)
        {
            return new StepCheck(StepChange.Blocked, $"release {release.Version} has no Mac app with its program's SHA-256 in {ReleaseManifest.FileName}");
        }

        if (ClientSteps.FolderProblem(machine, folder, planned, instead) is { } problem)
        {
            return new StepCheck(StepChange.Blocked, problem);
        }

        if (machine.FileExists(Target))
        {
            return new StepCheck(StepChange.Blocked, $"{Target} is a file, not the app: move it aside, then run the installer again");
        }

        if (!machine.DirectoryExists(Target))
        {
            return new StepCheck(StepChange.Create, $"release {release.Version}'s, without macOS's quarantine mark");
        }

        if (await machine.Sha256Async(ClientSteps.MacProgram(Target), cancellationToken).ConfigureAwait(false) != wanted.Sha256)
        {
            return new StepCheck(StepChange.Change, $"release {release.Version}'s; the one there now is kept as {Previous}");
        }

        if (await ClientSteps.QuarantinedAsync(machine, Target, cancellationToken).ConfigureAwait(false))
        {
            _onlyQuarantine = true;
            return new StepCheck(StepChange.Change, "macOS's quarantine mark removed, so the app opens");
        }

        return StepCheck.Unchanged($"release {release.Version}'s");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        if (_onlyQuarantine)
        {
            await UnquarantineAsync(context, Target, cancellationToken).ConfigureAwait(false);
            context.Log.Write($"Removed macOS's quarantine mark from {Target}.");
            return;
        }

        var release = await context.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        var (asset, sha256) = Wanted(release) ?? throw new InstallerException($"Release {release.Version} has no Mac app.");
        using var work = new DeployScript.WorkFolder(machine);
        var zip = await ReleaseFiles.GetAsync(context, release, asset, work, cancellationToken).ConfigureAwait(false);

        // Unpacked beside where it goes, so it is put in place by a rename.
        var staging = Path.Join(folder, $".hvo-roof-install-{Guid.NewGuid():N}");
        machine.CreateDirectory(staging, Modes.PrivateFolder);
        try
        {
            await KioskSteps.Run(
                context,
                $"unpack {asset.Name}",
                new CommandLine("ditto", "-x", "-k", "--noqtn", zip, staging),
                cancellationToken).ConfigureAwait(false);
            var staged = Path.Join(staging, MachineSurveyor.MacAppBundle);
            if (await machine.Sha256Async(ClientSteps.MacProgram(staged), cancellationToken).ConfigureAwait(false) != sha256)
            {
                throw new InstallerException($"The Mac app in {asset.Name} is not the one {ReleaseManifest.FileName} names: its program's SHA-256 differs.");
            }

            await UnquarantineAsync(context, staged, cancellationToken).ConfigureAwait(false);
            if (machine.DirectoryExists(Target))
            {
                if (!machine.DirectoryExists(settingsFolder))
                {
                    machine.CreateDirectory(settingsFolder, Modes.PrivateFolder);
                }

                machine.DeleteDirectory(Previous);
                machine.MoveDirectory(Target, Previous);
                context.Log.Write($"Kept the Mac app it replaces as {Previous}.");
            }

            machine.MoveDirectory(staged, Target);
        }
        finally
        {
            machine.DeleteDirectory(staging);
        }

        context.Log.Write($"Installed the Mac app from {asset.Name} (release {release.Version}) as {Target}.");
    }

    private static (ReleaseAsset Asset, string Sha256)? Wanted(ReleaseManifest release)
        => ReleaseFiles.Find(release, ClientSteps.MacAssetKind, ClientSteps.MacAssetPlatform) is { } asset
            && asset.Files.TryGetValue(ClientSteps.MacProgramName, out var sha256)
            ? (asset, sha256)
            : null;

    private static async Task UnquarantineAsync(InstallContext context, string app, CancellationToken cancellationToken)
    {
        // It says nothing useful for a file without the mark: what counts is that none is left.
        await context.Machine.Commands.RunAsync(new CommandLine("xattr", "-dr", ClientSteps.QuarantineAttribute, app), cancellationToken).ConfigureAwait(false);
        if (await ClientSteps.QuarantinedAsync(context.Machine, app, cancellationToken).ConfigureAwait(false))
        {
            throw new InstallerException($"macOS's quarantine mark is still on {app}: remove it (xattr -dr {ClientSteps.QuarantineAttribute} \"{app}\"), then run the installer again.");
        }
    }
}

/// <summary>The controller's CA, for the Mac app (<c>ca.crt</c> in its settings folder), saved only when its fingerprint is the one given.</summary>
public sealed class MacCaStep(string settingsFolder, ClientSettings client) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => Path.Join(settingsFolder, ClientSteps.MacCaFile);

    public override string Purpose => "the controller's CA: the Mac app trusts the controller's certificate by it";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var current = machine.ReadText(Target);
        if (current is not null && Holds(current))
        {
            return machine.GetMode(Target) is { } mode && mode != Modes.File
                ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.File)}")
                : StepCheck.Unchanged(ClientTrust.Describe(client));
        }

        if ((await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false)).Problem is { } problem)
        {
            return new StepCheck(StepChange.Blocked, problem);
        }

        return new StepCheck(current is null ? StepChange.Create : StepChange.Change, ClientTrust.Describe(client));
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        if (machine.ReadText(Target) is { } current && Holds(current))
        {
            machine.SetMode(Target, Modes.File);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.File)}.");
            return;
        }

        var (authority, problem) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            throw new InstallerException($"The Mac app's CA was not saved: {problem}.");
        }

        machine.WriteAtomically(Target, RoofCertificateAuthority.ToPem(authority), Modes.File);
        context.Log.Write($"Wrote {Target}: the controller's CA, {RoofCertificateAuthority.Describe(authority)} ({RoofCertificateAuthority.Fingerprint(authority)}).");
    }

    private bool Holds(string pem)
    {
        try
        {
            using var authority = RoofCertificateAuthority.FromPem(pem);
            return RoofCertificateAuthority.HasFingerprint(authority, client.CaSha256!);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The Mac app's device key (<c>device-key</c>, <c>0600</c>): an API key of its own with the Viewer role (not a kiosk
/// key), made through the controller's API by <paramref name="admin"/>, who signs in once; their session ends as soon as
/// the key is made. A key there that the controller takes is kept; one it refuses is replaced. The key is written
/// straight to the file: it is never shown, logged, or put on the clipboard.
/// </summary>
public sealed class MacDeviceKeyStep(string settingsFolder, string admin, ClientSettings client) : PlanStep
{
    private bool _needsKey;

    public override StepKind Kind => StepKind.File;

    public override string Target => Path.Join(settingsFolder, ClientSteps.MacDeviceKeyFile);

    public override string Purpose => "the Mac app's device key: a Viewer's, which shows the roof and offers Stop when no one is signed in";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        _needsKey = true;
        var made = $"a Viewer key ({KeyName(machine)}) made by {admin}, who signs in once; {Modes.Octal(Modes.PrivateFile)}, yours alone";
        var text = machine.ReadText(Target);
        if (text is null)
        {
            return new StepCheck(StepChange.Create, made);
        }

        var key = text.TrimEnd('\r', '\n');
        if (!IsKey(key))
        {
            return new StepCheck(StepChange.Change, $"{made}: the file there does not hold one key on one line");
        }

        context.Log.AddSecret(key);
        var (refused, note) = await CheckKeyAsync(context, key, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return new StepCheck(StepChange.Change, $"{made}: {refused}");
        }

        _needsKey = false;
        return machine.GetMode(Target) is { } mode && mode != Modes.PrivateFile
            ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.PrivateFile)}")
            : StepCheck.Unchanged(note);
    }

    public override IReadOnlyList<InstallSecret> SecretsNeeded(StepCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.MakesChange && _needsKey ? [InstallSecret.SignInPassword] : [];
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        if (!_needsKey)
        {
            machine.SetMode(Target, Modes.PrivateFile);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.PrivateFile)}.");
            return;
        }

        var password = context.Secrets[InstallSecret.SignInPassword]
            ?? throw new InstallerException($"The install needs {InstallSecrets.Describe(InstallSecret.SignInPassword)}, which was not given.");
        context.Log.AddSecret(password);
        X509Certificate2? authority = null;
        if (client.CaSha256 is not null)
        {
            (authority, var problem) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
            if (authority is null)
            {
                throw new InstallerException($"The Mac's device key was not made: {problem}.");
            }
        }

        using var api = ClientTrust.Connect(context, client, authority, credential: null);
        try
        {
            api.Credential = await api.Auth.SignInAsync(admin, password, cancellationToken).ConfigureAwait(false);
        }
        catch (RoofApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InstallerException($"The controller refused {admin}'s name or password, so the Mac's device key was not made. Run the installer again with the right password.");
        }
        catch (Exception error) when (error is RoofApiException || ClientTrust.IsUnreachable(error, cancellationToken))
        {
            throw new InstallerException($"{admin} could not sign in to {client.Controller} ({ClientTrust.Failure(error)}), so the Mac's device key was not made. Run the installer again once the controller answers.");
        }

        context.Log.AddSecret(((RoofSessionCredential)api.Credential).Token);
        try
        {
            var (name, secret, rotated) = await MakeKeyAsync(api, KeyName(machine), cancellationToken).ConfigureAwait(false);
            context.Log.AddSecret(secret);
            machine.WriteAtomically(Target, secret, Modes.PrivateFile);
            context.Log.Write($"Wrote {Target} ({Modes.Octal(Modes.PrivateFile)}): {(rotated ? "a new secret for" : "the new")} Viewer key {name}, made by {admin}.");
        }
        catch (RoofApiException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InstallerException($"{admin} may not add API keys, so the Mac's device key was not made: give an admin's name, then run the installer again.");
        }
        catch (Exception error) when (error is RoofApiException || ClientTrust.IsUnreachable(error, cancellationToken))
        {
            throw new InstallerException($"The controller did not make the Mac's device key ({ClientTrust.Failure(error)}). Run the installer again.");
        }
        finally
        {
            await SignOutAsync(context, api).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The key's name on the controller: <c>mac-HOST-USER</c>, from this Mac's short host name and the person's user name,
    /// with anything the controller does not take in a name made a dash.
    /// </summary>
    public static string KeyName(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var raw = $"mac-{machine.HostName.Split('.')[0]}-{machine.UserName}";
        var name = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-' ? c : '-').ToArray());
        return name.Length > 64 ? name[..64] : name;
    }

    // One API key on one line, as the Mac app reads it.
    private static bool IsKey(string key)
        => key.Length > 0 && key.All(c => c is >= ' ' and <= '~') && key.Trim().Length == key.Length;

    // Why the key there is replaced (null when it is kept), and what the plan says of one that is kept.
    private async Task<(string? Refused, string Note)> CheckKeyAsync(InstallContext context, string key, CancellationToken cancellationToken)
    {
        X509Certificate2? authority = null;
        if (client.CaSha256 is not null)
        {
            (authority, var problem) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
            if (authority is null)
            {
                return (null, $"kept: it could not be checked, as {problem}");
            }
        }

        using var api = ClientTrust.Connect(context, client, authority, new RoofApiKeyCredential(key));
        try
        {
            var caller = await api.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
            return caller.IsKiosk || !string.Equals(caller.Role, RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase)
                ? ($"the key there is {(caller.IsKiosk ? "a kiosk key" : $"a {caller.Role} key")}, and a Mac's is a Viewer's", string.Empty)
                : (null, $"the controller takes it ({caller.Name})");
        }
        catch (RoofApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ("the controller refuses the key there", string.Empty);
        }
        catch (Exception error) when (error is RoofApiException || ClientTrust.IsUnreachable(error, cancellationToken))
        {
            return (null, $"kept: it could not be checked ({ClientTrust.Failure(error)})");
        }
    }

    // A new key, or a new secret for the Mac's key the controller has already (made by an earlier install).
    private static async Task<(string Name, string Secret, bool Rotated)> MakeKeyAsync(RoofControllerClient api, string name, CancellationToken cancellationToken)
    {
        try
        {
            var made = await api.Identity.AddApiKeyAsync(
                new RoofApiKeyCreateRequest { Name = name, Role = RoofControllerApiContract.ViewerRole, Kiosk = false },
                cancellationToken).ConfigureAwait(false);
            return (made.Key.Name, made.Secret, false);
        }
        catch (RoofApiException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            var keys = await api.Identity.GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
            var existing = keys.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not { Kiosk: false, Source: RoofApiKeySource.Managed }
                || !string.Equals(existing.Role, RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallerException(
                    $"The controller has a key named {name} that is not a Mac's (a Viewer key, not a kiosk key, added through the API): remove it on the web UI's People page, then run the installer again.");
            }

            var rotated = await api.Identity.RotateApiKeyAsync(existing.Name, cancellationToken).ConfigureAwait(false);
            return (rotated.Key.Name, rotated.Secret, true);
        }
    }

    private static async Task SignOutAsync(InstallContext context, RoofControllerClient api)
    {
        try
        {
            // Not cancelled with the run: the session should end even when the install stops here.
            await api.Auth.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is RoofApiException || ClientTrust.IsUnreachable(error, CancellationToken.None))
        {
            context.Log.Write($"Could not end the admin's session ({ClientTrust.Failure(error)}): it ends when it expires.");
        }
    }
}

/// <summary>
/// The Mac app's settings (<c>appsettings.Local.json</c>): the controller's address, how it is trusted (its CA's file, or
/// its certificate's pin), and the device key's file. Settings a person changed by hand are kept.
/// </summary>
public sealed class MacSettingsStep(string settingsFolder, ClientSettings client) : PlanStep
{
    private const string Section = "Mac";

    public override StepKind Kind => StepKind.File;

    public override string Target => Path.Join(settingsFolder, ClientSteps.MacSettingsFile);

    public override string Purpose => "the Mac app's settings: the controller's address, how it trusts it, and its key";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (current, wanted, problem) = Settings(context.Machine);
        if (problem is not null)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, problem));
        }

        var mode = context.Machine.GetMode(Target);
        return Task.FromResult(
            current is null ? new StepCheck(StepChange.Create, ClientTrust.Describe(client))
            : !JsonNode.DeepEquals(current, wanted) ? new StepCheck(StepChange.Change, ClientTrust.Describe(client))
            : mode is { } found && found != Modes.File ? new StepCheck(StepChange.Change, $"{Modes.Octal(found)} → {Modes.Octal(Modes.File)}")
            : StepCheck.Unchanged(ClientTrust.Describe(client)));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (_, wanted, problem) = Settings(context.Machine);
        if (problem is not null || wanted is null)
        {
            throw new InstallerException($"The Mac app's settings could not be written: {problem}.");
        }

        context.Machine.WriteAtomically(Target, wanted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Modes.File);
        context.Log.Write($"Wrote {Target}: {ClientTrust.Describe(client)}.");
        return Task.CompletedTask;
    }

    // The settings there (null when none), the settings wanted, or why they cannot be told.
    private (JsonObject? Current, JsonObject? Wanted, string? Problem) Settings(InstallerMachine machine)
    {
        var text = machine.ReadText(Target);
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
                return (null, null, "it is not settings the Mac app reads: correct it, or move it aside, then run the installer again");
            }
        }

        var wanted = (JsonObject?)current?.DeepClone() ?? [];
        if (wanted[Section] is not JsonObject mac)
        {
            mac = [];
            wanted[Section] = mac;
        }

        if (!(mac["ControllerUrl"] is JsonValue url && url.TryGetValue<string>(out var existing)
            && Uri.TryCreate(existing, UriKind.Absolute, out var uri) && uri == client.Address))
        {
            mac["ControllerUrl"] = client.Controller;
        }

        Set(mac, "ServerCaCertificateFile", client.CaSha256 is not null ? ClientSteps.MacCaFile : null);
        Set(mac, "ServerCertificateSha256", client.CertificateSha256);
        Set(mac, "DeviceKeyFile", ClientSteps.MacDeviceKeyFile);
        return (current, wanted, null);
    }

    // A setting to have (a value), or not to have: one there already is set to null, which the app reads as not set.
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

/// <summary>
/// The Mac app opened once with <c>--check</c>, which exits once its window is drawn: after any change to the app, its
/// CA, its key or its settings, or until a run has passed it: the person's install record, written last, shows the app at
/// this release connected to this controller only then. It needs the Mac's own screen: over SSH it is left to the person.
/// </summary>
public sealed class MacCheckStep(string app, string settingsFolder, IReadOnlyList<PlanStep> inputs, ClientSettings client) : PlanStep
{
    public override StepKind Kind => StepKind.Check;

    public override string Target => $"{MachineSurveyor.MacAppBundle} --check";

    public override string Purpose => "opens the app's window once, and closes it: it starts with these settings";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!await ClientSteps.OnOwnScreenAsync(context.Machine, cancellationToken).ConfigureAwait(false))
        {
            return new StepCheck(StepChange.Info, "not run: this is not the Mac's own screen (over SSH, say): open HVO Roof to check it");
        }

        foreach (var input in inputs)
        {
            if (context.HasApplied(input) || (await input.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange)
            {
                return new StepCheck(StepChange.Change, "after the changes above");
            }
        }

        // A check that failed stopped the run before the record: everything else is in place, and it is still due. The
        // keychain is not the app's, so trusting the CA there or not does not call for another.
        if (context.Survey.UserRecord is not { } record || !record.Roles.Contains(InstallRole.MacApp)
            || record.Version != context.Version || record.Client is not { } recorded
            || recorded.Normalised() with { TrustInKeychain = false } != client.Normalised() with { TrustInKeychain = false })
        {
            return new StepCheck(StepChange.Change, "not checked yet with this release and controller");
        }

        return StepCheck.Unchanged("nothing changed since it was set up");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await context.Machine.Commands.RunAsync(
            new CommandLine(ClientSteps.MacProgram(app), "--check")
            {
                // The settings the installer wrote, whatever the person's shell points the app at.
                Environment = new Dictionary<string, string> { [ClientSteps.MacSettingsVariable] = settingsFolder },
                Timeout = TimeSpan.FromMinutes(2)
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InstallerException(result.ExitCode == ExitSettings
                ? $"The Mac app did not start with its settings ({result.Reason}). Correct them in {settingsFolder}, or run the installer again."
                : $"The Mac app did not open its window ({result.Reason}). Open HVO Roof to see why, then run the installer again.");
        }

        context.Log.Write($"Checked {MachineSurveyor.MacAppBundle}: it opened its window with its settings.");
    }

    // The app's exit code for settings it cannot start with (EX_CONFIG).
    private const int ExitSettings = 78;
}

/// <summary>
/// The controller's CA trusted for websites in the person's login keychain, so Safari and Chrome open the web UI without
/// a warning. macOS asks for the person's password to change their trust settings, on the Mac's own screen. The CA is
/// found by its fingerprint, and its trust by its SHA-1 (what the trust settings are keyed by), so a CA of the same name
/// (one made again the same day) is not taken for it, and a run with the controller out of reach finds it trusted.
/// </summary>
public sealed class KeychainTrustStep(string keychain, ClientSettings client) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => keychain;

    public override string Purpose => "trusts the controller's CA in your login keychain: Safari and Chrome open the web UI without a warning";

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var fingerprint = client.CaSha256!;
        var described = $"the controller's CA ({Short(fingerprint)})";
        if (await IsTrustedAsync(context, Hex(fingerprint), cancellationToken).ConfigureAwait(false))
        {
            return StepCheck.Unchanged($"{described}, trusted for websites");
        }

        // Not trusted yet: the CA is needed, from the controller, to add.
        var (authority, problem) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            return new StepCheck(StepChange.Blocked, problem);
        }

        var name = RoofCertificateAuthority.Describe(authority);
        return await ClientSteps.OnOwnScreenAsync(machine, cancellationToken).ConfigureAwait(false)
            ? new StepCheck(StepChange.Change, $"{name}, trusted for websites: macOS asks for your password")
            : new StepCheck(StepChange.Blocked, "macOS asks for your password to trust a CA, on the Mac's own screen, and this is not it (over SSH, say): run the installer there, or leave the keychain out");
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var machine = context.Machine;
        var (authority, problem) = await ClientTrust.AuthorityAsync(context, client, cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            throw new InstallerException($"The controller's CA was not trusted in your keychain: {problem}.");
        }

        using var work = new DeployScript.WorkFolder(machine);
        var file = Path.Join(work.Path, ClientSteps.MacCaFile);
        machine.WriteAtomically(file, RoofCertificateAuthority.ToPem(authority), Modes.File);
        context.Progress?.Invoke("macOS asks for your password to trust the controller's CA…");
        await KioskSteps.Run(
            context,
            "trust the controller's CA in your login keychain",
            new CommandLine("security", "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", keychain, file) { Timeout = TimeSpan.FromMinutes(5) },
            cancellationToken).ConfigureAwait(false);
        context.Log.Write($"Trusted the controller's CA ({RoofCertificateAuthority.Describe(authority)}, {RoofCertificateAuthority.Fingerprint(authority)}) for websites in {keychain}.");
    }

    /// <summary>
    /// The certificates <c>security find-certificate -a -Z</c> lists, each by its SHA-256 and SHA-1 (hex, upper case),
    /// which it prints before the rest of each.
    /// </summary>
    public static IReadOnlyList<(string Sha256, string Sha1)> KeychainHashes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var found = new List<(string, string)>();
        string? sha256 = null;
        string? sha1 = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(Sha256Label, StringComparison.Ordinal))
            {
                sha256 = Hex(line[Sha256Label.Length..]);
            }
            else if (line.StartsWith(Sha1Label, StringComparison.Ordinal))
            {
                sha1 = Hex(line[Sha1Label.Length..]);
            }
            else if (line.StartsWith("keychain:", StringComparison.Ordinal))
            {
                // The hashes are each certificate's first lines: a pair not complete by now is not one.
                sha256 = sha1 = null;
            }

            if (sha256 is not null && sha1 is not null)
            {
                found.Add((sha256, sha1));
                sha256 = sha1 = null;
            }
        }

        return found;
    }

    /// <summary>
    /// The certificates trusted for websites in trust settings as <c>security trust-settings-export</c> writes them (as
    /// XML), by their SHA-1 (hex, upper case): trusted as a root for every use or for SSL servers, and not denied there.
    /// </summary>
    public static IReadOnlySet<string> TrustedForWebsites(string plist)
    {
        ArgumentNullException.ThrowIfNull(plist);
        using var reader = XmlReader.Create(new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var root = XDocument.Load(reader).Root?.Element("dict");
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        if (root is null || Entries(root).FirstOrDefault(entry => entry.Key == "trustList").Value is not { Name.LocalName: "dict" } list)
        {
            return trusted;
        }

        foreach (var (sha1, certificate) in Entries(list))
        {
            var usages = Entries(certificate).FirstOrDefault(entry => entry.Key == "trustSettings").Value?.Elements("dict").ToList() ?? [];
            var forWebsites = usages.Select(Entries).Select(usage => usage.ToDictionary(entry => entry.Key, entry => entry.Value)).Where(ForWebsites).ToList();
            var results = forWebsites.Select(usage => usage.TryGetValue("kSecTrustSettingsResult", out var result) ? (int?)int.Parse(result.Value, CultureInfo.InvariantCulture) : TrustRoot).ToList();

            // No usages at all is trust as a root for everything.
            if ((usages.Count == 0 || results.Any(result => result is TrustRoot or TrustAsRoot)) && !results.Contains(Deny))
            {
                trusted.Add(Hex(sha1));
            }
        }

        return trusted;
    }

    // Whether the CA with this SHA-256 is in the keychain, and trusted for websites in the person's trust settings.
    private async Task<bool> IsTrustedAsync(InstallContext context, string sha256, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var found = await machine.Commands.RunAsync(new CommandLine("security", "find-certificate", "-a", "-Z", keychain), cancellationToken).ConfigureAwait(false);
        if (!found.Succeeded || KeychainHashes(found.Output).FirstOrDefault(entry => entry.Sha256 == sha256).Sha1 is not { } sha1)
        {
            return false;
        }

        using var work = new DeployScript.WorkFolder(machine);
        var file = Path.Join(work.Path, "trust-settings.plist");
        var exported = await machine.Commands.RunAsync(new CommandLine("security", "trust-settings-export", file), cancellationToken).ConfigureAwait(false);
        if (!exported.Succeeded)
        {
            // None at all: nothing is trusted.
            return false;
        }

        var xml = await machine.Commands.RunAsync(new CommandLine("plutil", "-convert", "xml1", "-o", "-", file), cancellationToken).ConfigureAwait(false);
        try
        {
            return xml.Succeeded && TrustedForWebsites(xml.Output).Contains(sha1);
        }
        catch (Exception error) when (error is XmlException or FormatException or OverflowException)
        {
            context.Log.Write($"The trust settings macOS exported could not be read ({error.Message}): the CA is trusted again.");
            return false;
        }
    }

    // A plist dict's keys, each with the element after it.
    private static IEnumerable<(string Key, XElement Value)> Entries(XElement dict)
    {
        XElement? key = null;
        foreach (var element in dict.Elements())
        {
            if (key is null)
            {
                key = element.Name.LocalName == "key" ? element : null;
                continue;
            }

            yield return (key.Value, element);
            key = null;
        }
    }

    // A usage that covers SSL servers: one for every policy, or for Apple's SSL policy by its name or its OID.
    private static bool ForWebsites(Dictionary<string, XElement> usage)
    {
        var name = usage.GetValueOrDefault("kSecTrustSettingsPolicyName")?.Value;
        var policy = usage.GetValueOrDefault("kSecTrustSettingsPolicy")?.Value;
        return (name is null && policy is null)
            || name == "sslServer"
            || (policy is not null && string.Concat(policy.Where(c => !char.IsWhiteSpace(c))) == SslPolicy);
    }

    private static string Hex(string text) => string.Concat(text.Where(Uri.IsHexDigit)).ToUpperInvariant();

    private static string Short(string fingerprint) => fingerprint.Length > 11 ? $"{fingerprint[..11]}…" : fingerprint;

    private const string Sha256Label = "SHA-256 hash:";
    private const string Sha1Label = "SHA-1 hash:";

    // kSecTrustSettingsResult's values.
    private const int TrustRoot = 1;
    private const int TrustAsRoot = 2;
    private const int Deny = 3;

    // Apple's SSL policy, 1.2.840.113635.100.1.3, as the trust settings keep its OID (base64).
    private const string SslPolicy = "KoZIhvdjZAED";
}
