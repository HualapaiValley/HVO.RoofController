using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.Screens;
using HVO.RoofControllerV4.Web;

namespace HVO.RoofControllerV4.RPi.Tests.Versioning;

/// <summary>
/// The product version (Directory.Build.props, docs/releasing.md): every program carries the same one, the version in the
/// props or a prerelease of it, and the clients describe it the same way.
/// </summary>
[TestClass]
public sealed class ProductVersionTests
{
    /// <summary>One assembly of each program: the controller, the web UI, the CLI, the kiosk and Mac screens, the emulator.</summary>
    private static readonly Assembly[] Programs =
    [
        typeof(Program).Assembly,
        typeof(RoofWebOptions).Assembly,
        typeof(RoofCli).Assembly,
        typeof(KioskSystemPanel).Assembly,
        typeof(RoofControllerClient).Assembly,
        typeof(RoofProductVersion).Assembly,
        typeof(EmulatorHostOptions).Assembly
    ];

    [TestMethod]
    public void EveryProgram_CarriesTheProductVersion_OrAPrereleaseOfIt()
    {
        var prefix = VersionPrefix();
        prefix.Should().MatchRegex(@"^\d+\.\d+\.\d+$", "Directory.Build.props holds a release version without a suffix");

        var versions = Programs.Select(RoofProductVersion.Of).ToList();

        versions.Distinct().Should().ContainSingle("every program is built at one version from one commit");
        RoofProductVersion.WithoutCommit(versions[0]).Should().MatchRegex($@"^{Regex.Escape(prefix)}(-[0-9A-Za-z.-]+)?$");
    }

    [TestMethod]
    public void TheFirstRelease_IsVersion4()
        => VersionPrefix().Should().StartWith("4.", "the first release is v4.0.0, matching RoofControllerV4, and later ones follow SemVer");

    [TestMethod]
    [DataRow("4.0.0", "4.0.0", null, "4.0.0")]
    [DataRow("4.0.0-ci.12", "4.0.0-ci.12", null, "4.0.0-ci.12")]
    [DataRow("4.0.0-dev+0123456789abcdef0123456789abcdef01234567", "4.0.0-dev", "0123456789abcdef0123456789abcdef01234567", "4.0.0-dev (commit 0123456789ab)")]
    [DataRow("4.1.0-rc.1+abc1234", "4.1.0-rc.1", "abc1234", "4.1.0-rc.1 (commit abc1234)")]
    [DataRow("4.0.0+", "4.0.0", null, "4.0.0")]
    public void AnInformationalVersion_IsReadAsVersionAndCommit(string informational, string version, string? commit, string described)
    {
        RoofProductVersion.WithoutCommit(informational).Should().Be(version);
        RoofProductVersion.Commit(informational).Should().Be(commit);
        RoofProductVersion.Describe(informational).Should().Be(described);
    }

    [TestMethod]
    public void AClientsOwnVersion_IsDescribedWithItsName()
    {
        var (label, value) = RoofSystemText.DescribeClient("Web UI", typeof(RoofWebOptions).Assembly);

        label.Should().Be("Web UI");
        value.Should().Be(RoofProductVersion.Describe(typeof(RoofWebOptions).Assembly));
        value.Should().StartWith(VersionPrefix());
    }

    [TestMethod]
    public void TheControllersInformation_DescribesItsVersion()
    {
        var information = new HVO.RoofControllerV4.Common.Models.SystemInformationResponse(
            "HVO.RoofControllerV4.RPi", "Production", "roof", "Linux", ".NET 10", "4.0.0-ci.7+0123456789abcdef0123",
            DateTimeOffset.UnixEpoch, 60, DateTimeOffset.UnixEpoch);
        var metrics = new HVO.RoofControllerV4.Common.Models.SystemRuntimeMetricsResponse(0, 0, 0, 0, 0, 0, TimeSpan.Zero, 0, DateTimeOffset.UnixEpoch);

        RoofSystemText.DescribeInformation(information, metrics).First()
            .Should().Be(("Application", "HVO.RoofControllerV4.RPi 4.0.0-ci.7 (commit 0123456789ab) (Production)"));
    }

    /// <summary>The VersionPrefix in Directory.Build.props.</summary>
    internal static string VersionPrefix()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        return props.Descendants("VersionPrefix").Single().Value.Trim();
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
