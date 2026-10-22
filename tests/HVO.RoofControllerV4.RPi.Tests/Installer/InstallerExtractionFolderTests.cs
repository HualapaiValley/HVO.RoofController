using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>The <c>~/.net</c> folders earlier releases left, and the kiosk's folders that uninstall takes, on the fake machine.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerExtractionFolderTests
{
    private static InstallRecord KioskRecord() => new()
    {
        Scope = InstallScope.System,
        Roles = [InstallRole.Kiosk],
        Version = "4.0.0",
        InstallerVersion = "4.0.0",
        Kiosk = new KioskSettings()
    };

    [TestMethod]
    public async Task Uninstall_RemovesOurLegacyExtractionFolders_InRootsHome_AndNoOthers()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write("/root/.net/hvo-roof-install/abc/libonigwrap.so", "x")
            .Write("/root/.net/hvo-roof-install-linux-arm64/abc/libonigwrap.so", "x")
            .Write("/root/.net/hvo-roof-kiosk/abc/libSkiaSharp.so", "x")
            .Write("/root/.net/hvo-roof/abc/libonigwrap.so", "x")
            .Write("/root/.net/someone-else/keep.so", "x");

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);
        var targets = PlanBuilder.BuildUninstall(pi.Machine, survey, KioskRecord(), purge: false).Steps
            .OfType<RemovalStep>().Select(step => step.Target).ToList();

        targets.Should().Contain(["/root/.net/hvo-roof", "/root/.net/hvo-roof-install", "/root/.net/hvo-roof-install-linux-arm64", "/root/.net/hvo-roof-kiosk"]);
        targets.Should().NotContain("/root/.net/someone-else");
        pi.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    public void Purge_RemovesTheKioskStateAndCache()
    {
        using var pi = new FakeMachine().WithPi();

        var purged = PlanBuilder.PurgedFolders(pi.Machine, KioskRecord(), ControllerLayout.System).Select(folder => folder.Folder).ToList();

        purged.Should().Contain([KioskSteps.StateFolder, KioskSteps.CacheFolder]);
        purged.Should().NotContain(folder => folder.Contains("/.net", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Uninstall_AsPerson_RemovesTheirOwnLegacyFolders_NotRoots()
    {
        using var pi = new FakeMachine().WithPi().AsPerson();
        var home = pi.Machine.Home;
        pi.Write(Path.Join(home, ".net", "hvo-roof-install", "abc", "libonigwrap.so"), "x")
            .Write("/root/.net/hvo-roof-install/abc/libonigwrap.so", "x");
        var record = new InstallRecord { Scope = InstallScope.User, Roles = [], Version = "4.0.0", InstallerVersion = "4.0.0" };

        var survey = await MachineSurveyor.SurveyAsync(pi.Machine);
        var targets = PlanBuilder.BuildUninstall(pi.Machine, survey, record, purge: false).Steps
            .OfType<RemovalStep>().Select(step => step.Target).ToList();

        targets.Should().Contain(Path.Join(home, ".net", "hvo-roof-install"));
        targets.Should().NotContain("/root/.net/hvo-roof-install");
        pi.Unexpected.Should().BeEmpty();
    }
}
