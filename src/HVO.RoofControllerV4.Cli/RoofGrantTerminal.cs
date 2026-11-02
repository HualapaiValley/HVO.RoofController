using System.Runtime.InteropServices;
using System.Text;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// Practical checks for a person's grant prompt. A terminal cannot prove a person is present: a sufficiently capable
/// local program can create a controlling foreground pty and answer a challenge. These checks reject common wrappers.
/// </summary>
internal static class RoofGrantTerminal
{
    internal static readonly string[] AutomationMarkers =
    [
        "CI", "GITHUB_ACTIONS", "GITLAB_CI", "TF_BUILD", "JENKINS_URL", "TEAMCITY_VERSION",
        "EXPECT", "EXPECT_SCRIPT", "PEXPECT", "AUTOMATION", "NONINTERACTIVE"
    ];

    internal static bool HasAutomationMarker(Func<string, string?> environment)
        => AutomationMarkers.Any(name => !string.IsNullOrEmpty(environment(name)))
            || string.Equals(environment("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environment("DEBIAN_FRONTEND"), "noninteractive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// All three standard descriptors must name the same controlling foreground terminal. Linux also exposes the
    /// terminal's session ID, which must be this process's session. Any unsupported or failed check refuses the grant.
    /// </summary>
    internal static bool Check()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;

        try
        {
            var group = GetProcessGroup();
            var session = GetSession(0);
            if (group <= 0 || session <= 0) return false;
            string? terminal = null;
            for (var fd = 0; fd <= 2; fd++)
            {
                if (IsTty(fd) != 1 || GetForegroundGroup(fd) != group) return false;
                if (OperatingSystem.IsLinux() && GetTerminalSession(fd) != session) return false;

                var buffer = new byte[1024];
                if (GetTtyName(fd, buffer, (nuint)buffer.Length) != 0) return false;
                var end = Array.IndexOf(buffer, (byte)0);
                if (end <= 0) return false;
                var name = Encoding.UTF8.GetString(buffer, 0, end);
                if (terminal is not null && !string.Equals(terminal, name, StringComparison.Ordinal)) return false;
                terminal = name;
            }

            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "isatty")]
    private static extern int IsTty(int fd);

    [DllImport("libc", EntryPoint = "ttyname_r")]
    private static extern int GetTtyName(int fd, [Out] byte[] name, nuint length);

    [DllImport("libc", EntryPoint = "getpgrp")]
    private static extern int GetProcessGroup();

    [DllImport("libc", EntryPoint = "getsid")]
    private static extern int GetSession(int pid);

    [DllImport("libc", EntryPoint = "tcgetpgrp")]
    private static extern int GetForegroundGroup(int fd);

    [DllImport("libc", EntryPoint = "tcgetsid")]
    private static extern int GetTerminalSession(int fd);
}
