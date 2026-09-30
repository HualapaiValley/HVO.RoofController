using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Roles;

/// <summary>What the installer can set up on a machine. Answers files and install records name them in kebab case (mac-app).</summary>
public enum InstallRole
{
    /// <summary>The controller, driving the real HAT: only on the observatory's Raspberry Pi.</summary>
    Controller,

    /// <summary>A test rig: the controller against the HAT emulator, on Linux or a Mac with Docker.</summary>
    Rig,

    /// <summary>The touchscreen kiosk, on the controller's Pi.</summary>
    Kiosk,

    /// <summary>hvo-roof, the command-line client, on Linux or a Mac.</summary>
    Cli,

    /// <summary>The Mac app, on an Apple silicon Mac.</summary>
    MacApp
}

/// <summary>Whose the installed files are.</summary>
public enum InstallScope
{
    /// <summary>The machine's: the installer runs as root (with sudo) and records it in /etc/hvo-roof/install.json.</summary>
    System,

    /// <summary>The person's: the installer runs as them, never as root, and records it in their settings folder.</summary>
    User
}

/// <summary>What people read about each role, and where each is installed.</summary>
public static class InstallRoles
{
    /// <summary>Every role, in the order the installer offers and installs them.</summary>
    public static IReadOnlyList<InstallRole> All { get; } = [InstallRole.Controller, InstallRole.Rig, InstallRole.Kiosk, InstallRole.Cli, InstallRole.MacApp];

    /// <summary>The name answers files and install records use.</summary>
    public static string Name(InstallRole role) => role switch
    {
        InstallRole.Controller => "controller",
        InstallRole.Rig => "rig",
        InstallRole.Kiosk => "kiosk",
        InstallRole.Cli => "cli",
        InstallRole.MacApp => "mac-app",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    /// <summary>The role as a person reads it in a sentence.</summary>
    public static string Title(InstallRole role) => role switch
    {
        InstallRole.Controller => "the controller",
        InstallRole.Rig => "a test rig",
        InstallRole.Kiosk => "the kiosk",
        InstallRole.Cli => "hvo-roof",
        InstallRole.MacApp => "the Mac app",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    /// <summary>The label of the role's choice in the wizard.</summary>
    public static string Label(InstallRole role) => role switch
    {
        InstallRole.Controller => "Controller: drives the roof through the real HAT (the observatory's Pi)",
        InstallRole.Rig => "Test rig: the controller against the HAT emulator (nothing moves)",
        InstallRole.Kiosk => "Kiosk: the touchscreen on the controller's Pi",
        InstallRole.Cli => "hvo-roof: the command-line client",
        InstallRole.MacApp => "Mac app: HVO Roof",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    /// <summary>
    /// Whose files the role's are: the controller, the kiosk and a rig on Linux are the machine's; hvo-roof, the Mac app
    /// and a rig on a Mac (in Docker Desktop, which is the person's) are the person's.
    /// </summary>
    public static InstallScope ScopeOf(InstallRole role, InstallerOs os) => role switch
    {
        InstallRole.Controller or InstallRole.Kiosk => InstallScope.System,
        InstallRole.Rig => os == InstallerOs.MacOS ? InstallScope.User : InstallScope.System,
        _ => InstallScope.User
    };

    /// <summary>The roles in the order the installer offers them, each once.</summary>
    public static IReadOnlyList<InstallRole> Ordered(IEnumerable<InstallRole> roles)
    {
        var set = roles.ToHashSet();
        return All.Where(set.Contains).ToArray();
    }

    /// <summary>The roles as a person reads them: "the controller and the kiosk".</summary>
    public static string Describe(IEnumerable<InstallRole> roles)
    {
        var titles = Ordered(roles).Select(Title).ToArray();
        return titles.Length switch
        {
            0 => "nothing",
            1 => titles[0],
            _ => string.Join(", ", titles[..^1]) + " and " + titles[^1]
        };
    }

    /// <summary>True when <paramref name="roles"/> run the controller's container (the controller, or a rig).</summary>
    public static bool RunsController(IEnumerable<InstallRole> roles)
        => roles.Any(role => role is InstallRole.Controller or InstallRole.Rig);

    /// <summary>True when <paramref name="roles"/> set up a client of a controller elsewhere: hvo-roof, or the Mac app.</summary>
    public static bool UsesController(IEnumerable<InstallRole> roles)
        => roles.Any(role => role is InstallRole.Cli or InstallRole.MacApp);

    /// <summary>The text with its first letter in upper case; hvo-roof, a command's name, keeps its case.</summary>
    public static string Capitalise(string text)
        => text.Length == 0 || text.StartsWith("hvo-roof", StringComparison.Ordinal) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
