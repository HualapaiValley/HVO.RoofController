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
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// A release installed from its folder with no registry (#127): each image loaded from the release's offline archive
/// for this machine, checked against release.json and by the ID Docker gives it, run by that ID and never pulled; the
/// archives in release.json; and a release from GitHub, or from the folder of a release from before offline archives,
/// pulled by its digest as before. A controller loaded from an archive is put back by a rollback as any other is. The
/// release-image label only claims the release: a container of an image with another ID that carries it is replaced,
/// never adopted, and never started by a rollback.
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
    public void AContainerRunByItsImagesId_RunsTheReleasesImage_OnlyWithAnIdTheReleaseGivesItsArchive()
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(offlineArchives: true), "4.0.0");
        var arm64 = release.Controller.ArchiveFor("linux/arm64")!;
        var amd64Config = FakeMachine.ArchiveConfigDigest(FakeMachine.OfflineArchiveName("roof-controller", "amd64"));
        var unrelated = "sha256:" + new string('a', 64);
        var other = "sha256:" + new string('b', 64);
        ContainerSurvey Survey(string image, string? imageId, string? released) => new()
        {
            Name = MachineSurveyor.ControllerContainer,
            Image = image,
            ImageId = imageId,
            State = "running",
            Origin = ContainerOrigin.DeployScript,
            ReleaseImage = released
        };

        foreach (var id in release.Controller.LoadedIds(arm64))
        {
            var loaded = Survey(id, id, ControllerReference);
            loaded.Runs(release.Controller, "linux/arm64").Should().BeTrue($"{id} is an ID the release gives its arm64 archive");
            loaded.FalseClaim(release.Controller, "linux/arm64").Should().BeNull();
        }

        // The label only claims the release's image: an image with another ID carrying it does not run it.
        var mislabelled = Survey(unrelated, unrelated, ControllerReference);
        mislabelled.Runs(release.Controller, "linux/arm64").Should().BeFalse("Docker's ID for the image it runs is not one the release gives it");
        mislabelled.ImageDigest.Should().BeNull("the label is not a digest it was deployed by");
        mislabelled.FalseClaim(release.Controller, "linux/arm64").Should().Be(
            $"its label names {ControllerReference}, but it runs the image {unrelated}, not one with an ID release.json gives that image for linux/arm64");
        Survey(amd64Config, amd64Config, ControllerReference).Runs(release.Controller, "linux/arm64").Should().BeFalse("the amd64 image is not the Pi's");
        Survey(unrelated, null, ControllerReference).Runs(release.Controller, "linux/arm64").Should().BeFalse("Docker gave no ID to check");
        Survey(arm64.ConfigDigest, arm64.ConfigDigest, ControllerReference).Runs(ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0").Controller, "linux/arm64")
            .Should().BeFalse("a release without offline archives gives no ID to check the label against");

        // With no label, or one naming another image, nothing claims the release.
        Survey(arm64.ConfigDigest, arm64.ConfigDigest, null).Runs(release.Controller, "linux/arm64").Should().BeFalse();
        var another = Survey(arm64.ConfigDigest, arm64.ConfigDigest, $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{other}");
        another.Runs(release.Controller, "linux/arm64").Should().BeFalse();
        another.FalseClaim(release.Controller, "linux/arm64").Should().BeNull("its label does not name the release's image");

        // Deployed by its digest, the digest decides, whatever the label says.
        Survey(ControllerReference, unrelated, null).Runs(release.Controller, "linux/arm64").Should().BeTrue();
        var byOther = Survey($"ghcr.io/hualapaivalley/roof-controller:4.0.0@{other}", arm64.ConfigDigest, ControllerReference);
        byOther.ImageDigest.Should().Be(other);
        byOther.Runs(release.Controller, "linux/arm64").Should().BeFalse("the image it was run by comes first");
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
        // The installer loads its own checked copy of the emulator's archive; the deploy script is given the controller's.
        bench.LoadedArchives.Should().HaveCount(2);
        bench.LoadedArchives[0].Should().StartWith($"{bench.TemporaryDirectory}/hvo-roof-install-").And.EndWith($"/{emulatorArchive}");
        bench.LoadedArchives[1].Should().Be($"{folder}/{controllerArchive}");
        bench.LoadedContents.Select(Convert.ToHexString).Should().Equal(
            Convert.ToHexString(FakeMachine.OfflineArchiveContent(emulatorArchive)), Convert.ToHexString(FakeMachine.OfflineArchiveContent(controllerArchive)));
        bench.Exists(Path.GetDirectoryName(bench.LoadedArchives[0])!).Should().BeFalse("the installer's copy goes once Docker has loaded it");
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
            ["IMAGE_ARCHIVE_SHA256"] = Sha256(FakeMachine.OfflineArchiveContent(controllerArchive)),
            ["BUILD_PLATFORM"] = "linux/amd64"
        });
        bench.Deploys[0]["TMPDIR"].Should().StartWith($"{bench.TemporaryDirectory}/hvo-roof-install-", "the script's copy goes in the installer's own folder");
        bench.Containers[MachineSurveyor.ControllerContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(controllerArchive));
        bench.Read(InstallPaths.SystemLog).Should().Contain($"Loaded the HAT emulator's image from {folder}/{emulatorArchive}, through a copy checked against release.json: the image {emulatorId}, release 4.0.0's {EmulatorReference}. Nothing was pulled.");
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
            ["IMAGE_ARCHIVE_SHA256"] = Sha256(FakeMachine.OfflineArchiveContent(FakeMachine.OfflineArchiveName("roof-controller", "arm64"))),
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
    [DataRow(true, DisplayName = "replaced")]
    [DataRow(false, DisplayName = "written over in place")]
    public async Task TheEmulatorsArchive_ChangedOnceItIsChecked_IsNeverWhatDockerLoads(bool replaced)
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");
        var other = FakeMachine.OfflineArchiveContent(FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64", "6.6.6"));
        UnixFileMode? copyMode = null, folderMode = null;

        // Someone who can write to the release's folder changes the archive once the installer has checked it, just
        // before Docker reads it.
        bench.BeforeLoad = path =>
        {
            if (Path.GetFileName(path) != name || copyMode is not null)
            {
                return;
            }

            copyMode = bench.Mode(path);
            folderMode = bench.Mode(Path.GetDirectoryName(path)!);
            if (replaced)
            {
                File.WriteAllBytes(bench.OnDisk($"{folder}/{name}.new"), other);
                File.Move(bench.OnDisk($"{folder}/{name}.new"), bench.OnDisk($"{folder}/{name}"), overwrite: true);
            }
            else
            {
                using var stream = new FileStream(bench.OnDisk($"{folder}/{name}"), FileMode.Open, FileAccess.Write);
                stream.SetLength(0);
                stream.Write(other);
            }
        };

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        File.ReadAllBytes(bench.OnDisk($"{folder}/{name}")).Should().Equal(other, "the archive in the release's folder was changed before the load");
        bench.LoadedArchives[0].Should().NotBe($"{folder}/{name}", "Docker reads the installer's copy, never the release's folder");
        bench.LoadedContents[0].Should().Equal(FakeMachine.OfflineArchiveContent(name), "Docker loads the bytes checked against release.json");
        copyMode.Should().Be(Modes.PrivateFile, "only root reads the copy");
        folderMode.Should().Be(Modes.PrivateFolder, "only root opens the copy's folder");
        bench.Exists(Path.GetDirectoryName(bench.LoadedArchives[0])!).Should().BeFalse("the copy goes once Docker has loaded it");
        bench.Containers[MachineSurveyor.HatEmulatorContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(name));
    }

    [TestMethod]
    public async Task TheEmulatorsCopy_GoesWhenDockerCannotLoadIt()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        string? copy = null;
        bench.BeforeLoad = path =>
        {
            copy = path;
            File.Delete(bench.OnDisk(path));
        };

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.ToString().Should().Contain($"Could not load the HAT emulator's image from {folder}/{FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64")}");
        copy.Should().StartWith($"{bench.TemporaryDirectory}/hvo-roof-install-");
        bench.Exists(Path.GetDirectoryName(copy)!).Should().BeFalse("the installer's folder, and any copy in it, goes whatever happens");
        bench.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheControllersArchive_ChangedOnceTheInstallerChecksIt_IsRefusedByTheDeployScript()
    {
        using var pi = new FakeMachine().WithPi();
        var folder = pi.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-controller", "arm64");
        var released = FakeMachine.OfflineArchiveContent(name);
        var other = released.ToArray();
        other[0] ^= 1;
        UnixFileMode? temporaryMode = null;

        // Once the installer has checked the archive, and before the deploy script copies it, the archive changes: of the
        // same size, so only its SHA-256 tells it from the release's.
        pi.DuringDeploy = () =>
        {
            temporaryMode = pi.Mode(pi.Deploys[^1]["TMPDIR"]);
            File.WriteAllBytes(pi.OnDisk($"{folder}/{name}"), other);
        };

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.ToString().Should().Contain($"IMAGE_ARCHIVE {folder}/{name} is not the release's archive: its SHA-256 is {Sha256(other)}, and IMAGE_ARCHIVE_SHA256 (release.json's) is {Sha256(released)}.");
        pi.Deploys.Should().ContainSingle().Which["IMAGE_ARCHIVE_SHA256"].Should().Be(Sha256(released));
        temporaryMode.Should().Be(Modes.PrivateFolder, "the script's copy goes in a folder only root can open");
        pi.Exists(pi.Deploys[0]["TMPDIR"]).Should().BeFalse("the installer's folder, and the script's copy in it, goes when the run ends");
        pi.LoadedArchives.Should().BeEmpty("the deploy script loads nothing but the release's archive");
        pi.Containers.Should().NotContainKey(MachineSurveyor.ControllerContainer);
    }

    [TestMethod]
    public async Task NoRoomForTheCopyDockerLoads_IsRefusedBeforeAnythingChanges()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var name = FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64");
        bench.FreeSpace = FakeMachine.OfflineArchiveContent(name).Length + OfflineImages.CopyHeadroom - 1;

        var run = await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.ToString().Should().Contain(
            $"there is not room in {bench.TemporaryDirectory} for a private copy of {name}, which is what Docker loads, so that it reads only what was checked: it needs 17 MiB (the archive and 16 MiB more), and 16 MiB is free. Free some room there, then run the installer again");
        bench.LoadedArchives.Should().BeEmpty();
        bench.Deploys.Should().BeEmpty();

        bench.FreeSpace = FakeMachine.OfflineArchiveContent(name).Length + OfflineImages.CopyHeadroom;
        (await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder)).ExitCode.Should().Be(0);
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

        // The release on GitHub, which a rollback reads, is the one in the folder: its offline archives are listed.
        bench.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = FakeMachine.ReleaseJson(offlineArchives: true);
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

    [TestMethod]
    public async Task ContainersOfAnotherImage_CarryingTheReleasesLabel_AreReplaced_NotAdopted()
    {
        using var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        var folder = bench.ReleaseFolder();
        var answers = bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] });
        (await bench.RunAsync("--answers", answers, "--release", folder)).ExitCode.Should().Be(0);

        // Each now runs an image with another ID, under the release's label (copied, say, from the ones the installer made).
        var unrelated = "sha256:" + new string('7', 64);
        bench.Containers[MachineSurveyor.ControllerContainer] = bench.Containers[MachineSurveyor.ControllerContainer] with { ImageId = unrelated };
        bench.Containers[MachineSurveyor.HatEmulatorContainer] = bench.Containers[MachineSurveyor.HatEmulatorContainer] with { ImageId = unrelated };

        var plan = await bench.RunAsync("--plan", "--answers", answers, "--release", folder);

        plan.ExitCode.Should().Be(0, plan.ToString());
        plan.Output.Should().Contain($"replaced with release 4.0.0's image: its label names {EmulatorReference}, but it runs the image {unrelated}")
            .And.Contain($"redeployed as release 4.0.0: its label names {ControllerReference}, but it runs the image {unrelated}")
            .And.NotContain("Nothing to change");

        var run = await bench.RunAsync("--answers", answers, "--release", folder);

        run.ExitCode.Should().Be(0, run.ToString());
        bench.Deploys.Should().HaveCount(2, "the controller is deployed again, through the deploy script");
        bench.LoadedArchives.Should().HaveCount(4);
        bench.Containers[MachineSurveyor.ControllerContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(FakeMachine.OfflineArchiveName("roof-controller", "amd64")));
        bench.Containers[MachineSurveyor.HatEmulatorContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(FakeMachine.OfflineArchiveName("roof-hat-emulator", "amd64")));
    }

    [TestMethod]
    public async Task AControllerTheDeployScriptLeaves_WithAnotherImageUnderTheReleasesLabel_FailsTheInstall()
    {
        // The deploy script checks the ID itself; the installer checks the controller it leaves again.
        using var pi = new FakeMachine().WithPi();
        pi.ImageStore = "unknown";
        var folder = pi.ReleaseFolder();

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }), "--release", folder);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.ToString().Should().Contain(
            $"The deploy script finished, but {MachineSurveyor.ControllerContainer} does not run release 4.0.0's image (its label names {ControllerReference}, but it runs the image sha256:{new string('0', 64)}, not one with an ID release.json gives that image for linux/arm64)");
    }

    [TestMethod]
    public async Task ARollback_ToAControllerOfAnotherImage_CarryingTheReleasesLabel_IsRefused_AndNothingChanges()
    {
        using var bench = await UpgradedFromAnArchiveAsync();
        var unrelated = "sha256:" + new string('7', 64);
        bench.Containers[ControllerRollbackStep.PreviousContainer] = bench.Containers[ControllerRollbackStep.PreviousContainer] with { ImageId = unrelated };
        var current = bench.Containers[MachineSurveyor.ControllerContainer];
        var previous = bench.Containers[ControllerRollbackStep.PreviousContainer];
        var emulator = bench.Containers[MachineSurveyor.HatEmulatorContainer];
        var deploys = bench.DeployOptions.Count;

        var rollback = await bench.RunAsync("rollback");

        rollback.ExitCode.Should().Be((int)InstallerExitCode.Refused, rollback.ToString());
        rollback.ToString().Should().Contain(
            $"{ControllerRollbackStep.PreviousContainer} is not release 4.0.0's controller: its label names {ControllerReference}, but it runs the image {unrelated}, not one with an ID release.json gives that image for linux/amd64. The installer does not start an image it cannot check");
        bench.DeployOptions.Should().HaveCount(deploys, "the deploy script never ran");
        bench.Containers[MachineSurveyor.ControllerContainer].Should().Be(current);
        bench.Containers[ControllerRollbackStep.PreviousContainer].Should().Be(previous);
        bench.Containers[MachineSurveyor.HatEmulatorContainer].Should().Be(emulator);
    }

    [TestMethod]
    public async Task ARollback_FromAControllerThatOnlyClaimsTheReleaseBefore_PutsTheRealOneBack()
    {
        using var bench = await UpgradedFromAnArchiveAsync();

        // The controller runs 4.0.1's image by an ID the release before does not give, under 4.0.0's label.
        bench.Containers[MachineSurveyor.ControllerContainer] = bench.Containers[MachineSurveyor.ControllerContainer] with
        {
            Version = "4.0.0",
            Digest = FakeMachine.ControllerDigest,
            ImageId = "sha256:" + new string('7', 64)
        };

        var rollback = await bench.RunAsync("rollback");

        rollback.ExitCode.Should().Be(0, rollback.ToString());
        bench.DeployOptions[^1].Should().Be("--rollback", "it does not run release 4.0.0, so it is rolled back");
        bench.Containers[MachineSurveyor.ControllerContainer].ImageId.Should().Be(FakeMachine.ArchiveConfigDigest(FakeMachine.OfflineArchiveName("roof-controller", "amd64")));
    }

    // A rig installed from 4.0.0's folder, its images loaded from their archives, then upgraded to 4.0.1 from GitHub: the
    // controller loaded from the archive is kept as roof-controller-previous. The installer is now 4.0.1's.
    private static async Task<FakeMachine> UpgradedFromAnArchiveAsync()
    {
        var bench = new FakeMachine(architecture: Architecture.X64, hostName: "bench");
        try
        {
            bench.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = FakeMachine.ReleaseJson(offlineArchives: true);
            var folder = bench.ReleaseFolder();
            (await bench.RunAsync("--answers", bench.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Rig] }), "--release", folder)).ExitCode.Should().Be(0);
            bench.WithRelease("4.0.1", latest: true);
            (await bench.RunAsync("upgrade")).ExitCode.Should().Be(0);
            bench.InstallerVersion = "4.0.1";
            return bench;
        }
        catch
        {
            bench.Dispose();
            throw;
        }
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
