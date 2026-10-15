using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer;

/// <summary>Explicit setup precedes application changes; other prerequisites have manual instructions.</summary>
public static class Prerequisites
{
    public static IReadOnlyList<string> Guidance(InstallerSession session)
    {
        var lines = new List<string>();
        if (!session.Survey.Docker.IsUsable && session.Machine.Os == Machine.InstallerOs.MacOS)
            lines.Add("Docker: install and start Docker Desktop for Apple silicon from https://docs.docker.com/desktop/setup/install/mac-install/. Verify docker info and docker compose version, then rerun.");
        else if (!session.Survey.Docker.IsUsable)
            lines.Add("Docker: install Docker Engine and the Compose plugin using https://docs.docker.com/engine/install/ for this OS, then explicitly start Docker with sudo systemctl enable --now docker. Verify docker info and docker compose version as the installing user. This installer never grants Docker group access.");
        lines.Add("Physical controller: only on the intended Raspberry Pi, enable I2C with sudo raspi-config nonint do_i2c 0 if needed, reboot, then rerun the installer. Missing GPIO or thermal devices are not fixed by enabling I2C. Elsewhere choose the emulated rig.");
        lines.Add("Kiosk: a connected DRM display and console boot are required. If a desktop owns it, explicitly select sudo systemctl set-default multi-user.target and reboot before retrying. No display-manager change or reboot is automatic.");
        lines.Add("Splash: optional on Raspberry Pi OS or Ubuntu with GRUB. Requires Python 3; install it explicitly with sudo apt-get update && sudo apt-get install python3. Enabling splash separately approves its official distribution Plymouth/Pillow packages and initramfs changes. Ubuntu virtual machines use zero rotation and emulated hardware.");
        lines.Add("CLI: install per-user without sudo. Add its chosen bin folder to PATH yourself or use the full path. No shell profile is edited.");
        return lines;
    }

    /// <summary>Sets up only explicitly approved kiosk libraries, then checks readiness before application changes.</summary>
    public static async Task PrepareAsync(InstallerSession session, CancellationToken cancellationToken = default)
    {
        await session.RefreshSurveyAsync(cancellationToken).ConfigureAwait(false);
        var problems = session.Problems();
        if (problems.Count > 0) throw new InstallerRefusedException(string.Join(Environment.NewLine, problems));
        if (session.Answers.Roles.Any(role => InstallRoles.ScopeOf(role, session.Machine.Os) == InstallScope.System)
            && session.Machine.FileExists("/var/run/reboot-required"))
            throw new InstallerRefusedException("The OS reports a required reboot. Reboot separately, then rerun the installer; application installation has not started.");
        if (!session.Answers.Roles.Contains(InstallRole.Kiosk)) return;
        var packages = new KioskPackagesStep();
        var check = await packages.CheckAsync(session.Context, cancellationToken).ConfigureAwait(false);
        if (check.Change == StepChange.Blocked) throw new InstallerRefusedException(check.Detail!);
        if (check.MakesChange)
        {
            await packages.ApplyAsync(session.Context, check, cancellationToken).ConfigureAwait(false);
            await session.RefreshSurveyAsync(cancellationToken).ConfigureAwait(false);
            if (session.Machine.FileExists("/var/run/reboot-required"))
                throw new InstallerRefusedException("Prerequisite setup requires a reboot. Reboot separately and retry; application installation has not started.");
            problems = session.Problems();
            if (problems.Count > 0) throw new InstallerRefusedException(string.Join(Environment.NewLine, problems));
        }
    }
}
