using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentAssertions;
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
    public void TheAnswersFileExample_IsOneTheInstallerReads()
    {
        var example = JsonBlock().Match(Page);
        example.Success.Should().BeTrue("docs/install.md shows an answers file");

        var answers = InstallAnswers.Parse(example.Groups[1].Value);

        answers.Roles.Should().Equal(InstallRole.Controller);
        answers.Controller.Should().Be(new ControllerSettings(), "the example shows the defaults");
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
        page.Should().Contain($"| `cli.folder` | `{CliSettings.HomeFolder}` or `{CliSettings.SharedFolder}` | `{new CliSettings().Folder}` |");
        page.Should().Contain($"| `macApp.folder` | `{MacAppSettings.SharedFolder}` or `{MacAppSettings.HomeFolder}` | `{new MacAppSettings().Folder}` |");
    }

    [TestMethod]
    public void EveryPageTheWizardsTestsDraw_HasAScreenshot_ShownOnThePage()
    {
        var tests = File.ReadAllText(Path.Combine(Root, "tests", "HVO.RoofControllerV4.RPi.Tests", "Installer", "InstallerWizardTests.cs"));
        var drawn = RenderCall().Matches(tests).Select(match => match.Groups[1].Value).ToArray();
        var folder = Path.Combine(Root, "docs", "images", "install");
        var screenshots = Directory.GetFiles(folder, "*.svg").Select(Path.GetFileNameWithoutExtension).ToArray();
        var shown = Screenshot().Matches(Page).Select(match => match.Groups[1].Value).ToArray();

        drawn.Should().NotBeEmpty();
        screenshots.Should().BeEquivalentTo(drawn, "docs/images/install holds a screenshot of each page the tests draw, and no other (docs/install.md says how to refresh them)");
        shown.Should().BeEquivalentTo(drawn, "docs/install.md shows each screenshot once");
    }

    [GeneratedRegex(@"^\| (\d+) \|", RegexOptions.Multiline)]
    private static partial Regex ExitCodeRow();

    [GeneratedRegex(@"```json\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"\.Render\(TestContext, ""([^""]+)""\)")]
    private static partial Regex RenderCall();

    [GeneratedRegex(@"\]\(images/install/([^)]+)\.svg\)")]
    private static partial Regex Screenshot();
}
