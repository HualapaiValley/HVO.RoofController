using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// Where the controller keeps its files. On Linux (the controller, and a rig) these are the deploy script's own
/// defaults; a rig on a Mac keeps them in the person's Application Support folder, which Docker Desktop shares.
/// </summary>
public sealed record ControllerLayout(
    string Configuration,
    string Secrets,
    string Https,
    string Settings,
    string Data,
    string Identity,
    string SettingsSecrets)
{
    public static ControllerLayout System { get; } = new(
        "/etc/hvo-roof",
        "/etc/hvo-roof/secrets",
        "/etc/hvo-roof/https",
        "/etc/hvo-roof/config",
        "/var/lib/hvo-roof",
        "/var/lib/hvo-roof/identity",
        "/var/lib/hvo-roof/settings-secrets");

    /// <summary>A rig on a Mac: everything under ~/Library/Application Support/HVO Roof Rig.</summary>
    public static ControllerLayout MacRig(string home)
    {
        var root = Path.Join(home, "Library", "Application Support", "HVO Roof Rig");
        return new(root, Path.Join(root, "secrets"), Path.Join(root, "https"), Path.Join(root, "config"), root, Path.Join(root, "identity"), Path.Join(root, "settings-secrets"));
    }

    public static ControllerLayout For(InstallerMachine machine)
        => machine.Os == InstallerOs.MacOS ? MacRig(machine.Home) : System;

    /// <summary>The installer's certificate authority's folder: its key, which only root reads.</summary>
    public string Ca => Path.Join(Configuration, "ca");

    /// <summary>The CA's private key (PKCS#8, PEM). It never leaves this machine.</summary>
    public string CaKey => Path.Join(Ca, "ca.key");

    /// <summary>The CA's certificate (PEM): what each client trusts.</summary>
    public string CaCertificate => Path.Join(Configuration, "ca.crt");

    /// <summary>The controller's certificate and key (PKCS#12), as the deploy script mounts it (HTTPS_CERT_FILE).</summary>
    public string Pfx => Path.Join(Https, "roof-controller.pfx");

    /// <summary>The controller's settings file: the settings changed from the shipped defaults, through the API or by hand.</summary>
    public string SettingsFile => Path.Join(Settings, "appsettings.Local.json");

    /// <summary>The controller's identity store: its people and managed API keys, as hashes.</summary>
    public string IdentityFile => Path.Join(Identity, "identity.json");

    /// <summary>The PKCS#12 file's password, a secret the controller reads.</summary>
    public string PfxPassword => Path.Join(Secrets, "Kestrel__Certificates__Default__Password");
}

/// <summary>Turns the answers into the steps that install them on this machine.</summary>
public static class PlanBuilder
{
    /// <summary>
    /// The steps that install <paramref name="answers"/>. <paramref name="replacingAuthority"/> is the fingerprint of a
    /// certificate authority a person asked to replace (<c>--new-ca</c>).
    /// </summary>
    public static InstallPlan Build(InstallerMachine machine, MachineSurvey survey, InstallAnswers answers, string? replacingAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(answers);
        answers = answers.Normalised();
        var roles = answers.Roles;
        var steps = new List<PlanStep>();

        if (InstallRoles.RunsController(roles))
        {
            var rig = roles.Contains(InstallRole.Rig);
            var layout = ControllerLayout.For(machine);
            var settings = answers.Controller!;
            var names = CertificateNames.For(machine, settings);
            AddFolder(steps, layout.Configuration, Modes.Folder, "the controller's configuration");
            AddFolder(steps, layout.Secrets, Modes.PrivateFolder, "secrets the controller reads, one file per setting");
            AddFolder(steps, layout.Https, Modes.PrivateFolder, "the controller's HTTPS certificate");
            if (settings.Connection == ConnectionMode.PrivateCa)
            {
                AddFolder(steps, layout.Ca, Modes.PrivateFolder, "the certificate authority's key");
            }

            AddFolder(steps, layout.Settings, Modes.Folder, "settings files the controller reads");
            AddFolder(steps, layout.Data, Modes.Folder, "the controller's data");
            AddFolder(steps, layout.Identity, Modes.PrivateFolder, "people, sessions and API keys");
            AddFolder(steps, layout.SettingsSecrets, Modes.PrivateFolder, "secrets set through the API");
            var certificate = AddCertificateSteps(steps, layout, names, settings.Connection, replacingAuthority);
            var keys = AllocateKeys(machine, survey, layout, roles);
            steps.AddRange(keys.Select(key => new ApiKeyStep(layout, key)));
            var camera = CameraSteps.For(layout, rig, settings.Camera);
            steps.AddRange(camera);
            var import = settings.ImportSettingsFrom is { } backup ? new SettingsImportStep(layout, backup) : null;
            if (import is not null)
            {
                steps.Add(import);
            }

            steps.Add(new DeployToolsStep());
            var emulator = rig ? new HatEmulatorStep(settings.Rig ?? new RigSettings()) : null;
            if (emulator is not null)
            {
                steps.Add(emulator);
            }

            steps.Add(new ControllerStep(layout, settings, names, rig, certificate, keys, emulator, camera, import));
            if (settings.FirstAdmin is { } admin)
            {
                steps.Add(new FirstAdminStep(layout, admin, keys.First(key => key.Use == ApiKeyUse.Admin)));
            }

            steps.Add(new PortStep(settings.ApiPort, settings.UsesHttps ? "the controller's API (HTTPS)" : "the controller's API (HTTP)", MachineSurveyor.ControllerContainer));
            steps.Add(new PortStep(settings.WebPort, "the web UI", MachineSurveyor.ControllerContainer));
        }

        if (roles.Contains(InstallRole.Kiosk))
        {
            steps.Add(new LaterStep(StepKind.Folder, "/opt/hvo-roof-kiosk", "the kiosk's program", context => context.Machine.DirectoryExists("/opt/hvo-roof-kiosk")));
            steps.Add(new LaterStep(StepKind.Folder, "/etc/hvo-roof-kiosk", "the kiosk's settings and device key", context => context.Machine.DirectoryExists("/etc/hvo-roof-kiosk")));
            steps.Add(new LaterStep(StepKind.Service, MachineSurveyor.KioskUnit, "starts the kiosk on the touchscreen", context => context.Survey.Kiosk is not null));
        }

        if (roles.Contains(InstallRole.Cli))
        {
            var path = Path.Join(InstallPaths.Expand(machine, answers.Cli!.Folder), "hvo-roof");
            steps.Add(new LaterStep(StepKind.File, path, "hvo-roof, the command-line client", context => context.Machine.FileExists(path)));
        }

        if (roles.Contains(InstallRole.MacApp))
        {
            var path = Path.Join(InstallPaths.Expand(machine, answers.MacApp!.Folder), MachineSurveyor.MacAppBundle);
            steps.Add(new LaterStep(StepKind.File, path, "the Mac app", context => context.Machine.DirectoryExists(path)));
        }

        // The record comes last: it says the roles are installed once they are.
        foreach (var scope in roles.Select(role => InstallRoles.ScopeOf(role, machine.Os)).Distinct().Order())
        {
            var scopeRoles = roles.Where(role => InstallRoles.ScopeOf(role, machine.Os) == scope).ToArray();
            steps.Add(new RecordStep(scope, InstallPaths.RecordFor(scope, machine), (context, existing) => BuildRecord(context, scope, scopeRoles, answers, existing)));
        }

        return new InstallPlan(steps);
    }

    /// <summary>
    /// The controller's certificate alone, as <c>hvo-roof-install cert</c> makes it: its folders, the CA, the file's
    /// password and the certificate, for the recorded <paramref name="settings"/>. <paramref name="renew"/> issues it
    /// again even when it is still good.
    /// </summary>
    public static InstallPlan BuildCertificate(InstallerMachine machine, ControllerSettings settings, string? replacingAuthority = null, bool renew = false)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(settings);
        var layout = ControllerLayout.For(machine);
        var steps = CertificateFolders(layout);
        AddCertificateSteps(steps, layout, CertificateNames.For(machine, settings.Normalised()), settings.Connection, replacingAuthority, renew);
        return new InstallPlan(steps);
    }

    /// <summary>
    /// A person's own certificate put in place, as <c>hvo-roof-install cert import</c> does it: its folders, the file's
    /// password, the certificate, and the record (when there is one) saying the controller serves the person's own.
    /// </summary>
    public static InstallPlan BuildImport(InstallerMachine machine, ImportedCertificate imported, string source, (InstallScope Scope, string Path)? record)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(source);
        var layout = ControllerLayout.For(machine);
        var steps = CertificateFolders(layout);
        steps.Add(new CertificatePasswordStep(layout));
        steps.Add(new ImportCertificateStep(layout, imported, source));
        if (record is var (scope, path))
        {
            steps.Add(new RecordStep(scope, path, (_, existing) => existing is { Controller: { } controller }
                ? existing with { Controller = controller with { Connection = ConnectionMode.OwnCertificate } }
                : existing!));
        }

        return new InstallPlan(steps);
    }

    /// <summary>
    /// The controller deployed again with the recorded <paramref name="settings"/> and <paramref name="roles"/>, to serve
    /// the certificate <paramref name="certificate"/> puts in place, as <c>hvo-roof-install cert</c> does once the roof is
    /// idle: the API keys, the programs the deploy script runs, the HAT emulator for a rig, and the controller.
    /// </summary>
    public static InstallPlan BuildRedeploy(InstallerMachine machine, MachineSurvey survey, IReadOnlyCollection<InstallRole> roles, ControllerSettings settings, IControllerCertificateStep certificate)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(certificate);
        var layout = ControllerLayout.For(machine);
        settings = settings.Normalised();
        var rig = roles.Contains(InstallRole.Rig);
        var keys = AllocateKeys(machine, survey, layout, roles);
        var steps = new List<PlanStep>(keys.Select(key => new ApiKeyStep(layout, key))) { new DeployToolsStep() };

        // The camera's files as the record has them: a change to one, or one written by an install that stopped before it
        // redeployed the controller, is more than the certificate.
        var camera = CameraSteps.For(layout, rig, settings.Camera);
        steps.AddRange(camera);
        var emulator = rig ? new HatEmulatorStep(settings.Rig ?? new RigSettings()) : null;
        if (emulator is not null)
        {
            steps.Add(emulator);
        }

        steps.Add(new ControllerStep(layout, settings, CertificateNames.For(machine, settings), rig, certificate, keys, emulator, camera));
        return new InstallPlan(steps);
    }

    private static List<PlanStep> CertificateFolders(ControllerLayout layout)
    {
        var steps = new List<PlanStep>();
        AddFolder(steps, layout.Configuration, Modes.Folder, "the controller's configuration");
        AddFolder(steps, layout.Secrets, Modes.PrivateFolder, "secrets the controller reads, one file per setting");
        AddFolder(steps, layout.Https, Modes.PrivateFolder, "the controller's HTTPS certificate");
        return steps;
    }

    /// <summary>
    /// The record after this install: the roles already recorded and these (the controller replacing a rig, and a rig the
    /// controller), with this install's choices for its roles and the recorded ones for the rest.
    /// </summary>
    public static InstallRecord BuildRecord(InstallContext context, InstallScope scope, IReadOnlyCollection<InstallRole> roles, InstallAnswers answers, InstallRecord? existing)
    {
        var kept = (existing?.Roles ?? []).Where(role =>
            !(role == InstallRole.Rig && roles.Contains(InstallRole.Controller))
            && !(role == InstallRole.Controller && roles.Contains(InstallRole.Rig)));
        var all = InstallRoles.Ordered(kept.Concat(roles));
        return new InstallRecord
        {
            Scope = scope,
            Roles = all,
            Hat = all.Contains(InstallRole.Controller) ? HatMode.Real : all.Contains(InstallRole.Rig) ? HatMode.Emulated : null,
            Version = context.Version,
            InstallerVersion = context.Version,
            InstalledAt = existing?.InstalledAt ?? default,
            UpdatedAt = existing?.UpdatedAt ?? default,
            // An import is done once: the record keeps the settings, not where they came from.
            Controller = InstallRoles.RunsController(roles) ? answers.Controller! with { ImportSettingsFrom = null } : InstallRoles.RunsController(all) ? existing?.Controller : null,
            Cli = roles.Contains(InstallRole.Cli) ? answers.Cli : all.Contains(InstallRole.Cli) ? existing?.Cli : null,
            MacApp = roles.Contains(InstallRole.MacApp) ? answers.MacApp : all.Contains(InstallRole.MacApp) ? existing?.MacApp : null
        };
    }

    // The CA (with a private CA), the file's password (unless the person gives their own certificate) and the certificate.
    private static CertificateStep? AddCertificateSteps(List<PlanStep> steps, ControllerLayout layout, CertificateNames names, ConnectionMode connection, string? replacingAuthority, bool renew = false)
    {
        if (connection == ConnectionMode.Http)
        {
            return null;
        }

        if (connection == ConnectionMode.PrivateCa)
        {
            AddFolder(steps, layout.Ca, Modes.PrivateFolder, "the certificate authority's key");
            steps.Add(new CertificateAuthorityStep(layout, names, replacingAuthority));
        }

        if (connection != ConnectionMode.OwnCertificate)
        {
            steps.Add(new CertificatePasswordStep(layout));
        }

        var certificate = new CertificateStep(layout, names, connection, replacingAuthority, renew);
        steps.Add(certificate);
        return certificate;
    }

    /// <summary>
    /// The API keys the controller needs from the installer: the operator key (the deploy script's verified Stop), the
    /// admin key (the first admin), the web UI's Stop key, and the kiosk's when the kiosk runs here too. Keys already in
    /// the secrets folder are reused. Without root, the plan says what each is for, not which entry it takes.
    /// </summary>
    public static IReadOnlyList<ApiKeyAllocation> AllocateKeys(InstallerMachine machine, MachineSurvey survey, ControllerLayout layout, IReadOnlyCollection<InstallRole> roles)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(roles);
        ApiKeyUse[] uses = roles.Contains(InstallRole.Kiosk)
            ? [ApiKeyUse.Operator, ApiKeyUse.Admin, ApiKeyUse.WebStop, ApiKeyUse.Kiosk]
            : [ApiKeyUse.Operator, ApiKeyUse.Admin, ApiKeyUse.WebStop];
        try
        {
            var existing = ApiKeyFiles.Read(machine, layout.Secrets);
            return ApiKeyFiles.Allocate(existing, uses, ApiKeyFiles.IndexOfContainerKeyFile(survey.Controller?.WebStopKeyFile), ManagedKeyNames(machine, layout));
        }
        catch (UnauthorizedAccessException)
        {
            return [.. ApiKeyFiles.Allocate([], uses, null).Select(key => key with { Index = -1 })];
        }
    }

    private static IEnumerable<string> ManagedKeyNames(InstallerMachine machine, ControllerLayout layout)
    {
        try
        {
            return ControllerIdentity.Read(machine, layout.IdentityFile).ApiKeys.Select(key => key.Name);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InstallerException)
        {
            return [];
        }
    }

    // A rig on a Mac keeps its data in its configuration folder: each folder once.
    private static void AddFolder(List<PlanStep> steps, string path, UnixFileMode mode, string purpose)
    {
        if (!steps.OfType<FolderStep>().Any(step => step.Target == path))
        {
            steps.Add(new FolderStep(path, mode, purpose));
        }
    }
}
