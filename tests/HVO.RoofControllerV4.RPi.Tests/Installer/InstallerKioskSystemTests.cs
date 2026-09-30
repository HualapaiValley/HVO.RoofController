using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The kiosk's install steps (#70) on a real Linux machine, as root: apt, the user and its groups, the files with their
/// modes and owners, the unit and the udev rule, systemd starting the release's kiosk program as hvo-kiosk, and a second
/// run that changes nothing. CI's installer-kiosk job runs it on an arm64 runner, with <c>HVO_KIOSK_INSTALL=1</c> and
/// <c>HVO_KIOSK_PROGRAM</c>, the program published for the Pi; anywhere else it is skipped. The
/// controller's side (its container, its kiosk key's redeploy) and the Pi's screen are not here: the fake machine's tests
/// cover the first, and the second stays an assumption (docs/kiosk.md).
/// </summary>
[TestClass]
[TestCategory("KioskInstall")]
[SupportedOSPlatform("linux")]
public sealed class InstallerKioskSystemTests
{
    private const string Version = "4.0.0";

    // The folders and files this test makes, and removes when it ends.
    private static readonly string[] Made = ["/etc/hvo-roof", "/opt/hvo-roof-kiosk", "/etc/hvo-roof-kiosk"];

    [TestMethod]
    public async Task TheKiosk_IsInstalled_AndStartedBySystemd_AndASecondRunChangesNothing()
    {
        var program = Environment.GetEnvironmentVariable("HVO_KIOSK_PROGRAM");
        if (Environment.GetEnvironmentVariable("HVO_KIOSK_INSTALL") != "1" || string.IsNullOrEmpty(program))
        {
            Assert.Inconclusive("Installs the kiosk on this machine: set HVO_KIOSK_INSTALL=1 and HVO_KIOSK_PROGRAM, as CI's installer-kiosk job does.");
        }

        var machine = InstallerMachine.Current();
        machine.IsRoot.Should().BeTrue("the kiosk is installed as root");
        File.Exists("/dev/gpiomem").Should().BeFalse("this test never runs on a Raspberry Pi");
        Made.Should().NotContain(path => Directory.Exists(path), "this test runs only where there is no controller or kiosk");

        var work = Directory.CreateTempSubdirectory("hvo-kiosk-install-");
        try
        {
            var release = MakeRelease(work.FullName, File.ReadAllBytes(program!));
            var layout = ControllerLayout.For(machine);
            var controller = new ControllerSettings();
            var kiosk = new KioskSettings();
            var kioskKey = new ApiKeyAllocation(ApiKeyUse.Kiosk, 3, ApiKeyFiles.KioskName, RoofControllerApiContract.ViewerRole, Kiosk: true, Local: true, Reused: false);
            var adminKey = new ApiKeyAllocation(ApiKeyUse.Admin, 1, "installer-admin", RoofControllerApiContract.AdminRole, Kiosk: false, Local: false, Reused: false);
            WriteAuthority(layout.CaCertificate);
            var commandLine = KioskSteps.CommandLineFiles[0];
            if (!File.Exists(commandLine))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(commandLine)!);
                File.WriteAllText(commandLine, "console=tty1 root=PARTUUID=0a1b2c3d-02 rootfstype=ext4 fsck.repair=yes rootwait\n");
            }

            var plan = new InstallPlan(
            [
                new FolderStep(layout.Secrets, Modes.PrivateFolder, "secrets the controller reads, one file per setting"),
                new ApiKeyStep(layout, kioskKey),
                .. KioskSteps.For(layout, controller, kiosk, kioskKey, adminKey, firstAdmin: null)
            ]);
            var log = InstallLog.Open(machine, Path.Join(work.FullName, "install.log"), TimeProvider.System);

            // Each run of the installer starts with the machine as it finds it.
            async Task<InstallContext> Fresh() => new()
            {
                Machine = machine,
                Log = log,
                Survey = await MachineSurveyor.SurveyAsync(machine),
                Answers = new InstallAnswers { Roles = [InstallRole.Controller, InstallRole.Kiosk], Controller = controller, Kiosk = kiosk },
                Version = Version,
                Release = ReleaseSource.Folder(release)
            };

            var context = await Fresh();
            var checkedPlan = await plan.CheckAsync(context);
            checkedPlan.Steps.Should().NotContain(step => step.Check.Change == StepChange.Blocked, "{0}", string.Join("; ", checkedPlan.Steps.Select(step => $"{step.Step.Target}: {step.Check.Detail}")));
            await checkedPlan.ApplyAsync(context);

            Run("dpkg-query", ["-W", "-f", "${db:Status-Status} ", .. KioskSteps.Packages]).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Should().HaveCount(KioskSteps.Packages.Count).And.OnlyContain(status => status == "installed");
            Run("id", "-nG", KioskSteps.User).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().Contain(KioskSteps.Groups);
            Stat(KioskSteps.Program).Should().Be($"755 root:root {new FileInfo(program!).Length}");
            File.ReadAllBytes(KioskSteps.Program).Should().Equal(File.ReadAllBytes(program!), "the program is the release's");
            Stat(KioskSteps.DeviceKeyFile).Should().StartWith($"400 {KioskSteps.User}:{KioskSteps.User} ");
            File.ReadAllText(KioskSteps.DeviceKeyFile).Should().Be(File.ReadAllText(ApiKeyFiles.KeyFile(layout.Secrets, kioskKey.Index)).Trim());
            Stat(KioskSteps.ConfigurationFolder).Should().StartWith($"750 root:{KioskSteps.User} ");
            Stat(KioskSteps.SettingsFile).Should().StartWith("644 root:root ");
            var settings = JsonNode.Parse(File.ReadAllText(KioskSteps.SettingsFile))!["Kiosk"]!;
            settings["ControllerUrl"]!.GetValue<string>().Should().Be("https://localhost:8443/");
            settings["ServerCaCertificateFile"]!.GetValue<string>().Should().Be(layout.CaCertificate);
            Run("systemctl", "is-enabled", MachineSurveyor.KioskUnit).Trim().Should().Be("enabled");
            File.ReadAllText(commandLine).Should().Contain(KioskSteps.HideCursorSetting);

            // systemd runs it as hvo-kiosk, and it reads its settings, its key and the CA before it looks for the screen,
            // which this machine does not have: it logs where it draws, then fails there, and systemd starts it again.
            var started = await Eventually(() => Journal().Contains("Roof kiosk on ", StringComparison.Ordinal), TimeSpan.FromSeconds(90));
            started.Should().BeTrue($"the kiosk logs where it draws once its settings, key and CA are read. Its journal:\n{Journal()}");
            Journal().Should().NotContain("did not start").And.NotContain(File.ReadAllText(KioskSteps.DeviceKeyFile));
            Run("systemctl", "show", "-p", "User", "--value", MachineSurveyor.KioskUnit).Trim().Should().Be(KioskSteps.User);

            var again = await plan.CheckAsync(await Fresh());
            again.Steps.Should().OnlyContain(step => !step.Check.MakesChange, "{0}", string.Join("; ", again.Steps.Where(step => step.Check.MakesChange).Select(step => $"{step.Step.Target}: {step.Check.Detail}")));
        }
        finally
        {
            Run("systemctl", "disable", "--now", MachineSurveyor.KioskUnit);
            foreach (var file in new[] { MachineSurveyor.KioskUnitFile, KioskSteps.BacklightRuleFile })
            {
                File.Delete(file);
            }

            foreach (var folder in Made)
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }

            Run("systemctl", "daemon-reload");
            Run("userdel", KioskSteps.User);
            work.Delete(recursive: true);
        }
    }

    // The release as a folder, as --release takes one: release.json, and the kiosk's tarball with the program given.
    private static string MakeRelease(string work, byte[] program)
    {
        var folder = Path.Join(work, "release");
        Directory.CreateDirectory(folder);
        var tarball = FakeMachine.KioskTarball(Version, program);
        var name = FakeMachine.KioskAssetName(Version);
        File.WriteAllBytes(Path.Join(folder, name), tarball);
        var json = JsonNode.Parse(FakeMachine.ReleaseJson(Version))!;
        var asset = json["assets"]![0]!;
        asset["size"] = tarball.LongLength;
        asset["sha256"] = Convert.ToHexStringLower(SHA256.HashData(tarball));
        asset["files"]!["hvo-roof-kiosk"] = Convert.ToHexStringLower(SHA256.HashData(program));
        File.WriteAllText(Path.Join(folder, ReleaseManifest.FileName), json.ToJsonString());
        return folder;
    }

    // A CA's certificate where the installer keeps it: the kiosk trusts it, and needs no key.
    private static void WriteAuthority(string path)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HVO roof kiosk test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, certificate.ExportCertificatePem() + "\n");
    }

    private static string Journal() => Run("journalctl", "-u", MachineSurveyor.KioskUnit, "--no-pager", "-o", "cat");

    private static string Stat(string path) => Run("stat", "-c", "%a %U:%G %s", path).Trim();

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return true;
    }

    private static string Run(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
