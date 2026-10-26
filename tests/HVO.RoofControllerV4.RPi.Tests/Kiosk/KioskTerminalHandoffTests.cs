using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.RPi.Tests.Web;
using KioskProgram = HVO.RoofControllerV4.Kiosk.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class KioskTerminalHandoffTests
{
    [TestMethod]
    [DataRow("success", "exited", "0")]
    [DataRow("exit-code", "exited", "1")]
    [DataRow("exit-code", "exited", "78")]
    [DataRow("signal", "killed", "TERM")]
    [DataRow("timeout", "killed", "KILL")]
    [DataRow(null, null, null)]
    public void UnrequestedExits_DoNotStartLogin(string? result, string? code, string? status)
    {
        using var fixture = new HandoffFixture();
        fixture.Run(result, code, status).Should().Be(0);
        fixture.Commands.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FailedHandoff_ReportsRecoveryWithoutRestartingController(bool failSystemctl)
    {
        using var fixture = new HandoffFixture();
        fixture.Run("success", "exited", "80", failSystemctl ? 1 : 0, failSystemctl ? 0 : 1).Should().Be(1);
        fixture.Error.Should().Contain("handoff failed").And.Contain("sudo systemctl");
        fixture.Commands.Should().Equal(failSystemctl
            ? ["systemctl --no-block restart hvo-roof-terminal.service"]
            : ["systemctl --no-block restart hvo-roof-terminal.service", "chvt 1"]);
    }

    [TestMethod]
    public async Task ProcessExit_ReleasesExclusiveDisplayAndInput_BeforeHandoff_AndReentryAcquiresBoth()
    {
        // Avalonia's pinned DRM/libinput implementation has no disposal API. Native descriptors and workers
        // belong to the process. Model both as exclusive OS resources, rather than an in-memory Dispose flag.
        using var files = new WebTestSupport.TempDirectory();
        using var fixture = new HandoffFixture();
        var script = """
            import fcntl, os, sys
            names = ('drm', 'input') if sys.argv[2] in ('hold', 'probe') else (sys.argv[2],)
            handles = [open(os.path.join(sys.argv[1], name), 'a+') for name in names]
            for handle in handles:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            print('owned', flush=True)
            if sys.argv[2] == 'hold':
                sys.stdin.readline()
                sys.exit(80)
            """;
        using var kiosk = Start("hold");
        try
        {
            (await kiosk.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("owned");
            foreach (var resource in new[] { "drm", "input" })
            {
                using var blocked = Start(resource);
                await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                blocked.ExitCode.Should().NotBe(0, $"{resource} belongs to the running kiosk");
            }
            fixture.Commands.Should().BeEmpty("post-exit handoff cannot happen before process/resource release");
            await kiosk.StandardInput.WriteLineAsync("exit");
            await kiosk.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            kiosk.ExitCode.Should().Be(KioskProgram.TerminalExitCode);
            using (var released = Start("probe"))
            {
                await released.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                released.ExitCode.Should().Be(0, "display and input are free before login or VT selection");
            }
            fixture.Run("success", "exited", kiosk.ExitCode.ToString()).Should().Be(0);
            fixture.Commands.Should().Equal("systemctl --no-block restart hvo-roof-terminal.service", "chvt 1");
            using var returning = Start("probe");
            await returning.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            returning.ExitCode.Should().Be(0, "a new kiosk reacquires both resources");
        }
        finally
        {
            if (!kiosk.HasExited) { kiosk.Kill(entireProcessTree: true); await kiosk.WaitForExitAsync(); }
        }

        Process Start(string mode)
        {
            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-u", "-c", script, files.Path, mode }) start.ArgumentList.Add(arg);
            return Process.Start(start)!;
        }
    }

    [TestMethod]
    public void InstalledUnitPolicy_RecoversCrashesButLeavesAuthorizedLoginVisible()
    {
        var kiosk = Unit("hvo-roof-kiosk.service");
        var terminal = Unit("hvo-roof-terminal.service");
        Restart(0).Should().BeTrue("unrequested clean exits still recover");
        Restart(1).Should().BeTrue("crashes recover");
        Restart(78).Should().BeFalse("invalid settings need correction");
        Restart(KioskProgram.TerminalExitCode).Should().BeFalse("login must not be covered by a restarted kiosk");
        kiosk["SuccessExitStatus"].Should().Be("80");
        kiosk["Environment"].Split(' ').Should().Contain("HVO_KIOSK_TERMINAL_HANDOFF=1");
        kiosk["ExecStopPost"].Should().Be("+/bin/sh /opt/hvo-roof-kiosk/terminal-handoff.sh");
        kiosk["Conflicts"].Split(' ').Should().Contain(["hvo-roof-terminal.service", "getty@tty1.service"]);
        terminal["After"].Split(' ').Should().Contain("hvo-roof-kiosk.service", "login follows process/resource release");
        terminal["Conflicts"].Split(' ').Should().Contain(["hvo-roof-kiosk.service", "getty@tty1.service"]);
        terminal["ExecStart"].Should().Be("/sbin/agetty --noclear --login-program /bin/login tty1 linux");
        foreach (var unit in new[] { kiosk, terminal })
        {
            unit["TTYVHangup"].Should().Be("yes", "returning UI or login hangs up any previous terminal session");
            unit["TTYReset"].Should().Be("yes");
            unit["TTYVTDisallocate"].Should().Be("yes");
            unit["TTYPath"].Should().Be("/dev/tty1");
        }
        terminal["KillMode"].Should().Be("control-group");
        bool Restart(int status) => kiosk["Restart"] == "always" && !kiosk["RestartPreventExitStatus"].Split(' ').Contains(status.ToString());
    }

    private static Dictionary<string, string> Unit(string resource) => KioskSteps.Resource(resource).Split('\n')
        .Select(line => line.Trim()).Where(line => !line.StartsWith('#') && line.Contains('='))
        .GroupBy(line => line[..line.IndexOf('=')]).ToDictionary(group => group.Key,
            group => string.Join(' ', group.Select(line => line[(line.IndexOf('=') + 1)..])));

    /// <summary>Runs the installed shell helper, replacing only its two absolute host commands with fixtures.</summary>
    private sealed class HandoffFixture : IDisposable
    {
        private readonly WebTestSupport.TempDirectory _files = new();
        private string Log => Path.Combine(_files.Path, "commands");
        public string[] Commands => File.Exists(Log) ? File.ReadAllLines(Log) : [];
        public string Error { get; private set; } = string.Empty;

        public int Run(string? result, string? code, string? status, int systemctlExit = 0, int chvtExit = 0)
        {
            var helper = KioskSteps.Resource("terminal-handoff.sh");
            foreach (var (command, exit) in new[] { ("systemctl", systemctlExit), ("chvt", chvtExit) })
            {
                var path = Path.Combine(_files.Path, command);
                File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' \"{command} $*\" >> \"$HVO_TEST_COMMAND_LOG\"\nexit {exit}\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                helper = helper.Replace("/usr/bin/" + command, "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'", StringComparison.Ordinal);
            }
            var script = Path.Combine(_files.Path, "handoff.sh");
            File.WriteAllText(script, helper);
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(script);
            start.Environment["SERVICE_RESULT"] = result ?? "";
            start.Environment["EXIT_CODE"] = code ?? "";
            start.Environment["EXIT_STATUS"] = status ?? "";
            start.Environment["HVO_TEST_COMMAND_LOG"] = Log;
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("The emulated handoff did not finish");
            }
            Error = process.StandardError.ReadToEnd();
            return process.ExitCode;
        }

        public void Dispose() => _files.Dispose();
    }
}
