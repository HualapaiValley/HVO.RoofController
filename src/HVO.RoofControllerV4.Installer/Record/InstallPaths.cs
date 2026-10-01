using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.Installer.Record;

/// <summary>Where the installer keeps its record and its log, on the machine and for the person.</summary>
public static class InstallPaths
{
    /// <summary>The record of the machine's roles: the controller, a rig on Linux, the kiosk.</summary>
    public const string SystemRecord = "/etc/hvo-roof/install.json";

    /// <summary>The installer's log when it runs as root.</summary>
    public const string SystemLog = "/var/log/hvo-roof-install.log";

    /// <summary>The person's hvo-roof folder: <c>$XDG_CONFIG_HOME/hvo-roof</c> when that is an absolute path, else <c>~/.config/hvo-roof</c> (where hvo-roof keeps its credentials).</summary>
    public static string UserConfigFolder(InstallerMachine machine)
        => Path.Join(XdgFolder(machine, "XDG_CONFIG_HOME") ?? Path.Join(machine.Home, ".config"), "hvo-roof");

    /// <summary>The record of the person's roles: hvo-roof, the Mac app, a rig on a Mac.</summary>
    public static string UserRecord(InstallerMachine machine) => Path.Join(UserConfigFolder(machine), "install.json");

    /// <summary>The record for <paramref name="scope"/>.</summary>
    public static string RecordFor(InstallScope scope, InstallerMachine machine)
        => scope == InstallScope.System ? SystemRecord : UserRecord(machine);

    /// <summary>Where the installer keeps itself as root, for upgrade, rollback, backup and uninstall.</summary>
    public const string SystemInstaller = "/usr/local/sbin/hvo-roof-install";

    /// <summary>
    /// Where the installer keeps itself for <paramref name="scope"/>: <see cref="SystemInstaller"/>, or for a person
    /// <c>~/.local/bin/hvo-roof-install</c>, next to hvo-roof.
    /// </summary>
    public static string Installer(InstallScope scope, InstallerMachine machine)
        => scope == InstallScope.System ? SystemInstaller : Path.Join(machine.Home, ".local", "bin", "hvo-roof-install");

    /// <summary>
    /// The installer's log: <see cref="SystemLog"/> as root; for a person, <c>~/Library/Logs/hvo-roof-install.log</c> on a
    /// Mac, or <c>$XDG_STATE_HOME/hvo-roof/install.log</c> (<c>~/.local/state</c> by default) on Linux.
    /// </summary>
    public static string Log(InstallerMachine machine)
    {
        if (machine.IsRoot)
        {
            return SystemLog;
        }

        return machine.Os == InstallerOs.MacOS
            ? Path.Join(machine.Home, "Library", "Logs", "hvo-roof-install.log")
            : Path.Join(XdgFolder(machine, "XDG_STATE_HOME") ?? Path.Join(machine.Home, ".local", "state"), "hvo-roof", "install.log");
    }

    /// <summary>A path the person gave with ~ for their home folder, as a path on the machine.</summary>
    public static string Expand(InstallerMachine machine, string path)
        => path == "~" ? machine.Home
            : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Join(machine.Home, path[2..])
            : path;

    // An XDG variable counts only when it is an absolute path, as the specification says.
    private static string? XdgFolder(InstallerMachine machine, string variable)
        => machine.Environment(variable) is { Length: > 0 } value && Path.IsPathRooted(value) ? value.TrimEnd('/') : null;
}
