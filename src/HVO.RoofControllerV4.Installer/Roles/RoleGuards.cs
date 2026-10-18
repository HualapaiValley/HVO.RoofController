using System.Globalization;
using System.Text;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Roles;

/// <summary>A role as the wizard offers it: whether this machine can have it, and why not when it cannot.</summary>
public sealed record RoleOption(InstallRole Role, bool Available, string? Reason);

/// <summary>
/// Which roles a machine may have, and which choices the installer refuses. The real HAT is driven only from a Pi with
/// the HAT's devices; a test rig never goes where the controller drives the real HAT, and on a machine with the HAT's
/// I2C bus a person confirms it by typing the host name; the machine's roles run as root, the person's never do.
/// </summary>
public static class RoleGuards
{
    /// <summary>The platforms the installer runs on.</summary>
    public static IReadOnlyList<string> SupportedPlatforms { get; } = ["linux-arm64", "linux-x64", "osx-arm64"];

    /// <summary>Why the installer cannot run here at all, or null when it can.</summary>
    public static string? PlatformProblem(MachineSurvey survey)
        => SupportedPlatforms.Contains(survey.RuntimeIdentifier)
            ? null
            : $"This machine is {survey.RuntimeIdentifier}. The installer runs on {string.Join(", ", SupportedPlatforms)} (Intel Macs are not supported).";

    public static IReadOnlyList<RoleOption> Options(MachineSurvey survey) => InstallRoles.All.Select(role => Option(role, survey)).ToArray();

    public static RoleOption Option(InstallRole role, MachineSurvey survey)
    {
        var reason = PlatformProblem(survey) ?? role switch
        {
            InstallRole.Controller => ControllerProblem(survey),
            InstallRole.Rig => RigProblem(survey),
            InstallRole.Kiosk => KioskProblem(survey),
            InstallRole.Cli => null,
            InstallRole.MacApp => survey.RuntimeIdentifier == "osx-arm64" ? null : "The Mac app runs on a Mac with Apple silicon.",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };
        return new RoleOption(role, reason is null, reason);
    }

    /// <summary>
    /// True when <paramref name="roles"/> installs a test rig on a machine with the HAT's I2C bus that is not a rig
    /// already: a person then types the host name to confirm this is not the observatory's Pi.
    /// </summary>
    public static bool NeedsRigConfirmation(MachineSurvey survey, IEnumerable<InstallRole> roles)
        => roles.Contains(InstallRole.Rig) && survey.HasI2c && survey.SystemRecord?.Roles.Contains(InstallRole.Rig) != true;

    /// <summary>What the wizard asks for the confirmation.</summary>
    public static string RigConfirmationPrompt(MachineSurvey survey)
        => $"This machine has the HAT's I2C bus ({HatDevices.I2c}). A test rig must never be installed on the observatory's Pi. Type this machine's host name ({survey.HostName}) to confirm it is not.";

    // The same controller, trusted the same way: the keychain is this run's alone.
    private static bool SameController(ClientSettings recorded, ClientSettings client)
    {
        var (a, b) = (recorded.Normalised(), client.Normalised());
        return (a.Address is { } x && b.Address is { } y ? x == y : a.Controller == b.Controller)
            && a.CaSha256 == b.CaSha256 && a.CertificateSha256 == b.CertificateSha256;
    }

    /// <summary>What a person types to confirm plain HTTP.</summary>
    public const string HttpConfirmationWord = "http";

    /// <summary>What the wizard asks to confirm plain HTTP.</summary>
    public const string HttpConfirmationPrompt =
        "Over HTTP, API keys, session tokens and PINs cross the network unencrypted: anyone on it can read and use them. Type http to confirm this network is one you trust.";

    /// <summary>
    /// True when <paramref name="answers"/> serve the controller over plain HTTP and it does not already: a person then
    /// types <see cref="HttpConfirmationWord"/> to confirm.
    /// </summary>
    public static bool NeedsHttpConfirmation(MachineSurvey survey, InstallAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(answers);
        return InstallRoles.RunsController(answers.Roles)
            && answers.Controller?.Connection == ConnectionMode.Http
            && survey.SystemRecord?.Controller?.Connection != ConnectionMode.Http
            && survey.UserRecord?.Controller?.Connection != ConnectionMode.Http;
    }

    /// <summary>
    /// Every reason the installer refuses <paramref name="answers"/> on this machine; empty when it may go ahead. With
    /// <paramref name="planOnly"/>, for <c>--plan</c>, which changes nothing, it needs neither root for the machine's roles
    /// nor the confirmations a person types. Without <paramref name="clientChosen"/>, for the wizard's pages before its
    /// Client page, the client's controller is not checked yet: that page is where it is changed.
    /// </summary>
    public static IReadOnlyList<string> Check(MachineSurvey survey, InstallAnswers answers, bool planOnly = false, bool clientChosen = true)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(answers);
        var problems = new List<string>();
        if (PlatformProblem(survey) is { } platform)
        {
            problems.Add(platform);
            return problems;
        }

        var roles = InstallRoles.Ordered(answers.Roles);
        if (roles.Count == 0)
        {
            problems.Add("Choose at least one role.");
            return problems;
        }

        foreach (var role in roles)
        {
            if (Option(role, survey).Reason is { } reason)
            {
                problems.Add(reason);
            }
        }

        if (roles.Contains(InstallRole.Controller) && roles.Contains(InstallRole.Rig))
        {
            problems.Add("The controller and a test rig cannot share a machine: both run as the roof-controller container.");
        }

        if (roles.Contains(InstallRole.Kiosk) && !survey.IsPi && answers.Kiosk?.Rotation is > 0)
            problems.Add("Display rotation is Pi-only. Set kiosk.rotation to 0 on a VM or other non-Pi host.");

        var clients = roles.Where(role => role is InstallRole.Cli or InstallRole.MacApp).ToArray();
        if (roles.Contains(InstallRole.Rig) && clients.Length > 0 && survey.Os == InstallerOs.MacOS)
        {
            // Their trust is in the rig's CA, which is made in the same run, before anyone can compare its fingerprint.
            problems.Add($"{Capitalise(InstallRoles.Describe(clients))} {Are(clients)} set up against a running controller: install the rig first, then run the installer again for {(clients.Length == 1 ? "it" : "them")}.");
        }

        if (answers.Client is { TrustInKeychain: true } && survey.Os != InstallerOs.MacOS)
        {
            problems.Add("The controller's CA is trusted in the keychain on a Mac only: docs/install.md says how to add it to a browser on Linux.");
        }

        // The person's record keeps one controller for hvo-roof and the Mac app: a run for one of them would move the
        // other's (on its next run from the record) while its files still point at the one it has.
        if (clientChosen && clients.Length == 1 && answers.Client is { } client
            && survey.UserRecord is { Client: { } recorded } record
            && !SameController(recorded, client))
        {
            var other = clients[0] == InstallRole.Cli ? InstallRole.MacApp : InstallRole.Cli;
            if (record.Roles.Contains(other))
            {
                var how = recorded.Address is { } address && address == client.Address ? ", trusting it another way" : string.Empty;
                problems.Add($"{Capitalise(InstallRoles.Describe([clients[0]]))} and {InstallRoles.Describe([other])} connect to the same controller, "
                    + $"and {InstallRoles.Describe([other])} connects to {recorded.Controller}{how}: give the same controller and trust, or choose both to move them together.");
            }
        }

        var system = roles.Where(role => InstallRoles.ScopeOf(role, survey.Os) == InstallScope.System).ToArray();
        var user = roles.Where(role => InstallRoles.ScopeOf(role, survey.Os) == InstallScope.User).ToArray();
        if (system.Length > 0 && user.Length > 0)
        {
            problems.Add($"{Capitalise(InstallRoles.Describe(system))} {Are(system)} installed as root (with sudo), and {InstallRoles.Describe(user)} as you (without sudo). Install them in separate runs.");
        }
        else if (system.Length > 0 && !survey.IsRoot && !planOnly)
        {
            problems.Add($"{Capitalise(InstallRoles.Describe(system))} {Are(system)} installed as root: run the installer with sudo.");
        }
        else if (user.Length > 0 && survey.IsRoot)
        {
            problems.Add($"{Capitalise(InstallRoles.Describe(user))} {Are(user)} yours, and never installed as root: run the installer as yourself, without sudo.");
        }

        if (roles.Contains(InstallRole.Kiosk)
            && !InstallRoles.RunsController(roles)
            && survey.SystemRecord?.Roles.Any(role => role is InstallRole.Controller or InstallRole.Rig) != true)
        {
            problems.Add("The kiosk needs the controller on this Pi: choose the controller too, or install it first.");
        }
        else if (roles.Contains(InstallRole.Kiosk) && !InstallRoles.RunsController(roles) && !survey.Docker.IsUsable)
        {
            // The kiosk's key and PINs come from the controller's container.
            problems.Add($"The kiosk is set up through the controller, which runs in Docker. {survey.Docker.Problem}");
        }

        if (InstallRoles.RunsController(roles) && !survey.Docker.IsUsable)
        {
            problems.Add($"{Capitalise(InstallRoles.Describe(roles.Where(role => role is InstallRole.Controller or InstallRole.Rig)))} {Are(roles.Where(role => role is InstallRole.Controller or InstallRole.Rig).ToArray())} run in Docker. {survey.Docker.Problem}");
        }
        else if (InstallRoles.RunsController(roles))
        {
            // The limits the deploy gives: the ones given, then the desired-limits record's, the running controller's, the default.
            var resolved = ResolvedResources.Resolve((answers.Controller ?? new ControllerSettings()).Normalised().Resources, survey);
            problems.AddRange(resolved.Problems);
            problems.AddRange(ContainerLimitProblems(survey.Docker.Limits, resolved.Resources));
        }

        if (!planOnly && NeedsRigConfirmation(survey, roles) && !string.Equals(answers.RigConfirmation?.Trim(), survey.HostName, StringComparison.Ordinal))
        {
            problems.Add(string.IsNullOrWhiteSpace(answers.RigConfirmation)
                ? $"Confirm the test rig: this machine has the HAT's I2C bus. Type its host name ({survey.HostName}) to confirm it is not the observatory's Pi (rigConfirmation in an answers file)."
                : $"The confirmation does not match: type this machine's host name, {survey.HostName}.");
        }

        if (!planOnly && NeedsHttpConfirmation(survey, answers)
            && !string.Equals(answers.HttpConfirmation?.Trim(), HttpConfirmationWord, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(string.IsNullOrWhiteSpace(answers.HttpConfirmation)
                ? "Confirm plain HTTP: API keys, session tokens and PINs would cross the network unencrypted. Type http to confirm (httpConfirmation in an answers file), or choose private-ca."
                : "The confirmation does not match: type http to serve plain HTTP, or choose private-ca.");
        }

        problems.AddRange(answers.Problems());
        return problems;
    }

    /// <summary>
    /// How to let Docker enforce the controller's limits, as the deploy script says it: on a Raspberry Pi the firmware
    /// adds cgroup_disable=memory to the kernel's command line itself, so cgroup_enable=memory goes at the end of
    /// cmdline.txt's single line, where the later argument wins.
    /// </summary>
    public const string ContainerLimitRemedy =
        "To fix it on a Raspberry Pi: its firmware adds cgroup_disable=memory to the kernel's command line itself (it is not in the file). "
        + "Append \" cgroup_enable=memory\" to the end of the single line in /boot/firmware/cmdline.txt (on that line, not a new one; the later argument wins), "
        + "reboot the Pi with the roof idle, and check that docker info --format '{{.MemoryLimit}} {{.SwapLimit}} {{.CPUCfsQuota}} {{.PidsLimit}}' prints true four times. "
        + "On another machine, give the Docker daemon the memory, cpu and pids cgroup controllers. "
        + "There is no override (docs/deployment.md, \"The container's privileges and limits\").";

    /// <summary>
    /// Why Docker here cannot give the controller its limits (docs/deployment.md, "The container's privileges and
    /// limits"), which the deploy script refuses the same way before it changes anything (#149; as SkyMonitor's host
    /// prerequisites, #1285). Docker must say it enforces the memory limit with no swap beyond it (MemoryLimit and
    /// SwapLimit), the CPU quota (CPUCfsQuota) and the process limit (PidsLimit), since the controller always runs with
    /// all three: Docker would drop one it cannot enforce with only a warning. One it does not report counts as
    /// unsupported, and there is no override. The machine must also have the CPUs of the CPU limit. Every finding comes
    /// in one problem, with the cgroup version, the kernel's cgroup v2 controllers when Docker runs here, Docker's
    /// warnings about limits, and the remedy. A memory limit above the machine's memory is only a warning
    /// (<see cref="ContainerLimitWarnings"/>).
    /// A <paramref name="rollback"/> is a recovery path, so a limit Docker cannot enforce does not refuse it (the owner's
    /// decision on #149): it is <see cref="ContainerLimitRollbackWarnings"/>' warning instead. The CPUs still count then,
    /// for a CPU limit Docker enforces.
    /// </summary>
    public static IEnumerable<string> ContainerLimitProblems(DockerLimits? docker, ContainerResources resources, bool rollback = false)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (docker is null)
        {
            yield return "Docker did not say which limits it enforces (docker info), and the controller runs with a CPU, a memory and a process limit: check that docker info works, then run the installer again.";
            yield break;
        }

        var capabilities = UnenforcedLimits(docker, resources, "so Docker would run the controller without");
        List<string> findings = rollback ? [] : [.. capabilities];
        if (!rollback || docker.Cpu == true)
        {
            if (docker.Cpus is not { } cpus)
            {
                findings.Add($"docker-host-cpus-unreported: docker info does not report the CPUs (NCPU), so the CPU limit of {resources.CpusText} cannot be checked against them.");
            }
            else if (resources.CpusOrDefault > cpus)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"docker-host-cpus-insufficient: docker info reports {cpus} CPUs, fewer than the CPU limit of {resources.CpusText}: ")
                    + (cpus >= ContainerResources.MinimumCpus
                        ? string.Create(CultureInfo.InvariantCulture, $"give controller.resources.cpus (or --cpus) from {ContainerResources.MinimumCpus} to {cpus}.")
                        : "the CPU limit is at least 1."));
            }
        }

        if (findings.Count == 0)
        {
            yield break;
        }

        var problem = new StringBuilder($"Docker here cannot give the controller its limits ({resources.Describe()}), so the {(rollback ? "rollback" : "install")} is refused. Every finding:");
        foreach (var finding in findings)
        {
            problem.Append(Environment.NewLine).Append("  - ").Append(finding);
        }

        if (!rollback && capabilities.Count > 0)
        {
            problem.Append(Environment.NewLine).Append("  Docker's host: ").Append(HostDetails(docker)).Append('.');
            problem.Append(Environment.NewLine).Append("  ").Append(ContainerLimitRemedy).Append(" Then run the installer again.");
        }

        yield return problem.ToString();
    }

    /// <summary>
    /// The loud warning a rollback gives on a machine whose Docker cannot enforce some of the controller's limits, as the
    /// deploy script's <c>--rollback</c> gives it too, in its output and in the controller's log: the rollback goes ahead
    /// with the ones Docker can enforce, and the version it brings back runs without the others (the owner's decision on
    /// #149). Each limit it cannot enforce is named, with the host's details and the remedy. Empty when Docker enforces
    /// them all (or did not answer: <see cref="ContainerLimitProblems"/> refuses that).
    /// </summary>
    public static IEnumerable<string> ContainerLimitRollbackWarnings(DockerLimits? docker, ContainerResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (docker is null || UnenforcedLimits(docker, resources, "so the version the rollback brings back runs without") is not { Count: > 0 } unenforced)
        {
            yield break;
        }

        var warning = new StringBuilder("THE ROLLBACK GOES AHEAD, BUT DOCKER HERE CANNOT ENFORCE THESE LIMITS, so the version it brings back runs WITHOUT them. ")
            .Append("A rollback is a recovery path, so it is not refused (#149); an install, an upgrade or a deploy is refused here until this is fixed:");
        foreach (var limit in unenforced)
        {
            warning.Append(Environment.NewLine).Append("  - ").Append(limit);
        }

        warning.Append(Environment.NewLine).Append("  Docker's host: ").Append(HostDetails(docker)).Append('.');
        warning.Append(Environment.NewLine).Append("  ").Append(ContainerLimitRemedy).Append(" Then upgrade again.");
        yield return warning.ToString();
    }

    // Each limit Docker does not say it enforces, as a finding with its code. Memory and swap are separate controls
    // (review ROOF-151-R1 F3): without swap control, the memory limit still holds and only the swap beyond it does not.
    private static List<string> UnenforcedLimits(DockerLimits docker, ContainerResources resources, string without)
    {
        static string Flag(bool? flag) => flag switch { true => "true", false => "false", null => "not reported" };
        var limits = new List<string>();
        if (docker.Memory != true)
        {
            limits.Add($"docker-memory-limit-unsupported: docker info reports MemoryLimit {Flag(docker.Memory)} and SwapLimit {Flag(docker.Swap)}, {without} its memory limit ({resources.MemoryOrDefault}, and no swap beyond it).");
        }
        else if (docker.Swap != true)
        {
            limits.Add(without.Contains("rollback", StringComparison.Ordinal)
                ? $"docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit {Flag(docker.Swap)}, so the version the rollback brings back keeps its memory limit ({resources.MemoryOrDefault}) but may swap beyond it."
                : $"docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit {Flag(docker.Swap)}, so Docker would let the controller swap beyond its memory limit ({resources.MemoryOrDefault}).");
        }

        if (docker.Cpu != true)
        {
            limits.Add($"docker-cpu-quota-unsupported: docker info reports CPUCfsQuota {Flag(docker.Cpu)}, {without} its CPU limit ({resources.CpusText}).");
        }

        if (docker.Pids != true)
        {
            limits.Add(string.Create(CultureInfo.InvariantCulture, $"docker-pids-limit-unsupported: docker info reports PidsLimit {Flag(docker.Pids)}, {without} its process limit ({resources.PidsLimit})."));
        }

        return limits;
    }

    // The cgroup version, the kernel's cgroup v2 controllers when Docker runs here, and Docker's warnings about limits.
    private static string HostDetails(DockerLimits docker)
    {
        var host = new StringBuilder(docker.CgroupVersion is { } version ? $"cgroup v{version}" : "cgroup version not reported");
        host.Append(docker.CgroupControllers is { } controllers
            ? $"; {MachineSurveyor.CgroupControllersFile}: \"{controllers}\""
            : $"; {MachineSurveyor.CgroupControllersFile} on Docker's machine lists the kernel's cgroup v2 controllers (Docker needs memory, cpu and pids)");
        foreach (var warning in docker.Warnings.Where(warning =>
                     warning.Contains("limit", StringComparison.OrdinalIgnoreCase)
                     || warning.Contains("cfs", StringComparison.OrdinalIgnoreCase)
                     || warning.Contains("cgroup", StringComparison.OrdinalIgnoreCase)))
        {
            host.Append($"; Docker warns \"{warning}\"");
        }

        return host.ToString();
    }

    /// <summary>
    /// What refuses deploying the controller with <paramref name="answers"/>' limits here, checked before anything
    /// changes: limits below the floor, Docker that is not usable, desired-limits records that cannot be read or are not
    /// valid when a limit is needed from them (<see cref="ResolvedResources"/>), and a Docker that cannot enforce the
    /// limits or has fewer CPUs (<see cref="ContainerLimitProblems"/>). Empty when the answers run no controller. For the paths that change
    /// something before an install's own guards run (review ROOF-151-R1 F4): an upgrade's hand-over to another release's
    /// installer (its backup, and the installer it puts in place), and a restore (the files it puts back).
    /// </summary>
    public static IEnumerable<string> ControllerLimitProblems(MachineSurvey survey, InstallAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(answers);
        if (!InstallRoles.RunsController(answers.Roles))
        {
            return [];
        }

        if (!survey.Docker.IsUsable)
        {
            return [$"The controller runs in Docker. {survey.Docker.Problem}"];
        }

        var given = (answers.Controller ?? new ControllerSettings()).Normalised().Resources;
        var resolved = ResolvedResources.Resolve(given, survey);
        return [.. given.Problems(), .. resolved.Problems, .. ContainerLimitProblems(survey.Docker.Limits, resolved.Resources)];
    }

    /// <summary>
    /// What the installer warns of in the desired-limits records here (<see cref="DesiredLimits"/>), against the limits
    /// <paramref name="given"/> (the record's, the answers', --cpus and --memory), as the deploy script warns: a limit given
    /// that differs from the record's (the one given comes first, and the deploy records it), a record written for another
    /// container than the running controller (it still comes first for a limit not given), and a record that is not valid
    /// when every limit is given (it is not applied). <see cref="ResolvedResources.Warnings"/>.
    /// </summary>
    public static IReadOnlyList<string> DesiredLimitWarnings(MachineSurvey survey, ContainerResources given)
        => ResolvedResources.Resolve(given, survey).Warnings;

    /// <summary>
    /// What the installer warns of in the controller's limits: a memory limit above the memory Docker reports here. It is
    /// kept, as a limit is a cap, not a reservation; the deploy script warns the same.
    /// </summary>
    public static IEnumerable<string> ContainerLimitWarnings(DockerLimits? docker, ContainerResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.MemoryBytes is { } limit && docker?.MemoryBytes is { } memory && limit > memory)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"The controller's memory limit of {resources.MemoryOrDefault}{(resources.Memory is null ? " (the default)" : string.Empty)} is more than the {memory / (1024 * 1024)}m Docker reports here. It is kept: a memory limit is a cap, not a reservation, so the machine runs out of memory before the container reaches it.");
        }
    }

    private static string? ControllerProblem(MachineSurvey survey)
    {
        if (survey.RuntimeIdentifier != "linux-arm64" || !survey.IsPi)
        {
            return "The controller drives the real HAT, only from the observatory's Raspberry Pi (64-bit Raspberry Pi OS). Choose a test rig to run it against the HAT emulator.";
        }

        var missing = survey.MissingHatDevices().ToArray();
        return missing.Length == 0
            ? null
            : $"The HAT's devices are missing ({string.Join(", ", missing)}). On the observatory's Pi, enable I2C (sudo raspi-config nonint do_i2c 0) and reboot; elsewhere, choose a test rig.";
    }

    private static string? KioskProblem(MachineSurvey survey)
    {
        if (survey.RuntimeIdentifier != "linux-arm64")
        {
            return "The kiosk runs on the controller's Raspberry Pi (64-bit Raspberry Pi OS).";
        }

        if (survey.Display is { DisplayManagerActive: true } or { DisplayManagerAtBoot: true })
        {
            var desktop = survey.Display.DisplayManagerActive
                ? "A desktop has the screen (display-manager.service is running)"
                : $"A desktop takes the screen at boot (display-manager.service is enabled, and the Pi boots to {MachineSurveyor.GraphicalTarget})";
            return $"{desktop}, and the kiosk draws on it itself: use Raspberry Pi OS Lite, or boot to the console (sudo systemctl set-default multi-user.target), reboot, and run the installer again.";
        }

        // Once installed, a screen that is off or unplugged for now does not stop an update.
        if (survey.SystemRecord?.Roles.Contains(InstallRole.Kiosk) == true || survey.Display?.Screens.Count > 0)
        {
            return null;
        }

        return survey.Display is { Outputs.Count: > 0 } display
            ? $"The kiosk needs a screen, and none is connected ({string.Join(", ", display.Outputs.Select(output => $"{output.Name}: {output.Status}"))}). Connect the touchscreen, check its ribbon cable, reboot, and run the installer again."
            : $"The kiosk needs a screen, and this Pi has no display outputs ({MachineSurveyor.DrmFolder} lists none): connect the touchscreen, reboot, and run the installer again.";
    }

    private static string? RigProblem(MachineSurvey survey)
    {
        if (survey.SystemRecord?.DrivesRealHat == true)
        {
            return $"This machine's install record ({InstallPaths.SystemRecord}) says it drives the real HAT: a test rig is never installed here.";
        }

        if (survey.SystemRecord is null && survey.SystemRecordProblem is { } problem)
        {
            // It may say the machine drives the real HAT: never assume it does not.
            return $"{problem} A test rig is not installed until it is fixed or removed, since it may say this machine drives the real HAT.";
        }

        if (survey.Controller is { HatEmulator: null } controller && survey.SystemRecord?.Roles.Contains(InstallRole.Rig) != true)
        {
            return $"The {controller.Name} container on this machine drives the real HAT: a test rig is never installed over it.";
        }

        return null;
    }

    private static string Are(IReadOnlyCollection<InstallRole> roles) => roles.Count == 1 ? "is" : "are";

    private static string Capitalise(string text) => InstallRoles.Capitalise(text);
}
