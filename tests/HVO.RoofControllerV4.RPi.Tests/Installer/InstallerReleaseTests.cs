using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Deployment;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The release the installer installs (#69): its release.json, from GitHub or a folder, read once a run, and only when
/// it is this installer's release with both images pinned to a digest.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerReleaseTests
{
    [TestMethod]
    public void AReleaseJson_GivesEachImageByItsDigest()
    {
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0");

        release.Version.Should().Be("4.0.0");
        release.Commit.Should().Be("0123456789abcdef0123456789abcdef01234567");
        release.Controller.Should().Be(new ReleaseImage(
            "ghcr.io/hualapaivalley/roof-controller",
            FakeMachine.ControllerDigest,
            $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{FakeMachine.ControllerDigest}"));
        release.HatEmulator.Reference.Should().Be($"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{FakeMachine.EmulatorDigest}");
    }

    [TestMethod]
    public void TheDownload_IsTheReleasesAsset() =>
        ReleaseManifest.DownloadUri("4.0.1").ToString()
            .Should().Be("https://github.com/HualapaiValley/HVO.RoofController/releases/download/v4.0.1/release.json");

    [TestMethod]
    [DataRow("version", "4.0.1", "it is the release of 4.0.1, and this installer installs 4.0.0")]
    [DataRow("schemaVersion", 2, "it is schema 2, and this installer reads schema 1")]
    [DataRow("images.controller.digest", "sha256:abc", "the controller image's digest is not sha256:<64 hex digits>")]
    [DataRow("images.controller.reference", "ghcr.io/hualapaivalley/roof-controller:4.0.0", "the controller image's reference is not its repository, a tag and its digest")]
    [DataRow("images.hatEmulator.reference", "ghcr.io/someone-else/roof-hat-emulator:4.0.0@sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "the hatEmulator image's reference is not its repository, a tag and its digest")]
    [DataRow("images.hatEmulator.repository", "GHCR.IO/Roof", "the hatEmulator image's repository is not an image repository")]
    [DataRow("images.hatEmulator", null, "it has no hatEmulator image")]
    [DataRow("upgradeNotes", "Nothing to do by hand.", "its upgrade notes are not a list")]
    public void AReleaseJson_ThatIsNotThisRelease_OrNotPinned_IsRefused(string path, object? value, string reason)
    {
        var json = JsonNode.Parse(FakeMachine.ReleaseJson())!;
        var parts = path.Split('.');
        var parent = parts[..^1].Aggregate(json, (node, part) => node[part]!);
        parent[parts[^1]] = value switch
        {
            null => null,
            int number => JsonValue.Create(number),
            _ => JsonValue.Create((string)value)
        };
        if (value is null)
        {
            parent.AsObject().Remove(parts[^1]);
        }

        var parse = () => ReleaseManifest.Parse(json.ToJsonString(), "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage($"The installer will not use release.json: {reason}*");
    }

    [TestMethod]
    public void AReleaseJson_GivesEveryReleasesUpgradeNotes()
    {
        UpgradeNote[] notes = [new("4.0.0", "Zero."), new("4.0.1", "One.\nNothing to do by hand.")];

        ReleaseManifest.Parse(FakeMachine.ReleaseJson("4.0.1", upgradeNotes: notes), "4.0.1").UpgradeNotes.Should().Equal(notes);
        ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0").UpgradeNotes.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("{\"version\": \"4.0\", \"text\": \"Zero.\"}")]
    [DataRow("{\"version\": \"4.0.0\"}")]
    [DataRow("{\"version\": \"4.0.0\", \"text\": \"\"}")]
    [DataRow("\"Zero.\"")]
    public void AnUpgradeNote_WithoutAReleasesVersionAndItsText_IsRefused(string note)
    {
        var json = JsonNode.Parse(FakeMachine.ReleaseJson())!;
        json["upgradeNotes"] = new JsonArray(JsonNode.Parse(note));

        var parse = () => ReleaseManifest.Parse(json.ToJsonString(), "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage("The installer will not use release.json: upgrade note 1 is not a release's version and its text.");
    }

    [TestMethod]
    public void AReleaseJson_ThatIsNotJson_IsRefused()
    {
        var parse = () => ReleaseManifest.Parse("<html>Not Found</html>", "4.0.0");

        parse.Should().Throw<InstallerException>().WithMessage("The installer cannot read release.json: it is not valid JSON*");
    }

    [TestMethod]
    public async Task TheRelease_IsDownloadedOnce_ARun()
    {
        using var pi = new FakeMachine().WithPi();
        var source = ReleaseSource.GitHub();

        var first = await source.GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);
        var second = await source.GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);

        second.Should().BeSameAs(first);
        pi.Downloaded.Should().Equal(ReleaseManifest.DownloadUri("4.0.0"));
    }

    [TestMethod]
    public async Task ADownloadThatFailed_IsTriedAgain_TheNextTimeTheReleaseIsAskedFor()
    {
        using var pi = new FakeMachine().WithPi();
        var published = pi.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()];
        pi.Downloads.Clear();
        var source = ReleaseSource.GitHub();
        var first = () => source.GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);
        await first.Should().ThrowAsync<InstallerException>();

        pi.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = published;
        var release = await source.GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);

        release.Version.Should().Be("4.0.0");
        pi.Downloaded.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ARelease_GitHubDoesNotHave_SaysToGiveItsFolder()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Downloads.Clear();

        var get = () => ReleaseSource.GitHub().GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);

        (await get.Should().ThrowAsync<InstallerException>()).Which.Message.Should()
            .Contain("could not get release.json for 4.0.0 from https://github.com/HualapaiValley/HVO.RoofController/releases/download/v4.0.0/release.json: HTTP 404 Not Found.")
            .And.Contain("--release DIR");
    }

    [TestMethod]
    public async Task AReleaseFolder_IsReadInPlaceOfGitHub()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write("/home/pi/release/release.json", FakeMachine.ReleaseJson(controllerDigest: "sha256:" + new string('9', 64)));

        var release = await ReleaseSource.Folder("/home/pi/release").GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);

        release.Controller.Digest.Should().Be("sha256:" + new string('9', 64));
        pi.Downloaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AReleaseFolder_WithoutReleaseJson_IsAUsageError()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Folder("/home/pi/release");

        var get = () => ReleaseSource.Folder("/home/pi/release").GetAsync(pi.Machine, InstallLog.None, "4.0.0", CancellationToken.None);

        (await get.Should().ThrowAsync<InstallerUsageException>()).WithMessage("There is no release.json in /home/pi/release.");
    }

    [TestMethod]
    public async Task TheReleaseOption_NeedsAFolderThatIsThere()
    {
        using var pi = new FakeMachine().WithPi();
        var before = pi.Snapshot();

        var run = await pi.RunAsync("--plan", "--release", "/nowhere");

        run.ExitCode.Should().Be((int)InstallerExitCode.Usage, run.ToString());
        run.Error.Should().Contain("There is no folder /nowhere for --release.");
        pi.Snapshot().Should().Equal(before);
    }
}
