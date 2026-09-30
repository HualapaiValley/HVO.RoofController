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
            InstallRole.Kiosk => survey.RuntimeIdentifier == "linux-arm64" ? null : "The kiosk runs on the controller's Raspberry Pi (64-bit Raspberry Pi OS).",
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

    /// <summary>
    /// Every reason the installer refuses <paramref name="answers"/> on this machine; empty when it may go ahead. With
    /// <paramref name="planOnly"/>, for <c>--plan</c>, which changes nothing, it neither needs root for the machine's roles
    /// nor the rig's confirmation.
    /// </summary>
    public static IReadOnlyList<string> Check(MachineSurvey survey, InstallAnswers answers, bool planOnly = false)
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

        if (InstallRoles.RunsController(roles) && !survey.Docker.IsUsable)
        {
            problems.Add($"{Capitalise(InstallRoles.Describe(roles.Where(role => role is InstallRole.Controller or InstallRole.Rig)))} {Are(roles.Where(role => role is InstallRole.Controller or InstallRole.Rig).ToArray())} run in Docker. {survey.Docker.Problem}");
        }

        if (!planOnly && NeedsRigConfirmation(survey, roles) && !string.Equals(answers.RigConfirmation?.Trim(), survey.HostName, StringComparison.Ordinal))
        {
            problems.Add(string.IsNullOrWhiteSpace(answers.RigConfirmation)
                ? $"Confirm the test rig: this machine has the HAT's I2C bus. Type its host name ({survey.HostName}) to confirm it is not the observatory's Pi (rigConfirmation in an answers file)."
                : $"The confirmation does not match: type this machine's host name, {survey.HostName}.");
        }

        problems.AddRange(answers.Problems());
        return problems;
    }

    private static string? ControllerProblem(MachineSurvey survey)
    {
        if (survey.RuntimeIdentifier != "linux-arm64")
        {
            return "The controller drives the real HAT, only from the observatory's Raspberry Pi (64-bit Raspberry Pi OS). Choose a test rig to run it against the HAT emulator.";
        }

        var missing = survey.MissingHatDevices().ToArray();
        return missing.Length == 0
            ? null
            : $"The HAT's devices are missing ({string.Join(", ", missing)}). On the observatory's Pi, enable I2C (sudo raspi-config nonint do_i2c 0) and reboot; elsewhere, choose a test rig.";
    }

    private static string? RigProblem(MachineSurvey survey)
    {
        if (survey.SystemRecord?.DrivesRealHat == true)
        {
            return $"This machine's install record ({InstallPaths.SystemRecord}) says it drives the real HAT: a test rig is never installed here.";
        }

        if (survey.Controller is { HatEmulator: null } controller && survey.SystemRecord?.Roles.Contains(InstallRole.Rig) != true)
        {
            return $"The {controller.Name} container on this machine drives the real HAT: a test rig is never installed over it.";
        }

        return null;
    }

    private static string Are(IReadOnlyCollection<InstallRole> roles) => roles.Count == 1 ? "is" : "are";

    // hvo-roof is a command's name, and keeps its case at the start of a sentence.
    private static string Capitalise(string text)
        => text.Length == 0 || text.StartsWith("hvo-roof", StringComparison.Ordinal) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
