using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The first admin and the camera (#69): the admin added through the controller's API with the installer's admin key,
/// only while the controller has none, and the camera's files in the secrets folder. Each password and PIN is typed
/// twice, or read from a file only its owner reads, and never shown, logged or saved.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerSecretTests
{
    private const string Secrets = "/etc/hvo-roof/secrets";
    private const string Password = "a long test password";
    private const string Pin = "9081726354";
    private const string CameraPassword = "camera test password";
    private const string AdminKeyFile = $"{Secrets}/RoofControllerSecurity__ApiKeys__1__Key";

    private static readonly CameraSettings Camera = new() { BaseUrl = "http://192.168.0.4:81", UserName = "roof-viewer" };

    private static InstallAnswers Answers(FirstAdminSettings? admin = null, CameraSettings? camera = null)
        => new() { Roles = [InstallRole.Controller], Controller = new ControllerSettings { FirstAdmin = admin, Camera = camera } };

    // Types each secret it is asked for, twice.
    private static Func<string, string?> Typing(params (string What, string Value)[] secrets)
        => what => secrets.FirstOrDefault(secret => what == secret.What || what == $"{secret.What} again").Value;

    private static void ShowsNoSecret(FakeMachine machine, InstallerRun run, params string[] secrets)
    {
        var log = machine.Read(InstallPaths.SystemLog);
        foreach (var secret in secrets.Concat(machine.ApiKeyValues()))
        {
            run.ToString().Should().NotContain(secret);
            log.Should().NotContain(secret);
        }

        foreach (var line in machine.Ran.Where(command => command.Arguments.Count > 0))
        {
            foreach (var secret in secrets)
            {
                line.Arguments.Should().NotContain(argument => argument.Contains(secret, StringComparison.Ordinal), "a secret is never on a command line");
            }
        }
    }

    [TestMethod]
    public async Task TheFirstAdmin_IsAddedThroughTheApi_WithTheInstallersAdminKey_AndNothingSecretIsShown()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the first admin's password", Password), ("the first admin's PIN", Pin));
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy", Pin = true }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Asked.Should().Equal("the first admin's password", "the first admin's password again", "the first admin's PIN", "the first admin's PIN again");
        pi.UserRequests.Should().ContainSingle().Which.Should().Be(
            new FakeUserRequest("roy", RoofControllerApiContract.AdminRole, Password, Pin, pi.Read(AdminKeyFile)));
        pi.People.Should().ContainKey("roy");
        run.Output.Should().Contain("roy").And.Contain("added through the controller's API as RoofAdmin, with a password and a PIN");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Added roy, the controller's first admin (RoofAdmin, with a PIN), with installer-admin.");
        ShowsNoSecret(pi, run, Password, Pin);

        var second = await pi.RunAsync("--answers", answers);
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change").And.Contain("there already, as RoofAdmin");
        pi.Asked.Should().HaveCount(4, "a second run asks for nothing");
        pi.UserRequests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task TheFirstAdmin_FromFilesOnlyTheirOwnerReads_AsksNothing()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Machine.WriteAtomically("/root/admin-password", $"{Password}\r\nthe second line is not read\n", Modes.PrivateFile);
        pi.Machine.WriteAtomically("/root/admin-pin", Pin, Modes.PrivateFile);
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy", Pin = true }));

        var run = await pi.RunAsync("--answers", answers, "--admin-password-file", "/root/admin-password", "--admin-pin-file", "/root/admin-pin");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Asked.Should().BeEmpty();
        pi.UserRequests.Should().ContainSingle().Which.Should().Match<FakeUserRequest>(request => request.Password == Password && request.Pin == Pin);
        ShowsNoSecret(pi, run, Password, Pin);
    }

    [TestMethod]
    public async Task ASecretFileOthersCanRead_IsRefused_BeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Machine.WriteAtomically("/root/admin-password", Password, Modes.File);
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" }));

        var run = await pi.RunAsync("--answers", answers, "--admin-password-file", "/root/admin-password");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("Others can read /root/admin-password (--admin-password-file): make it yours alone (chmod 600 /root/admin-password), then run the installer again.");
        pi.Exists(Secrets).Should().BeFalse("nothing was changed");
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("short")]
    [DataRow("")]
    public async Task ASecretFileThatDoesNotHoldOne_IsRefused(string content)
    {
        using var pi = new FakeMachine().WithPi();
        pi.Machine.WriteAtomically("/root/admin-password", content, Modes.PrivateFile);
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" }));

        var run = await pi.RunAsync("--answers", answers, "--admin-password-file", "/root/admin-password");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain($"The file /root/admin-password (--admin-password-file) does not hold the first admin's password: {RoofIdentityText.PasswordRule}");
        run.ToString().Should().NotContain("short\n");
    }

    [TestMethod]
    public async Task WithoutATerminal_TheInstallIsRefused_NamingTheFilesToGive()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy", Pin = true }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain(
            "The install needs the first admin's password and the first admin's PIN, and there is no terminal to type them at: "
            + "give them with --admin-password-file FILE and --admin-pin-file FILE. Nothing was changed.");
        pi.Exists(Secrets).Should().BeFalse("nothing was changed");
        pi.Read(InstallPaths.SystemLog).Should().Contain("Refused: The install needs the first admin's password");
    }

    [TestMethod]
    public async Task ASecretTypedDifferentlyTwice_OrOneTheControllerWouldRefuse_IsAskedForAgain()
    {
        using var pi = new FakeMachine().WithPi();
        var typed = new Queue<string>(["too short", Password, "another long password", Password, Password]);
        pi.Types = _ => typed.Dequeue();
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Error.Should().Contain(RoofIdentityText.PasswordRule).And.Contain("The two passwords differ.");
        pi.Asked.Should().Equal(
            "the first admin's password",
            "the first admin's password", "the first admin's password again",
            "the first admin's password", "the first admin's password again");
        pi.UserRequests.Should().ContainSingle().Which.Password.Should().Be(Password);
    }

    [TestMethod]
    public async Task ThreeWrongTries_StopTheInstall_BeforeAnythingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = what => what.EndsWith("again", StringComparison.Ordinal) ? "not the same password" : Password;
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("The first admin's password was not given in 3 tries, so nothing was changed.");
        pi.Exists(Secrets).Should().BeFalse();
    }

    [TestMethod]
    public async Task AControllerWithAnAdmin_GetsNoFirstAdmin_AndNoPasswordIsAskedFor()
    {
        using var pi = new FakeMachine().WithPi().WithPerson("maria", RoofControllerApiContract.AdminRole);
        pi.Types = Typing(("the first admin's password", Password));
        var answers = pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" }));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("not added: the controller has an admin already (maria)");
        pi.Asked.Should().BeEmpty();
        pi.UserRequests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APersonOfThatName_WhateverTheCase_IsLeftAsTheyAre()
    {
        using var pi = new FakeMachine().WithPi().WithPerson("Roy", RoofControllerApiContract.OperatorRole);

        var plan = await pi.RunAsync("--plan", "--answers", pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" })));

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("there already, as RoofOperator").And.NotContain("The install asks for");
    }

    [TestMethod]
    public async Task ThePlan_SaysWhichSecretsTheInstallAsksFor_AndAsksForNone()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Machine.WriteAtomically("/root/admin-pin", Pin, Modes.PrivateFile);

        var plan = await pi.RunAsync("--plan", "--answers", pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy", Pin = true }, Camera)), "--admin-pin-file", "/root/admin-pin");

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain("The install asks for the first admin's password (or give it with --admin-password-file FILE).")
            .And.Contain("The install asks for the camera's password (or give it with --camera-password-file FILE).")
            .And.NotContain("the first admin's PIN (or give it");
        pi.Asked.Should().BeEmpty();
        pi.Exists(Secrets).Should().BeFalse();
    }

    [TestMethod]
    public async Task SomeoneWhoAddedTheFirstAdminMeanwhile_KeepsTheirPassword()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the first admin's password", Password));
        pi.UsersAnswer = 409;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" })));

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("roy was added by someone else while the installer ran, so the password typed here was not used.");
    }

    [TestMethod]
    public async Task AControllerThatRefusesTheFirstAdmin_StopsTheInstall_SayingWhy()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the first admin's password", Password));
        pi.UsersAnswer = 400;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" })));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain("The controller did not add roy: HTTP 400, The request was refused (400).");
        ShowsNoSecret(pi, run, Password);
    }

    [TestMethod]
    public async Task TheCamera_IsWrittenToTheSecretsFolder_ForRootAlone_AndItsPasswordIsTyped()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the camera's password", CameraPassword));
        var answers = pi.WriteAnswers(Answers(camera: Camera));

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        foreach (var (setting, value) in new[] { (CameraSteps.BaseUrlSetting, "http://192.168.0.4:81"), (CameraSteps.UserNameSetting, "roof-viewer"), (CameraSteps.PasswordSetting, CameraPassword) })
        {
            pi.Read($"{Secrets}/{setting}").Should().Be(value, "the controller reads each file whole: no newline");
            pi.Mode($"{Secrets}/{setting}").Should().Be(Modes.PrivateFile);
        }

        run.Error.Should().Contain(HVO.RoofControllerV4.Installer.Installer.CameraCredentialReminder);
        pi.Asked.Should().Equal("the camera's password", "the camera's password again");
        ShowsNoSecret(pi, run, CameraPassword);

        var second = await pi.RunAsync("--answers", answers);
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        pi.Asked.Should().HaveCount(2, "the password in place is kept while the user stays the same");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ANewCameraUser_NeedsItsOwnPassword_AndTheControllerRedeploysToReadIt()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the camera's password", CameraPassword));
        (await pi.RunAsync("--answers", pi.WriteAnswers(Answers(camera: Camera)))).ExitCode.Should().Be(0);
        var moved = pi.WriteAnswers(Answers(camera: Camera with { UserName = "roof-viewer-2" }), "moved.json");

        var plan = await pi.RunAsync("--plan", "--answers", moved);
        plan.Output.Should().Contain("the user changes, so its password does").And.Contain("redeployed to read the camera's new settings");

        pi.Machine.WriteAtomically("/root/camera-password", "the second user's password\n", Modes.PrivateFile);
        var run = await pi.RunAsync("--answers", moved, "--camera-password-file", "/root/camera-password");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Read($"{Secrets}/{CameraSteps.UserNameSetting}").Should().Be("roof-viewer-2");
        pi.Read($"{Secrets}/{CameraSteps.PasswordSetting}").Should().Be("the second user's password");
        pi.Deploys.Should().HaveCount(2);
        pi.Asked.Should().HaveCount(2, "the file gave the new password");
    }

    [TestMethod]
    public async Task ACameraThatAsksForNoUser_HasEmptyCredentials_AndNoPasswordIsAskedFor()
    {
        using var pi = new FakeMachine().WithPi();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(Answers(camera: Camera with { UserName = null })));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Read($"{Secrets}/{CameraSteps.UserNameSetting}").Should().BeEmpty();
        pi.Read($"{Secrets}/{CameraSteps.PasswordSetting}").Should().BeEmpty();
        pi.Asked.Should().BeEmpty();
        run.Error.Should().NotContain(HVO.RoofControllerV4.Installer.Installer.CameraCredentialReminder);
    }

    [TestMethod]
    public async Task ACameraFileWrittenAfterTheControllerStarted_RedeploysIt()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the camera's password", CameraPassword));
        var answers = pi.WriteAnswers(Answers(camera: Camera));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);

        // As a run that wrote the files and stopped before it redeployed the controller leaves them.
        File.SetLastWriteTimeUtc(pi.OnDisk($"{Secrets}/{CameraSteps.BaseUrlSetting}"), DateTime.UtcNow.AddMinutes(1));
        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("redeployed to read the camera's new settings");
        pi.Deploys.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ACameraFileStampedAMomentAfterTheControllerStarted_DoesNotRedeployIt()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = pi.WriteAnswers(Answers(camera: Camera with { UserName = null }));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);

        // Docker Desktop's clock, which stamps the start on a Mac, can trail the Mac's by a moment.
        File.SetLastWriteTimeUtc(pi.OnDisk($"{Secrets}/{CameraSteps.BaseUrlSetting}"), DateTime.UtcNow + CameraSteps.ClockSlack / 2);
        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("Nothing to change");
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ACameraFileOthersCanRead_IsMadeRootsAlone_WithoutARedeploy()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the camera's password", CameraPassword));
        var answers = pi.WriteAnswers(Answers(camera: Camera));
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        var password = pi.OnDisk($"{Secrets}/{CameraSteps.PasswordSetting}");
        var written = File.GetLastWriteTimeUtc(password);
        File.SetUnixFileMode(password, Modes.File);

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("0644 → 0600");
        pi.Mode($"{Secrets}/{CameraSteps.PasswordSetting}").Should().Be(Modes.PrivateFile);
        pi.Read($"{Secrets}/{CameraSteps.PasswordSetting}").Should().Be(CameraPassword);
        File.GetLastWriteTimeUtc(password).Should().Be(written, "only its mode changed");
        pi.Deploys.Should().ContainSingle("the controller read the same values");
        pi.Asked.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ARig_ShowsTheEmulatorsCamera_WithNoUser()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }));

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Read($"{Secrets}/{CameraSteps.BaseUrlSetting}").Should().Be($"http://{MachineSurveyor.HatEmulatorContainer}:{HatEmulatorStep.ControlPort}");
        bench.Read($"{Secrets}/{CameraSteps.UserNameSetting}").Should().BeEmpty();
        bench.Read($"{Secrets}/{CameraSteps.PasswordSetting}").Should().BeEmpty();
        bench.Asked.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheControllersApi_IsCalledWithTheKeyAndBodyOnStandardInput_Only()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Types = Typing(("the first admin's password", Password));

        (await pi.RunAsync("--answers", pi.WriteAnswers(Answers(new FirstAdminSettings { Name = "roy" })))).ExitCode.Should().Be(0);

        var add = pi.Ran.Should().ContainSingle(command => command.Arguments.Last() == ControllerApi.Address + FirstAdminStep.UsersPath).Subject;
        add.Secret.Should().BeTrue("its output is never logged");
        add.Arguments.Should().StartWith(["exec", "-i", MachineSurveyor.ControllerContainer, "curl", "-sS", "--max-time", "15", "-K", "-"]);
        add.Input.Should().Contain("data-raw = \"{").And.Contain(Password);
    }
}
