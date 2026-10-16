using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// A release installed from its folder with no registry (#127): each image loaded from the release's offline archive
/// for this machine, checked against release.json and by the ID Docker gives it, run by that ID and never pulled; the
/// archives in release.json; and a release from GitHub, or from the folder of a release from before offline archives,
/// pulled by its digest as before. A controller loaded from an archive is put back by a rollback as any other is.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerOfflineImageTests
{
    private static readonly string ControllerReference = $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{FakeMachine.ControllerDigest}";
    private static readonly string EmulatorReference = $"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{FakeMachine.EmulatorDigest}";

    [TestMethod]
    public void AReleaseJson_GivesEachImagesOfflineArchives_AndTheIdsDockerMayGiveThem()
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(offlineArchives: true), "4.0.0");

        var name = FakeMachine.OfflineArchiveName("roof-controller", "arm64");
        var archive = release.Controller.ArchiveFor("linux/arm64");
        archive.Should().Be(new OfflineArchive(
            name,
            "linux/arm64",
            FakeMachine.OfflineArchiveContent(name).LongLength,
            Convert.ToHexStringLower(SHA256.HashData(FakeMachine.OfflineArchiveContent(name))),
            FakeMachine.ArchiveManifestDigest(name),
            FakeMachine.ArchiveConfigDigest(name)));
        release.Controller.LoadedIds(archive!).Should().Equal(FakeMachine.ControllerDigest, FakeMachine.ArchiveManifestDigest(name), FakeMachine.ArchiveConfigDigest(name));
        release.HatEmulator.OfflineArchives.Select(offline => offline.Name).Should()
            .Equal(FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64"), FakeMachine.OfflineArchiveName("roof-hat-emulator", "arm64"));
        release.Assets.Where(asset => asset.Kind == OfflineArchive.AssetKind).Should().HaveCount(4, "every asset is in assets, the archives too");
    }

    [TestMethod]
    public void AReleaseJson_FromBeforeOfflineArchives_HasNone()
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0");

        release.Controller.OfflineArchives.Should().BeEmpty();
        release.HatEmulator.ArchiveFor("linux/arm64").Should().BeNull();
    }

    [TestMethod]
    [DataRow("name", "roof-controller-4.0.0-linux-arm64.tar", "the controller image's offline archive 2 has no name of an OCI archive (<image>-<version>-linux-<arch>.oci.tar)")]
    [DataRow("name", "../roof-controller-4.0.0-linux-arm64.oci.tar", "the controller image's offline archive 2 has no name of an OCI archive (<image>-<version>-linux-<arch>.oci.tar)")]
    [DataRow("platform", "linux/arm/v7", "roof-controller-4.0.0-linux-arm64.oci.tar's platform is not linux/amd64 or linux/arm64")]
    [DataRow("platform", "linux/amd64", "the controller image has more than one offline archive for a platform")]
    [DataRow("size", 0, "roof-controller-4.0.0-linux-arm64.oci.tar's size is not a number of bytes")]
    [DataRow("size", "12", "roof-controller-4.0.0-linux-arm64.oci.tar's size is not a number of bytes")]
    [DataRow("sha256", "ABC", "roof-controller-4.0.0-linux-arm64.oci.tar's sha256 is not 64 lower-case hex digits")]
    [DataRow("manifestDigest", "sha256:abc", "roof-controller-4.0.0-linux-arm64.oci.tar's manifestDigest and configDigest are not each sha256:<64 hex digits>")]
    [DataRow("configDigest", null, "roof-controller-4.0.0-linux-arm64.oci.tar's manifestDigest and configDigest are not each sha256:<64 hex digits>")]
    public void AnOfflineArchive_ThatIsNotOne_IsRefused(string field, object? value, string reason)
    {
        var json = JsonNode.Parse(FakeMachine.ReleaseJson(offlineArchives: true))!;
        var archive = json["images"]!["controller"]!["offlineArchives"]![1]!.AsObject();
        archive.Remove(field);
        if (value is not null)
        {
            archive[field] = value is int number ? JsonValue.Create(number) : JsonValue.Create((string)value);
        }

        var parse = () => ReleaseManifest.Parse(json.ToJsonString(), "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage($"The installer will not use release.json: {reason}.");
    }

    [TestMethod]
    public void OfflineArchives_ThatAreNotAList_AreRefused()
    {
        var json = JsonNode.Parse(FakeMachine.ReleaseJson(offlineArchives: true))!;
        json["images"]!["hatEmulator"]!["offlineArchives"] = "roof-hat-emulator-4.0.0-linux-arm64.oci.tar";

        var parse = () => ReleaseManifest.Parse(json.ToJsonString(), "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage("The installer will not use release.json: the hatEmulator image's offline archives are not a list.");
    }

    [TestMethod]
    public void AContainerRunByItsImagesId_RunsTheReleasesImageItsLabelNames()
    {
        var digest = "sha256:" + new string('d', 64);
        var other = "sha256:" + new string('b', 64);
        ContainerSurvey Survey(string image, string? released) => new()
        {
            Name = MachineSurveyor.ControllerContainer,
            Image = image,
            State = "running",
            Origin = ContainerOrigin.DeployScript,
            ReleaseImage = released
        };

        Survey("sha256:" + new string('a', 64), $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{digest}").ImageDigest.Should().Be(digest);
        Survey($"ghcr.io/hualapaivalley/roof-controller:4.0.0@{other}", $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{digest}").ImageDigest
            .Should().Be(other, "the image it was run by comes first");
        Survey("sha256:" + new string('a', 64), null).ImageDigest.Should().BeNull();
    }

    [TestMethod]
    public async Task ARig_FromAReleaseFolder_LoadsBothImagesFromTheirArchives_RunsThemByTheirIds_AndPullsNothing()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var answers = bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] });
        var emulatorArchive = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");
        var controllerArchive = FakeMachine.OfflineArchiveName("roof-controller", "amd64");

        var plan = await bench.RunAsync("--plan", "--answers", answers, "--release", folder);

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain($"release 4.0.0's image from {emulatorArchive}, on the hvo-emulator network")
            .And.Contain($"release 4.0.0 from {controllerArchive}, through the deploy script");
        bench.LoadedArchives.Should().BeEmpty("a plan loads nothing");

        var run = await bench.RunAsync("--answers", answers, "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Ran.Should().NotContain(command => command.Program == "docker" && command.Arguments.Count > 0 && command.Arguments[0] == "pull", "nothing is pulled");
        bench.LoadedArchives.Should().Equal($"{folder}/{emulatorArchive}", $"{folder}/{controllerArchive}");
        var emulatorId = FakeMachine.ArchiveConfigDigest(emulatorArchive);
        bench.Ran.Should().Contain(command => command.Program == "docker" && command.Arguments.Count > 0 && command.Arguments[0] == "run"
            && command.Arguments[command.Arguments.Count - 1] == emulatorId
            && command.Arguments.SkipWhile(argument => argument != "--pull").Take(2).SequenceEqual(new[] { "--pull", "never" })
            && command.Arguments.Contains($"{MachineSurveyor.ReleaseImageLabel}={EmulatorReference}"));
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Should().Match<FakeContainer>(emulator => emulator.ImageId == emulatorId && emulator.Digest == FakeMachine.EmulatorDigest);
        bench.Deploys.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["IMAGE_REF"] = ControllerReference,
            ["IMAGE_ARCHIVE"] = $"{folder}/{controllerArchive}",
            ["BUILD_PLATFORM"] = "linux/amd64"
        });
        bench.Containers[MachineSurveyor.ControllerContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(controllerArchive));
        bench.Read(InstallPaths.SystemLog).Should().Contain($"Loaded the HAT emulator's image from {folder}/{emulatorArchive}: the image {emulatorId}, release 4.0.0's {EmulatorReference}. Nothing was pulled.");
        bench.Downloaded.Should().BeEmpty("the release's files come from its folder");
        bench.Unexpected.Should().BeEmpty();

        var second = await bench.RunAsync("--answers", answers, "--release", folder);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        bench.Deploys.Should().ContainSingle("a container run by its image's ID runs the release's image its label names");
        bench.LoadedArchives.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ThePi_FromAReleaseFolder_GivesTheDeployScriptItsOwnArchive()
    {
        using var pi = new FakeMachine().WithPi();
        var folder = pi.ReleaseFolder();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }), "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["IMAGE_REF"] = ControllerReference,
            ["IMAGE_ARCHIVE"] = $"{folder}/{FakeMachine.OfflineArchiveName("roof-controller", "arm64")}",
            ["BUILD_PLATFORM"] = "linux/arm64"
        });
        pi.LoadedArchives.Should().ContainSingle("the controller's image alone, which the deploy script loads");
        pi.Containers.Should().NotContainKey(MachineSurveyor.HatEmulatorContainer);
    }

    [TestMethod]
    public async Task InTheContainerdImageStore_TheLoadedImageIsTheReleasesIndex()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench") { ImageStore = "containerd" };
        var folder = bench.ReleaseFolder();

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Containers[MachineSurveyor.HatEmulatorContainer].ImageId.Should().Be(FakeMachine.EmulatorDigest);
    }

    [TestMethod]
    public async Task AReleaseFolder_WithoutTheArchiveThisMachineNeeds_IsRefused_AndNothingChanges()
    {
        using var pi = new FakeMachine().WithPi();
        var folder = pi.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-controller", "arm64");
        File.Delete(pi.OnDisk($"{folder}/{name}"));
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });
        var files = pi.Snapshot(InstallPaths.SystemLog);

        var run = await pi.RunAsync("--answers", answers, "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.ToString().Should().Contain(
            $"{folder} has no {name}, the controller's image for linux/arm64, which release 4.0.0 lists: download it with the release's other files from {ReleaseManifest.PageUri("4.0.0")} into {folder}, then run the installer again");
        pi.Deploys.Should().BeEmpty();
        pi.LoadedArchives.Should().BeEmpty();
        pi.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
    }

    [TestMethod]
    public async Task AnArchiveOfAnotherSize_IsRefusedBeforeAnythingChanges()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");
        File.WriteAllText(bench.OnDisk($"{folder}/{name}"), "cut short");

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.ToString().Should().Contain($"{folder}'s {name} is not release 4.0.0's: it holds 9 bytes, and release.json says {FakeMachine.OfflineArchiveContent(name).Length}.");
        bench.LoadedArchives.Should().BeEmpty();
        bench.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnArchiveThatIsNotTheReleases_IsNeverLoaded()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");
        var content = FakeMachine.OfflineArchiveContent(name);
        content[0] ^= 1;
        File.WriteAllBytes(bench.OnDisk($"{folder}/{name}"), content);

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.ToString().Should().Contain($"{folder}/{name} is not the release's {name}: its size or SHA-256 differs from release.json's.");
        bench.LoadedArchives.Should().BeEmpty();
        bench.Containers.Should().NotContainKey(MachineSurveyor.HatEmulatorContainer);
        bench.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnImageDockerGivesAnotherId_IsNeverRun()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench") { ImageStore = "unknown" };
        var folder = bench.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.ToString().Should().Contain($"Docker loaded {folder}/{name}, and holds no image with an ID release.json gives the HAT emulator's image: {FakeMachine.EmulatorDigest}, ");
        bench.Containers.Should().NotContainKey(MachineSurveyor.HatEmulatorContainer);
        bench.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AReleaseFromGitHub_IsPulled_ThoughItListsOfflineArchives()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        bench.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = FakeMachine.ReleaseJson(offlineArchives: true);

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }));

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Ran.Should().Contain(command => command.Program == "docker" && command.Arguments.SequenceEqual(new[] { "pull", "--platform", "linux/amd64", EmulatorReference }));
        bench.LoadedArchives.Should().BeEmpty();
        bench.Deploys.Should().ContainSingle().Which["IMAGE_ARCHIVE"].Should().BeEmpty();
        bench.Containers[MachineSurveyor.HatEmulatorContainer].ImageId.Should().BeNull();
    }

    [TestMethod]
    public async Task AReleaseFolder_OfAReleaseWithoutOfflineArchives_IsPulledAsBefore()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder(offlineArchives: false);

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Ran.Should().Contain(command => command.Program == "docker" && command.Arguments.SequenceEqual(new[] { "pull", "--platform", "linux/amd64", EmulatorReference }));
        bench.LoadedArchives.Should().BeEmpty();
        bench.Deploys.Should().ContainSingle().Which["IMAGE_ARCHIVE"].Should().BeEmpty();
    }

    [TestMethod]
    public async Task ARollback_PutsBackAControllerLoadedFromAnArchive()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        (await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder)).ExitCode.Should().Be(0);
        bench.WithRelease("4.0.1", latest: true);
        var upgrade = await bench.RunAsync("upgrade");
        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        bench.Containers[ControllerRollbackStep.PreviousContainer].ImageId.Should().NotBeNull("the controller kept is the one loaded from the archive");

        bench.InstallerVersion = "4.0.1";
        var rollback = await bench.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        bench.DeployOptions[^1].Should().Be("--rollback");
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Match<FakeContainer>(
            controller => controller.Version == "4.0.0" && controller.ImageId == FakeMachine.ArchiveConfigDigest(FakeMachine.OfflineArchiveName("roof-controller", "amd64")) && controller.State == "running");
        bench.Unexpected.Should().BeEmpty();
    }
}
