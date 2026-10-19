using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.RPi.Tests.Cli;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Actual final binaries through both installation copy paths, on a fake machine only (#172).</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class TerminalInstalledNoticeTests
{
    [TestMethod]
    [TestCategory("TerminalPublished")]
    public async Task FinalPrograms_KeepOfflineNotices_AfterInstallationAndRollbackCopyPaths()
    {
        var dist = Environment.GetEnvironmentVariable("HVO_TERMINAL_DIST");
        if (dist is null) Assert.Inconclusive("Run with HVO_TERMINAL_DIST naming the final release-assembled terminal files.");
        var os = OperatingSystem.IsMacOS() ? InstallerOs.MacOS : InstallerOs.Linux;
        using var machine = new FakeMachine(os, RuntimeInformation.OSArchitecture, root: false);
        var folder = machine.ReleaseFolder();
        var manifestPath = machine.OnDisk(Path.Join(folder, "release.json"));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var rid = machine.Machine.RuntimeIdentifier;
        var steps = new PlanStep[]
        {
            new CliProgramStep("/programs", planned: false, "choose another folder"),
            new InstallerProgramStep(InstallScope.User, machine.Machine)
        };
        foreach (var name in new[] { "hvo-roof", "hvo-roof-install" })
        {
            var assetName = $"{name}-{rid}";
            var source = Path.Combine(dist!, assetName);
            File.Exists(source).Should().BeTrue("CI must supply the exact final program for this host");
            File.Copy(source, machine.OnDisk(Path.Join(folder, assetName)), overwrite: true);
            var asset = manifest["assets"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == assetName)!;
            asset["size"] = new FileInfo(source).Length;
            await using var input = File.OpenRead(source);
            asset["sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var context = new InstallContext
        {
            Machine = machine.Machine, Log = InstallLog.None,
            Survey = await MachineSurveyor.SurveyAsync(machine.Machine), Answers = new InstallAnswers(),
            Version = "4.0.0", Release = ReleaseSource.Folder(folder)
        };
        foreach (var step in steps)
        {
            machine.Folder(Path.GetDirectoryName(step.Target)!);
            var check = await step.CheckAsync(context, default);
            check.Change.Should().Be(StepChange.Create);
            await step.ApplyAsync(context, check, default);
            await AssertInstalledNoticesAsync(machine, step.Target);

            // The kept program swap (rollback) goes through ProgramFile too. No install/service/controller is run.
            var previous = step.Target + ".previous";
            machine.Machine.MoveFile(step.Target, previous);
            machine.Write(step.Target, "a different program kept for rollback");
            await step.ApplyAsync(context, new StepCheck(StepChange.Change), default);
            await AssertInstalledNoticesAsync(machine, step.Target);
            machine.Read(previous).Should().Be("a different program kept for rollback");
        }
        machine.Unexpected.Should().BeEmpty();
    }

    private static async Task AssertInstalledNoticesAsync(FakeMachine machine, string target)
    {
        var privateHome = machine.OnDisk("/notice-home");
        Directory.CreateDirectory(privateHome);
        var extraction = Path.Join(privateHome, "extraction");
        var start = new ProcessStartInfo(machine.OnDisk(target))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, WorkingDirectory = privateHome
        };
        start.ArgumentList.Add("--third-party-notices");
        start.Environment.Clear();
        start.Environment["HOME"] = privateHome;
        start.Environment["PATH"] = privateHome;
        start.Environment["XDG_CONFIG_HOME"] = Path.Join(privateHome, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Join(privateHome, "cache");
        start.Environment["TMPDIR"] = privateHome;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extraction;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        process.ExitCode.Should().Be(0);
        (await error).Should().BeEmpty();
        TerminalPayloadPolicyTests.AssertNotices(await output);
        Directory.Exists(extraction).Should().BeFalse("the excluded native wrapper needs no extraction");
        Directory.GetFiles(privateHome, "*", SearchOption.AllDirectories).Should().BeEmpty("notices read and mutate no machine state");
    }
}
