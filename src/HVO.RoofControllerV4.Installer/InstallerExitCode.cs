namespace HVO.RoofControllerV4.Installer;

/// <summary>The exit codes of <c>hvo-roof-install</c>. install.sh and scripts rely on them; docs/install.md lists them.</summary>
public enum InstallerExitCode
{
    /// <summary>Installed (or, with <c>--plan</c>, the plan can be carried out).</summary>
    Success = 0,

    /// <summary>A step failed. The log says which, and what it did before; running the installer again carries on.</summary>
    Failed = 1,

    /// <summary>The command line or the answers file was not valid.</summary>
    Usage = 2,

    /// <summary>
    /// The installer refused: a role this machine cannot have, a missing prerequisite (sudo, Docker), or something it
    /// will not replace. Nothing was changed.
    /// </summary>
    Refused = 3,

    /// <summary>The person quit before installing, or the installer was interrupted.</summary>
    Cancelled = 130
}

/// <summary>A failure the installer explains to the person: its message is what they read.</summary>
public class InstallerException : Exception
{
    public InstallerException(string message, InstallerExitCode exitCode = InstallerExitCode.Failed)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public InstallerException(string message, Exception inner)
        : base(message, inner)
    {
        ExitCode = InstallerExitCode.Failed;
    }

    public InstallerExitCode ExitCode { get; }
}

/// <summary>The command line or the answers file was not valid.</summary>
public sealed class InstallerUsageException(string message) : InstallerException(message, InstallerExitCode.Usage);

/// <summary>The installer will not do what was asked on this machine; nothing was changed.</summary>
public sealed class InstallerRefusedException(string message) : InstallerException(message, InstallerExitCode.Refused);
