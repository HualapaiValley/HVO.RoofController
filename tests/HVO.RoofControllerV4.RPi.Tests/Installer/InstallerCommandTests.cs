using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// hvo-roof-install from the command line (#67), on fake machines: <c>--version</c>, <c>--plan</c>, <c>--answers</c>, a
/// second run that changes nothing, and no secret in anything it prints, logs or writes.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerCommandTests
{
    private const string Version = "4.0.0+0123456789abcdef0123456789abcdef01234567";

    [TestMethod]
    public async Task Version_PrintsTheReleaseItInstalls_AndChangesNothing()
    {
        using var pi = new FakeMachine().WithPi();
        var before = pi.Snapshot();

        var run = await pi.RunAsync("--version");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Be(Version + Environment.NewLine);
        run.Error.Should().BeEmpty();
        pi.Snapshot().Should().Equal(before);
        pi.Ran.Should().BeEmpty("it does not even look at the machine");
    }

    [TestMethod]
    public async Task Help_NamesEachOption_Once()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--help");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("--answers <FILE>").And.Contain("--plan").And.Contain("It never moves the roof.");
        run.Output.Split("--version").Should().HaveCount(2, "the installer's own --version replaces the built-in one");
    }

    [TestMethod]
    public async Task AnOptionItDoesNotKnow_IsAUsageError()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("--yes").And.Contain("Run 'hvo-roof-install --help' for usage.");
    }

    [TestMethod]
    public async Task TheWizard_NeedsATerminal()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync();

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Be("The wizard needs a terminal. Without one, install from an answers file: hvo-roof-install --answers FILE." + Environment.NewLine);
    }

    [TestMethod]
    public async Task Plan_WithNothingRecorded_AsksForAnswers()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--plan");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("Nothing is recorded as installed here: give the answers to plan, hvo-roof-install --plan --answers FILE.");
    }

    [TestMethod]
    public async Task Plan_WithoutSudo_SaysWhatTheInstallWouldDo_AndChangesNothing()
    {
        using var pi = new FakeMachine(root: false).WithPi();
        pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        var before = pi.Snapshot();

        var run = await pi.RunAsync("--plan", "--answers", "answers.json");

        run.ExitCode.Should().Be(0, run.ToString());
        var lines = run.Output.Split(Environment.NewLine);
        lines[0].Should().Be("The plan for the controller on roofpi, 4.0.0:");
        lines.Should().Contain("Folders").And.Contain("Containers").And.Contain("13 to create, 0 to change, 0 unchanged.")
            .And.Contain("Installing the controller needs root: run the installer with sudo.");
        run.Error.Should().BeEmpty();
        pi.Snapshot().Should().Equal(before, "--plan changes nothing and writes no log");
        pi.Exists(InstallPaths.SystemLog).Should().BeFalse();
        pi.Exists("/home/pi/.local/state/hvo-roof/install.log").Should().BeFalse();
    }

    [TestMethod]
    public async Task Plan_WhatIsInstalled_WhenNoAnswersAreGiven()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }))).ExitCode.Should().Be(0);

        var run = await pi.RunAsync("--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Nothing to change: this machine is already as the answers describe.").And.NotContain("needs root");
    }

    [TestMethod]
    public async Task Plan_ThatIsBlocked_ExitsRefused()
    {
        using var pi = new FakeMachine().WithPi().WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { ComposeProject = "roof" });
        pi.PortsInUse.UnionWith([8443, 8088]);

        var run = await pi.RunAsync("--plan", "--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("Docker Compose made it (project roof)").And.Contain("Blocked: 3 steps cannot go ahead, so nothing will be installed.");
    }

    [TestMethod]
    public async Task Plan_ForARoleThisMachineCannotHave_IsRefused()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop");

        var run = await laptop.RunAsync("--plan", "--answers", laptop.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().StartWith("The installer cannot go ahead:" + Environment.NewLine + "  - ").And.Contain("only from the observatory's Raspberry Pi")
            .And.EndWith("Nothing was changed." + Environment.NewLine);
        run.Output.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Answers_InstallTheController_ThenASecondRunChangesNothing()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var first = await pi.RunAsync("--answers", answers);

        first.ExitCode.Should().Be(0, first.ToString());
        first.Output.Should().Contain("Creating folder /var/lib/hvo-roof/identity…").And.Contain("Creating file /etc/hvo-roof/install.json: done.");
        first.Output.Should().Contain("Installed the controller (4.0.0) on roofpi.")
            .And.Contain("The controller's API:  https://roofpi.local:8443/")
            .And.Contain("The web UI:            https://roofpi.local:8088/")
            .And.Contain("The roof has not moved.")
            .And.Contain("The install record: /etc/hvo-roof/install.json")
            .And.Contain("The log: /var/log/hvo-roof-install.log");
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Roles.Should().Equal(InstallRole.Controller);
        pi.Mode(InstallPaths.SystemLog).Should().Be(Modes.GroupFile);
        var log = pi.Read(InstallPaths.SystemLog);
        log.Should().Contain($"hvo-roof-install {Version} on roofpi (linux-arm64), as root.")
            .And.Contain("Made /var/lib/hvo-roof/identity (0700).")
            .And.Contain("Wrote /etc/hvo-roof/install.json: the controller, version 4.0.0.")
            .And.Contain("Installed the controller.");

        var before = pi.Snapshot(InstallPaths.SystemLog);
        var second = await pi.RunAsync("--answers", answers);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change: this machine is already as the answers describe.").And.NotContain("Creating");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before, "a second run changes nothing");
        pi.Read(InstallPaths.SystemLog).Should().StartWith(log, "the log is only added to");
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Answers_ThatNeedAContainerMade_AreRefused_BeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        var before = pi.Snapshot();

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Be("This installer cannot install roof-controller yet, so nothing was installed. The plan (--plan) shows what an install will do." + Environment.NewLine);
        run.Output.Should().NotContain("Creating");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(before, "only the log is written");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Refused: This installer cannot install roof-controller yet");
    }

    [TestMethod]
    public async Task Answers_ThatThisMachineCannotHave_AreRefused_AndLogged()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Cli] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Error.Should().Contain("never installed as root").And.Contain("Nothing was changed.");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Refused: hvo-roof is yours, and never installed as root");
        pi.Exists("/root/.local/bin/hvo-roof").Should().BeFalse();
    }

    [TestMethod]
    public async Task Answers_ThatAreBlocked_AreRefused_AndLogged()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.Write("/var/lib/hvo-roof", string.Empty);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("/var/lib/hvo-roof is a file, not a folder");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Refused: Blocked:");
    }

    [TestMethod]
    public async Task HvoRoofAlreadyThere_IsRecordedForThePerson()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy").WithCli("/home/roy/.local/bin/hvo-roof");

        var run = await laptop.RunAsync("--answers", laptop.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Cli] }));

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("hvo-roof: /home/roy/.local/bin/hvo-roof. Next, connect it to the controller: hvo-roof setup")
            .And.Contain("/home/roy/.local/bin is not on your PATH")
            .And.Contain("The install record: /home/roy/.config/hvo-roof/install.json")
            .And.Contain("The log: /home/roy/.local/state/hvo-roof/install.log");
        laptop.Mode("/home/roy/.config/hvo-roof").Should().Be(Modes.PrivateFolder);
        laptop.Mode("/home/roy/.config/hvo-roof/install.json").Should().Be(Modes.PrivateFile);
        laptop.Mode("/home/roy/.local/state/hvo-roof").Should().Be(Modes.PrivateFolder);
        laptop.Mode("/home/roy/.local/state/hvo-roof/install.log").Should().Be(Modes.PrivateFile);
        InstallRecord.Parse(laptop.Read("/home/roy/.config/hvo-roof/install.json")).Should().Match<InstallRecord>(record =>
            record.Scope == InstallScope.User && record.Hat == null && record.Cli!.Folder == CliSettings.HomeFolder);
    }

    [TestMethod]
    [DataRow("nope.json", "There is no answers file /root/nope.json.", DisplayName = "Missing")]
    [DataRow("bad.json", "The answers file is not valid:", DisplayName = "Not JSON")]
    public async Task AnAnswersFileThatCannotBeRead_IsAUsageError(string file, string expected)
    {
        using var pi = new FakeMachine().WithPi().Write("/root/bad.json", "roles: cli");

        var run = await pi.RunAsync("--answers", file);

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().StartWith(expected);
        pi.Exists(InstallPaths.SystemLog).Should().BeFalse("the answers are read before anything is opened");
    }

    [TestMethod]
    public async Task NoSecret_IsPrinted_Logged_OrWritten()
    {
        var secret = $"not-a-real-secret-{Guid.NewGuid():N}";
        using var pi = new FakeMachine().WithPi()
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer { Secret = secret })
            .WithCertificates();
        pi.PortsInUse.UnionWith([8443, 8088]);
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var runs = new[]
        {
            await pi.RunAsync("--plan", "--answers", answers),
            await pi.RunAsync("--answers", answers),
            await pi.RunAsync("--answers", answers),
            await pi.RunAsync("--plan")
        };

        runs.Select(run => run.ExitCode).Should().Equal(0, 0, 0, 0);
        foreach (var run in runs)
        {
            run.ToString().Should().NotContain(secret);
        }

        pi.AllText().Should().NotContain(secret, "not in the log, the record or the answers");
        var log = pi.Read(InstallPaths.SystemLog);
        log.Should().Contain("$ docker container inspect roof-controller").And.Contain("exit 0 (secret: output not logged)");
        pi.Ran.Should().Contain(command => command.Secret && command.Arguments.Contains("inspect"), "the container's description holds its environment");
    }

    [TestMethod]
    public async Task AProgramThatCannotBeRun_IsAFailedCommand_NotACrash()
    {
        var folder = Directory.CreateTempSubdirectory("hvo-install-programs-");
        try
        {
            // hvo-roof downloaded without chmod +x, and a program built for another machine (an ELF header and nothing else).
            var notExecutable = Path.Join(folder.FullName, "hvo-roof");
            File.WriteAllText(notExecutable, "#!/bin/sh\necho 4.0.0\n");
            File.SetUnixFileMode(notExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var otherMachine = Path.Join(folder.FullName, "hvo-roof-other");
            File.WriteAllBytes(otherMachine, [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            File.SetUnixFileMode(otherMachine, Modes.Program);
            var runner = new ProcessCommandRunner(name => name == "PATH" ? folder.FullName : null);

            runner.Find("hvo-roof").Should().BeNull("a file that is not executable is not a program");
            runner.Find(notExecutable).Should().BeNull("by its path either");
            (await runner.RunAsync(new CommandLine(notExecutable, "--version"))).ExitCode.Should().Be(CommandResult.NotFound);

            var result = await runner.RunAsync(new CommandLine(otherMachine, "--version"));
            result.ExitCode.Should().Be(CommandResult.CannotRun);
            result.Error.Should().StartWith($"{otherMachine} could not be run: ");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task AFileTheInstallerCannotWrite_OutsideAStep_EndsInFailed_NotACrash()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy").WithCli("/home/roy/.local/bin/hvo-roof");
        laptop.Write("/home/roy/.local/state", "a file where the log's folder goes");

        var run = await laptop.RunAsync("--answers", laptop.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Cli] }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().StartWith("The installer stopped: ");
    }
}
