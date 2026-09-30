using HVO.RoofControllerV4.Installer.Answers;
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
}

/// <summary>Turns the answers into the steps that install them on this machine.</summary>
public static class PlanBuilder
{
    public static InstallPlan Build(InstallerMachine machine, MachineSurvey survey, InstallAnswers answers)
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
            AddFolder(steps, layout.Configuration, Modes.Folder, "the controller's configuration");
            AddFolder(steps, layout.Secrets, Modes.PrivateFolder, "secrets the controller reads, one file per setting");
            AddFolder(steps, layout.Https, Modes.PrivateFolder, "the controller's HTTPS certificate");
            AddFolder(steps, layout.Settings, Modes.Folder, "settings files the controller reads");
            AddFolder(steps, layout.Data, Modes.Folder, "the controller's data");
            AddFolder(steps, layout.Identity, Modes.PrivateFolder, "people, sessions and API keys");
            AddFolder(steps, layout.SettingsSecrets, Modes.PrivateFolder, "secrets set through the API");
            if (rig)
            {
                steps.Add(new ContainerStep(MachineSurveyor.HatEmulatorContainer, null, "the HAT emulator: the roof, drive and limit switches the rig drives"));
            }

            steps.Add(new ContainerStep(
                MachineSurveyor.ControllerContainer,
                rig ? HatMode.Emulated : HatMode.Real,
                rig ? "the controller, against the HAT emulator" : "the controller, driving the real HAT",
                settings));
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
            Controller = InstallRoles.RunsController(roles) ? answers.Controller : InstallRoles.RunsController(all) ? existing?.Controller : null,
            Cli = roles.Contains(InstallRole.Cli) ? answers.Cli : all.Contains(InstallRole.Cli) ? existing?.Cli : null,
            MacApp = roles.Contains(InstallRole.MacApp) ? answers.MacApp : all.Contains(InstallRole.MacApp) ? existing?.MacApp : null
        };
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
