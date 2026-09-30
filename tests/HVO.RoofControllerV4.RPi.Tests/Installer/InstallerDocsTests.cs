using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.RPi.Tests.Versioning;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// docs/install.md and the installer agree (#67): every exit code, role, connection and default it lists, its answers
/// file example, and a screenshot in docs/images/install for each page the wizard's tests draw, each shown on the page.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed partial class InstallerDocsTests
{
    private static readonly string Root = ProductVersionTests.RepositoryRoot();

    private static string Page => File.ReadAllText(Path.Combine(Root, "docs", "install.md"));

    [TestMethod]
    public void TheExitCodeTable_ListsEveryExitCode()
    {
        var listed = ExitCodeRow().Matches(Page).Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        listed.Should().BeEquivalentTo(Enum.GetValues<InstallerExitCode>().Select(code => (int)code), "docs/install.md lists the exit codes install.sh and scripts rely on");
    }

    [TestMethod]
    public void TheAnswersFileExamples_AreOnesTheInstallerReads()
    {
        var examples = JsonBlock().Matches(Page);
        examples.Should().HaveCountGreaterThanOrEqualTo(3, "docs/install.md shows an answers file for the controller, one for a rig, and one with the kiosk");

        var answers = InstallAnswers.Parse(examples[0].Groups[1].Value);
        answers.Roles.Should().Equal(InstallRole.Controller);
        answers.Controller.Should().BeEquivalentTo(
            new ControllerSettings
            {
                HostNames = ["roof"],
                Domains = ["observatory.example"],
                FirstAdmin = new FirstAdminSettings { Name = "observer", Pin = true },
                Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81", UserName = "roof-viewer" },
                TelemetryEndpoint = "http://collector:4318"
            },
            "the example shows the defaults, with a name and a domain for the certificate, and each optional choice");

        var rig = InstallAnswers.Parse(examples[1].Groups[1].Value);
        rig.Roles.Should().Equal(InstallRole.Rig);
        rig.Controller!.Rig.Should().Be(new RigSettings { TimeScale = 10, CameraFramesPerSecond = RigSettings.DefaultCameraFramesPerSecond });

        var kiosk = InstallAnswers.Parse(examples[2].Groups[1].Value);
        kiosk.Roles.Should().Equal(InstallRole.Controller, InstallRole.Kiosk);
        kiosk.Kiosk.Should().BeEquivalentTo(new KioskSettings { HideCursor = true, Pins = ["olga"] });
        kiosk.Problems().Should().BeEmpty();
    }

    [TestMethod]
    public void TheAnswersTable_NamesEveryRoleAndConnection_WithTheDefaults()
    {
        var page = Page;
        foreach (var role in InstallRoles.All)
        {
            page.Should().Contain($"`{InstallRoles.Name(role)}`");
        }

        page.Should().Contain("`private-ca`, `own-certificate`, `self-signed`, `http`");
        page.Should().Contain($"| `controller.httpsPort` | The controller's API over HTTPS (the deploy script's `HTTPS_HOST_PORT`) | `{ControllerSettings.DefaultHttpsPort}` |");
        page.Should().Contain($"| `controller.httpPort` | The controller's API over HTTP, with `http` (`HOST_PORT`) | `{ControllerSettings.DefaultHttpPort}` |");
        page.Should().Contain($"| `controller.webPort` | The web UI (`WEB_HOST_PORT`) | `{ControllerSettings.DefaultWebPort}` |");
        page.Should().Contain(FormattableString.Invariant($"| A rig only: how many times as fast as real time the emulated roof runs, from `{RigSettings.MinimumTimeScale}` to `{RigSettings.MaximumTimeScale}` | `{RigSettings.DefaultTimeScale}` (the wizard offers the running emulator's) |"));
        page.Should().Contain(FormattableString.Invariant($"| A rig only: the emulated camera's frame rate, from `{RigSettings.MinimumCameraFramesPerSecond}` to `{RigSettings.MaximumCameraFramesPerSecond}` | `{RigSettings.DefaultCameraFramesPerSecond}` (the wizard offers the running emulator's) |"));
        page.Should().Contain($"| `cli.folder` | `{CliSettings.HomeFolder}` or `{CliSettings.SharedFolder}` | `{new CliSettings().Folder}` |");
        page.Should().Contain($"| `macApp.folder` | `{MacAppSettings.SharedFolder}` or `{MacAppSettings.HomeFolder}` | `{new MacAppSettings().Folder}` |");
    }

    [TestMethod]
    public void EveryPageTheWizardsTestsDraw_HasAScreenshot_ShownOnThePage()
    {
        var drawn = Directory.GetFiles(Path.Combine(Root, "tests", "HVO.RoofControllerV4.RPi.Tests", "Installer"), "InstallerWizard*Tests.cs")
            .SelectMany(file => RenderCall().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var folder = Path.Combine(Root, "docs", "images", "install");
        var screenshots = Directory.GetFiles(folder, "*.svg").Select(Path.GetFileNameWithoutExtension).ToArray();
        var shown = Screenshot().Matches(Page).Select(match => match.Groups[1].Value).ToArray();

        drawn.Should().NotBeEmpty();
        screenshots.Should().BeEquivalentTo(drawn, "docs/images/install holds a screenshot of each page the tests draw, and no other (docs/install.md says how to refresh them)");
        shown.Should().BeEquivalentTo(drawn, "docs/install.md shows each screenshot once");
    }

    [TestMethod]
    public async Task ThePlanExample_IsWhatTheInstallerPrints()
    {
        var example = PlanExample().Match(Page);
        example.Success.Should().BeTrue("docs/install.md shows --plan for a test rig");
        using var rig1 = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "rig1", userName: "roy");
        var answers = rig1.WriteAnswers(InstallAnswers.Parse(JsonBlock().Matches(Page)[1].Groups[1].Value), "rig.json");

        var run = await rig1.RunAsync("--plan", "--answers", answers);

        (run.Output + run.Error).Should().Be(example.Groups[1].Value.Replace("rig.json", answers, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\| (\d+) \|", RegexOptions.Multiline)]
    private static partial Regex ExitCodeRow();

    [GeneratedRegex(@"```json\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"\.Render\(TestContext, ""([^""]+)""\)")]
    private static partial Regex RenderCall();

    [GeneratedRegex(@"```text\n\$ hvo-roof-install --plan --answers rig\.json\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex PlanExample();

    [GeneratedRegex(@"\]\(images/install/([^)]+)\.svg\)")]
    private static partial Regex Screenshot();
}
