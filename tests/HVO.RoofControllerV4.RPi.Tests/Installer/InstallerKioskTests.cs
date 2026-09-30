using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.Kiosk;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The kiosk on the controller's Pi (#70), as docs/kiosk.md sets it up by hand: its packages, its user, its program from
/// the release with the one it replaces kept, its device key, its settings, its unit and backlight rule, its service, the
/// console's cursor hidden, and PINs for the people chosen. The display and the touchscreen stay documented assumptions:
/// here they are the kernel's files that list them.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerKioskTests
{
    private const string Secrets = "/etc/hvo-roof/secrets";
    private const string CommandLine = "/boot/firmware/cmdline.txt";
    private const string Booted = "console=serial0,115200 console=tty1 root=PARTUUID=0a1b2c3d-02 rootfstype=ext4 fsck.repair=yes rootwait";
    private const string Pin = "735193";

    private static readonly string TarballUri = ReleaseManifest.DownloadUri("4.0.0", FakeMachine.KioskAssetName("4.0.0")).ToString();

    private static readonly InstallAnswers WithTheController = new() { Roles = [InstallRole.Controller, InstallRole.Kiosk] };

    [TestMethod]
    public async Task TheKiosk_IsInstalledWithTheController_AsTheGuideSetsItUp_AndASecondRunChangesNothing()
    {
        using var pi = KioskPi();
        var answers = pi.WriteAnswers(WithTheController);

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Packages.Should().BeEquivalentTo(KioskSteps.Packages);
        pi.AptInstalls.Should().ContainSingle().Which.Environment.Should().Contain("DEBIAN_FRONTEND", "noninteractive");
        pi.Users[KioskSteps.User].Should().BeEquivalentTo([KioskSteps.User, .. KioskSteps.Groups]);

        File.ReadAllBytes(pi.OnDisk(KioskSteps.Program)).Should().Equal(FakeMachine.KioskProgram("4.0.0"));
        pi.Mode(KioskSteps.Program).Should().Be(Modes.Program);
        pi.Exists(KioskSteps.PreviousProgram).Should().BeFalse("there was no kiosk to keep");
        pi.Mode(KioskSteps.ConfigurationFolder).Should().Be(Modes.GroupFolder);
        pi.Owners[KioskSteps.ConfigurationFolder].Should().Be(KioskSteps.FolderOwner);

        var kioskKey = ApiKeyFiles.KeyFile(Secrets, 3);
        pi.Read(KioskSteps.DeviceKeyFile).Should().Be(pi.Read(kioskKey).Trim(), "the kiosk signs in with the controller's kiosk key");
        pi.Mode(KioskSteps.DeviceKeyFile).Should().Be(Modes.OwnerReadOnly);
        pi.Owners[KioskSteps.DeviceKeyFile].Should().Be(KioskSteps.KeyOwner);

        var options = Options(pi);
        options.Validate().Should().BeEmpty();
        options.ControllerUrl.Should().Be(new Uri("https://localhost:8443/"));
        options.ServerCaCertificateFile.Should().Be("/etc/hvo-roof/ca.crt");
        options.ServerCertificateSha256.Should().BeNull("the CA is trusted: no pin");
        options.DeviceKeyFile.Should().Be(KioskSteps.DeviceKeyFile);
        options.Rotation.Should().Be(90, "the rest of the example's settings are kept");

        pi.Read(MachineSurveyor.KioskUnitFile).Should().Be(KioskSteps.Resource("hvo-roof-kiosk.service"));
        pi.Read(KioskSteps.BacklightRuleFile).Should().Be(KioskSteps.Resource("99-hvo-roof-kiosk-backlight.rules"));
        pi.Kiosk.Should().Be((true, true));
        pi.KioskStarts.Should().Be(1);
        pi.DaemonReloads.Should().Be(1);
        pi.BacklightTriggers.Should().Be(1);
        pi.Read(CommandLine).Should().Be($"{Booted} {KioskSteps.HideCursorSetting}\n");

        var record = InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord));
        record.Roles.Should().Equal(InstallRole.Controller, InstallRole.Kiosk);
        record.Kiosk.Should().Be(new KioskSettings());
        run.Output.Should().Contain($"The kiosk runs on the touchscreen, and starts when the Pi does ({MachineSurveyor.KioskUnit}). Its log: journalctl -u {MachineSurveyor.KioskUnit}")
            .And.Contain("Reboot to hide the console's cursor behind the kiosk: sudo reboot");
        ShowsNoSecret(pi, run);
        pi.Unexpected.Should().BeEmpty();

        var files = pi.Snapshot(InstallPaths.SystemLog);
        var second = await pi.RunAsync("--answers", answers);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        pi.KioskStarts.Should().Be(1, "a kiosk that runs what is in place is not started again");
        pi.AptInstalls.Should().ContainSingle();
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    public async Task AKioskSystemdIsStartingAgain_IsLeftToIt()
    {
        using var pi = KioskPi();
        var answers = pi.WriteAnswers(WithTheController);
        (await pi.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        pi.KioskRestarting = true;

        var plan = await pi.RunAsync("--answers", answers, "--plan");
        var second = await pi.RunAsync("--answers", answers);

        plan.Output.Should().Contain($"enabled, and systemd is starting it again: if it keeps stopping, see journalctl -u {MachineSurveyor.KioskUnit}");
        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        pi.KioskStarts.Should().Be(1, "systemd restarts a kiosk that stopped by itself");
    }

    [TestMethod]
    public async Task TheKiosk_AddedToAnInstalledController_UsesItsSettings_AndGivesItAKioskKey()
    {
        using var pi = KioskPi();
        var controller = new InstallAnswers { Roles = [InstallRole.Controller], Controller = new ControllerSettings { Connection = ConnectionMode.SelfSigned } };
        (await pi.RunAsync("--answers", pi.WriteAnswers(controller))).ExitCode.Should().Be(0);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Kiosk] }, "kiosk.json"));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().HaveCount(2, "the controller is redeployed to know the kiosk's key");
        pi.Deploys[^1]["EXTRA_DOCKER_ARGS"].Should().NotContain("ApiKeys__3", "the key is in the secrets folder, not on the command line");
        pi.Read(KioskSteps.DeviceKeyFile).Should().Be(pi.Read(ApiKeyFiles.KeyFile(Secrets, 3)).Trim());
        Options(pi).ServerCertificateSha256.Should().Be(ServedFingerprint(pi), "the recorded controller's self-signed certificate is pinned");
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Should().Match<InstallRecord>(record =>
            record.Roles.SequenceEqual(new[] { InstallRole.Controller, InstallRole.Kiosk }) && record.Controller!.Connection == ConnectionMode.SelfSigned);
        pi.KioskStarts.Should().Be(1);
        ShowsNoSecret(pi, run);
    }

    [TestMethod]
    public async Task OverHttp_TheKioskUsesTheControllersHttpPort()
    {
        using var pi = KioskPi();
        var answers = JsonNode.Parse((WithTheController with { Controller = new ControllerSettings { Connection = ConnectionMode.Http } }).ToJson())!.AsObject();
        answers["httpConfirmation"] = "http";
        pi.Write("/root/answers.json", answers.ToJsonString());

        var run = await pi.RunAsync("--answers", "/root/answers.json");

        run.ExitCode.Should().Be(0, run.ToString());
        var options = Options(pi);
        options.Validate().Should().BeEmpty();
        options.ControllerUrl.Should().Be(new Uri("http://localhost:8080/"));
        options.ServerCaCertificateFile.Should().BeNull();
        options.ServerCertificateSha256.Should().BeNull();
    }

    [TestMethod]
    public async Task WithASelfSignedCertificate_TheKioskPinsIt_AndARenewPinsTheNewOne_AndStartsItAgain()
    {
        using var pi = KioskPi();
        var answers = WithTheController with { Controller = new ControllerSettings { Connection = ConnectionMode.SelfSigned } };
        (await pi.RunAsync("--answers", pi.WriteAnswers(answers))).ExitCode.Should().Be(0);
        var first = ServedFingerprint(pi);
        Options(pi).ServerCertificateSha256.Should().Be(first);
        pi.Replies = _ => true;

        var renew = await pi.RunAsync("cert", "--renew");

        renew.ExitCode.Should().Be(0, renew.ToString());
        renew.Output.Should().Contain("The controller serves the new certificate.").And.Contain($"{KioskSteps.SettingsFile}: done.");
        var renewed = ServedFingerprint(pi);
        renewed.Should().NotBe(first);
        var options = Options(pi);
        options.ServerCertificateSha256.Should().Be(renewed, "the kiosk trusts the certificate the controller serves");
        options.Validate().Should().BeEmpty();
        pi.KioskStarts.Should().Be(2, "the kiosk is started again to read its new pin");

        var again = await pi.RunAsync("cert");
        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("Nothing to change");
        pi.KioskStarts.Should().Be(2);
    }

    [TestMethod]
    public async Task ARenew_UnderThePrivateCa_LeavesTheKioskAsItIs()
    {
        using var pi = KioskPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController))).ExitCode.Should().Be(0);
        var settings = pi.Read(KioskSteps.SettingsFile);
        pi.Replies = _ => true;

        var renew = await pi.RunAsync("cert", "--renew");

        renew.ExitCode.Should().Be(0, renew.ToString());
        renew.Output.Should().Contain("The controller serves the new certificate.").And.NotContain(KioskSteps.SettingsFile);
        pi.Read(KioskSteps.SettingsFile).Should().Be(settings, "the kiosk trusts the CA, which issued the new certificate too");
        pi.KioskStarts.Should().Be(1);
    }

    [TestMethod]
    public async Task AnUpdate_KeepsTheKioskItReplaces_ForARollback_AndStartsTheNewOne()
    {
        using var pi = KioskPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController))).ExitCode.Should().Be(0);
        var older = FakeMachine.KioskProgram("3.9.0");
        pi.Machine.WriteAtomically(KioskSteps.Program, Encoding.UTF8.GetString(older), Modes.Program);

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController));

        run.ExitCode.Should().Be(0, run.ToString());
        File.ReadAllBytes(pi.OnDisk(KioskSteps.Program)).Should().Equal(FakeMachine.KioskProgram("4.0.0"));
        File.ReadAllBytes(pi.OnDisk(KioskSteps.PreviousProgram)).Should().Equal(older);
        pi.Mode(KioskSteps.PreviousProgram).Should().Be(Modes.Program);
        pi.Exists($"{KioskSteps.Program}.new").Should().BeFalse();
        pi.KioskStarts.Should().Be(2, "the kiosk is started again to run the new program");
        pi.Read(InstallPaths.SystemLog).Should().Contain($"Kept the kiosk's program it replaces as {KioskSteps.PreviousProgram}.");
    }

    [TestMethod]
    public async Task AKioskTarballThatIsNotTheReleases_IsRefused_AndInstallsNoProgram()
    {
        using var pi = KioskPi();
        var tarball = FakeMachine.KioskTarball("4.0.0");
        tarball[tarball.Length / 2] ^= 0xff;
        pi.FileDownloads[TarballUri] = tarball;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"is not the release's {FakeMachine.KioskAssetName("4.0.0")}: its size or SHA-256 differs from release.json's.");
        pi.Exists(KioskSteps.Program).Should().BeFalse();
        pi.Exists($"{KioskSteps.Program}.new").Should().BeFalse();
        pi.KioskStarts.Should().Be(0);
    }

    [TestMethod]
    public async Task AKioskProgramThatIsNotTheOneReleaseJsonNames_IsNeverPutInPlace()
    {
        using var pi = KioskPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController))).ExitCode.Should().Be(0);
        var older = FakeMachine.KioskProgram("3.9.0");
        pi.Machine.WriteAtomically(KioskSteps.Program, Encoding.UTF8.GetString(older), Modes.Program);

        // A tarball release.json vouches for, with a program in it that is not the one it names.
        var tarball = FakeMachine.KioskTarball("4.0.0", program: FakeMachine.KioskProgram("4.0.9"));
        var release = JsonNode.Parse(FakeMachine.ReleaseJson())!;
        release["assets"]![0]!["size"] = tarball.LongLength;
        release["assets"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(tarball));
        pi.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = release.ToJsonString();
        pi.FileDownloads[TarballUri] = tarball;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"The kiosk's program in {FakeMachine.KioskAssetName("4.0.0")} is not the one release.json names: its SHA-256 differs.");
        File.ReadAllBytes(pi.OnDisk(KioskSteps.Program)).Should().Equal(older, "the program there is kept");
        pi.Exists(KioskSteps.PreviousProgram).Should().BeFalse();
        pi.Exists($"{KioskSteps.Program}.new").Should().BeFalse();
        pi.KioskStarts.Should().Be(1);
    }

    [TestMethod]
    public async Task WithoutApt_TheKioskIsRefused_BeforeAnythingChanges()
    {
        using var pi = KioskPi();
        pi.Packages = null;

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("the kiosk needs Raspberry Pi OS, which installs packages with apt: dpkg-query is not here");
        pi.Deploys.Should().BeEmpty();
        pi.Exists(KioskSteps.ProgramFolder).Should().BeFalse();
    }

    [TestMethod]
    public async Task WithTheCursorShown_TheCommandLineIsLeftAsItIs()
    {
        using var pi = KioskPi();
        var answers = WithTheController with { Kiosk = new KioskSettings { HideCursor = false } };

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(answers));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Read(CommandLine).Should().Be($"{Booted}\n");
        run.Output.Should().NotContain("sudo reboot");
    }

    [TestMethod]
    public async Task PeopleChosen_AreGivenAPin_FromAFile_AndSomeoneWithOneKeepsIt()
    {
        using var pi = InstalledKioskPi();
        pi.WithPerson("olga", RoofControllerApiContract.OperatorRole).WithPerson("ben", RoofControllerApiContract.AdminRole, pin: "864213");
        pi.Machine.WriteAtomically("/root/olga-pin", Pin + "\n", Modes.PrivateFile);
        var answers = pi.WriteAnswers(WithTheController with { Kiosk = new KioskSettings { Pins = ["olga", "ben"] } });

        var run = await pi.RunAsync("--answers", answers, "--pin-file", "olga=/root/olga-pin");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.PinRequests.Should().ContainSingle().Which.Should().Be(new FakePinRequest("olga", RoofControllerApiContract.OperatorRole, Pin, AdminKey(pi)));
        pi.PeoplesPins["olga"].Should().Be(Pin);
        pi.PeoplesPins["ben"].Should().Be("864213", "someone with a PIN keeps it: it is changed in the web UI");
        pi.Asked.Should().BeEmpty();
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Kiosk!.Pins.Should().BeEmpty("PINs are given once, and not recorded");
        ShowsNoSecret(pi, run, Pin);

        var second = await pi.RunAsync("--answers", answers);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        pi.PinRequests.Should().ContainSingle("olga has one now");
    }

    [TestMethod]
    public async Task APin_IsTyped_WhenNoFileGivesIt()
    {
        using var pi = InstalledKioskPi();
        pi.WithPerson("olga", RoofControllerApiContract.AdminRole);
        pi.Types = _ => Pin;
        var answers = pi.WriteAnswers(WithTheController with { Kiosk = new KioskSettings { Pins = ["Olga"] } });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Asked.Should().Equal("Olga's PIN", "Olga's PIN again");
        pi.PinRequests.Should().ContainSingle().Which.Should().Match<FakePinRequest>(request => request.Name == "olga" && request.Role == RoofControllerApiContract.AdminRole && request.Pin == Pin);
        ShowsNoSecret(pi, run, Pin);
    }

    [TestMethod]
    [DataRow("viewer", "viewer is a viewer, and only operators and admins sign in at the kiosk")]
    [DataRow("nobody", "nobody is not on the controller: add them on the web UI's People page")]
    public async Task APinForAViewer_OrSomeoneNotOnTheController_IsRefused_BeforeAnythingChanges(string name, string said)
    {
        using var pi = InstalledKioskPi();
        pi.WithPerson("viewer", RoofControllerApiContract.ViewerRole);
        pi.Types = _ => Pin;
        var answers = pi.WriteAnswers(WithTheController with { Kiosk = new KioskSettings { Pins = [name] } });

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain(said);
        pi.PinRequests.Should().BeEmpty();
        pi.Deploys.Should().BeEmpty();
        pi.Exists(KioskSteps.ProgramFolder).Should().BeFalse();
        pi.Asked.Should().BeEmpty("nothing is asked for an install that cannot go ahead");
    }

    [TestMethod]
    public async Task ThePlan_ShowsTheKiosk_AndChangesNothing()
    {
        using var pi = KioskPi();
        var files = pi.Snapshot();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(WithTheController), "--plan");

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain(KioskSteps.Program).And.Contain(KioskSteps.DeviceKeyFile).And.Contain(MachineSurveyor.KioskUnit)
            .And.Contain($"adds {KioskSteps.HideCursorSetting} to {CommandLine}; it takes effect at the next reboot");
        pi.Snapshot("/root/answers.json").Should().Equal(files);
        pi.AptInstalls.Should().BeEmpty();
        pi.Downloaded.Should().NotContain(uri => uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.Ordinal));
        pi.KioskStarts.Should().Be(0);
    }

    // The controller's Pi with its touchscreen, booted from Raspberry Pi OS's firmware partition.
    private static FakeMachine KioskPi() => new FakeMachine().WithPi().WithDisplay().Write(CommandLine, Booted + "\n");

    // The controller installed as the installer leaves it, with the touchscreen.
    private static FakeMachine InstalledKioskPi()
    {
        var pi = InstallerPlanTests.InstalledPi().WithDisplay().Write(CommandLine, Booted + "\n");
        return pi;
    }

    private static string AdminKey(FakeMachine pi) => pi.Read(ApiKeyFiles.KeyFile(Secrets, 1)).Trim();

    // The kiosk's settings, as the kiosk reads them from its folder.
    private static KioskOptions Options(FakeMachine pi)
        => global::HVO.RoofControllerV4.Kiosk.Program.ReadOptions(global::HVO.RoofControllerV4.Kiosk.Program.BuildConfiguration([], pi.OnDisk(KioskSteps.ProgramFolder)));

    private static string ServedFingerprint(FakeMachine pi) => ControllerCertificates.Fingerprint(pi.ServedCertificates[ControllerSettings.DefaultHttpsPort]);

    private static void ShowsNoSecret(FakeMachine pi, InstallerRun run, params string[] secrets)
    {
        var log = pi.Read(InstallPaths.SystemLog);
        foreach (var secret in secrets.Concat(pi.ApiKeyValues()))
        {
            run.ToString().Should().NotContain(secret);
            log.Should().NotContain(secret);
        }
    }
}
