using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The <c>~/.net</c> folders earlier releases' programs unpacked into, and the kiosk's own folders, after an uninstall on
/// the fake machine (#96).
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerExtractionFolderTests
{
    private const string KioskHome = "/home/hvo-kiosk";

    // What earlier releases left: the installed installer, the release asset run before it installed itself, hvo-roof and
    // the kiosk, in root's home, the home of the person who ran sudo, and the kiosk account's.
    private static readonly string[] Ours =
    [
        "/root/.net/hvo-roof-install",
        "/root/.net/hvo-roof-install-linux-arm64",
        "/root/.net/hvo-roof",
        "/home/pi/.net/hvo-roof-install",
        $"{KioskHome}/.net/hvo-roof-kiosk"
    ];

    // The controller and the kiosk installed with sudo by pi, with folders of ours and others' under each ~/.net.
    private static async Task<FakeMachine> PiWithLegacyFoldersAsync()
    {
        var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Environment["SUDO_USER"] = "pi";
        pi.UserHomes[KioskSteps.User] = KioskHome;
        foreach (var folder in Ours)
        {
            pi.Write($"{folder}/0123abcd/libonigwrap.so", "x");
        }

        pi.Write("/root/.net/someone-else/0123abcd/keep.so", "x")
            .Write("/root/.net/hvo-roofer/0123abcd/keep.so", "x");
        return pi;
    }

    [TestMethod]
    public async Task APurge_LeavesNoNetFolderOfOurs_InRootsTheSudoCallersOrTheKioskAccountsHome()
    {
        using var pi = await PiWithLegacyFoldersAsync();
        pi.Write($"{KioskSteps.StateFolder}/.net/hvo-roof-kiosk/0123abcd/libSkiaSharp.so", "x")
            .Write($"{KioskSteps.CacheFolder}/fontconfig/cache", "x");

        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");

        run.ExitCode.Should().Be(0, run.ToString());
        Ours.Where(pi.Exists).Should().BeEmpty();
        pi.Exists(KioskSteps.StateFolder).Should().BeFalse();
        pi.Exists(KioskSteps.CacheFolder).Should().BeFalse();

        // .net goes only when nothing else is left in it; others' folders stay, whatever their names begin with.
        pi.Exists("/home/pi/.net").Should().BeFalse();
        pi.Exists($"{KioskHome}/.net").Should().BeFalse();
        pi.Exists("/root/.net/someone-else/0123abcd/keep.so").Should().BeTrue();
        pi.Exists("/root/.net/hvo-roofer/0123abcd/keep.so").Should().BeTrue();
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnUninstall_WithoutPurge_RemovesThemToo_AndItsPlanSaysSo()
    {
        using var pi = await PiWithLegacyFoldersAsync();

        var plan = await pi.RunAsync("uninstall", "--plan");
        var run = await pi.RunAsync("uninstall", "--yes");

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.ToString().Should().Contain("~/.net/hvo-roof*").And.Contain("/home/pi/.net/hvo-roof-install").And.Contain($"{KioskHome}/.net/hvo-roof-kiosk");
        run.ExitCode.Should().Be(0, run.ToString());
        Ours.Where(pi.Exists).Should().BeEmpty();
        pi.Exists("/root/.net/someone-else/0123abcd/keep.so").Should().BeTrue();
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APurge_RunAsRootItself_LeavesNoNetFolderOfOurs_InAnyHome()
    {
        // No sudo to name the person who installed it, and a hvo-roof someone else on the machine ran (review F2).
        using var pi = await PiWithLegacyFoldersAsync();
        pi.Environment.Remove("SUDO_USER");
        pi.Users["astro"] = ["astro"];
        pi.UserHomes["astro"] = "/home/astro";
        pi.Write("/home/astro/.net/hvo-roof/0123abcd/libonigwrap.so", "x");

        var run = await pi.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "roofpi");

        run.ExitCode.Should().Be(0, run.ToString());
        Ours.Where(pi.Exists).Should().BeEmpty();
        pi.Exists("/home/astro/.net").Should().BeFalse();
        pi.Exists("/root/.net/someone-else/0123abcd/keep.so").Should().BeTrue();
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ABackup_IsRefused_InAFolderUninstallRemoves_BeforeAPurgeRemovesAnything()
    {
        // The purge's own backup, which the uninstall after it would have deleted (review F1).
        using var pi = await PiWithLegacyFoldersAsync();
        const string Inside = "/root/.net/hvo-roof-install";

        var purge = await pi.RunAsync("uninstall", "--purge", "--yes", "--backup", $"{Inside}/safety-copy.tar.gz", "--confirm", "roofpi");
        var backup = await pi.RunAsync("backup", "--output", "/home/astro/.net/hvo-roof-kiosk/b.tar.gz");

        purge.ExitCode.Should().Be((int)InstallerExitCode.Refused, purge.ToString());
        purge.Error.Should().Contain($"is in {Inside}, which uninstall removes");
        pi.Exists($"{Inside}/safety-copy.tar.gz").Should().BeFalse();
        Ours.Where(pi.Exists).Should().BeEquivalentTo(Ours);
        pi.Exists("/etc/hvo-roof/secrets").Should().BeTrue();
        pi.Exists("/var/lib/hvo-roof").Should().BeTrue();
        backup.ExitCode.Should().Be((int)InstallerExitCode.Refused, backup.ToString());
        backup.Error.Should().Contain("is in /home/astro/.net/hvo-roof-kiosk, which uninstall removes");
    }

    [TestMethod]
    public async Task ANetFolder_ThatIsALink_IsNotFollowed()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Environment["SUDO_USER"] = "pi";

        // ~/.net pointing at /etc: followed, /etc/hvo-roof would look like hvo-roof's folder.
        pi.Folder("/home/pi");
        Directory.CreateSymbolicLink(pi.OnDisk("/home/pi/.net"), pi.OnDisk("/etc"));

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Exists("/etc/hvo-roof/secrets").Should().BeTrue();
        new DirectoryInfo(pi.OnDisk("/home/pi/.net")).LinkTarget.Should().NotBeNull();
    }

    [TestMethod]
    public async Task AFolderOfOurs_ThatIsALink_OrHoldsOne_IsNotFollowed()
    {
        using var pi = await InstallerLifecycleTests.InstalledPiAsync();
        pi.Folder("/root/.net/hvo-roof-kiosk/0123abcd");
        Directory.CreateSymbolicLink(pi.OnDisk("/root/.net/hvo-roof-install"), pi.OnDisk("/etc/hvo-roof"));
        Directory.CreateSymbolicLink(pi.OnDisk("/root/.net/hvo-roof-kiosk/0123abcd/escape"), pi.OnDisk("/etc/hvo-roof"));

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Exists("/etc/hvo-roof/secrets").Should().BeTrue();
        pi.Exists("/root/.net/hvo-roof-kiosk").Should().BeFalse();
        new DirectoryInfo(pi.OnDisk("/root/.net/hvo-roof-install")).LinkTarget.Should().NotBeNull();
    }

    [TestMethod]
    public async Task APersonsUninstall_RemovesTheirOwnFolders_AndLeavesRootsAlone()
    {
        using var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");
        (await laptop.RunAsync("--answers", laptop.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Cli], Client = FakeMachine.ClientAnswers }))).ExitCode.Should().Be(0);
        laptop.Write("/home/roy/.net/hvo-roof/0123abcd/libonigwrap.so", "x")
            .Write("/home/roy/.net/hvo-roof-install/0123abcd/libonigwrap.so", "x")
            .Write("/root/.net/hvo-roof-install/0123abcd/libonigwrap.so", "x");

        var run = await laptop.RunAsync("uninstall", "--purge", "--yes", "--no-backup", "--confirm", "laptop");

        run.ExitCode.Should().Be(0, run.ToString());
        laptop.Exists("/home/roy/.net").Should().BeFalse();
        laptop.Exists("/root/.net/hvo-roof-install/0123abcd/libonigwrap.so").Should().BeTrue();
        laptop.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public void OnlyOurProgramsFolders_AreOurs()
    {
        LegacyExtractionStep.IsOurs("hvo-roof").Should().BeTrue();
        LegacyExtractionStep.IsOurs("hvo-roof-install-linux-arm64").Should().BeTrue();
        LegacyExtractionStep.IsOurs("hvo-roof-kiosk").Should().BeTrue();
        LegacyExtractionStep.IsOurs("hvo-roofer").Should().BeFalse();
        LegacyExtractionStep.IsOurs("hvo-sky").Should().BeFalse();
        LegacyExtractionStep.IsOurs("HVO-ROOF").Should().BeFalse();
    }

    [TestMethod]
    public void APath_IsInAFolderOfOurs_OnlyUnderADotNet()
    {
        LegacyExtractionStep.FolderHolding("/root/.net/hvo-roof-install/b.tar.gz").Should().Be("/root/.net/hvo-roof-install");
        LegacyExtractionStep.FolderHolding("/home/pi/.net/hvo-roof").Should().Be("/home/pi/.net/hvo-roof");
        LegacyExtractionStep.FolderHolding("/srv/.net/hvo-roof-kiosk/0123abcd/b.tar.gz").Should().Be("/srv/.net/hvo-roof-kiosk");
        LegacyExtractionStep.FolderHolding("/root/.net/hvo-roofer/b.tar.gz").Should().BeNull();
        LegacyExtractionStep.FolderHolding("/root/.net/b.tar.gz").Should().BeNull();
        LegacyExtractionStep.FolderHolding("/root/hvo-roof/b.tar.gz").Should().BeNull();
        LegacyExtractionStep.FolderHolding("/var/backups/hvo-roof/hvo-roof-pi.tar.gz").Should().BeNull();
    }
}
