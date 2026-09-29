using FluentAssertions;
using HVO.RoofControllerV4.Web;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

[TestClass]
public sealed class RoofWebOptionsTests
{
    [TestMethod]
    public void Defaults_ListenOn8088_AndReachTheControllerOverLoopback()
    {
        var options = new RoofWebOptions();

        options.Urls.Should().Be("http://+:8088");
        options.ControllerUrl.Should().Be(new Uri("http://localhost:8080"));
        options.StatusRefreshSeconds.Should().Be(2);
        options.SupervisorStatePath.Should().BeNull();
        options.SupervisorControlPath.Should().BeNull();
        options.Cameras.Should().Equal(RoofWebOptions.DefaultCameraId);
        options.Validate(isDevelopment: false).Should().BeEmpty();
    }

    [TestMethod]
    public void Cameras_FromSettings_ReplaceTheDefault_AndCountEachOnce()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RoofWeb:CameraIds:0"] = "5",
                ["RoofWeb:CameraIds:1"] = "3",
                ["RoofWeb:CameraIds:2"] = "5",
            })
            .Build();

        var options = configuration.GetSection(RoofWebOptions.SectionName).Get<RoofWebOptions>()!;

        options.Cameras.Should().Equal(5, 3);
        options.Validate(false).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(100)]
    [DataRow(-2)]
    public void Validate_ACameraThatIsNotOneToNinetyNine_IsRefused(int camera)
    {
        new RoofWebOptions { CameraIds = [2, camera] }.Validate(false).Should().ContainSingle()
            .Which.Should().Be($"RoofWeb:CameraIds has {camera}, which is not a camera number from 1 to 99.");
    }

    [TestMethod]
    public void Validate_MoreThanFourCameras_IsRefused_ButARepeatCountsOnce()
    {
        new RoofWebOptions { CameraIds = [1, 2, 3, 4, 5] }.Validate(false).Should().ContainSingle()
            .Which.Should().Be("RoofWeb:CameraIds names 5 cameras; the roof page shows at most 4.");
        new RoofWebOptions { CameraIds = [1, 2, 3, 4, 4, 1] }.Validate(false).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ; ")]
    public void Validate_WithoutUrls_IsRefused(string urls)
    {
        new RoofWebOptions { Urls = urls }.Validate(false).Should().ContainSingle()
            .Which.Should().Contain("RoofWeb:Urls must name at least one URL");
    }

    [TestMethod]
    public void Validate_AUrlThatIsNotHttp_IsRefused()
    {
        new RoofWebOptions { Urls = "http://+:8088;ftp://+:21" }.Validate(false).Should().ContainSingle()
            .Which.Should().Contain("'ftp://+:21'");
    }

    [TestMethod]
    [DataRow("ftp://localhost:8080")]
    [DataRow("/relative")]
    public void Validate_AControllerUrlThatIsNotAbsoluteHttp_IsRefused(string url)
    {
        new RoofWebOptions { ControllerUrl = new Uri(url, UriKind.RelativeOrAbsolute) }.Validate(false).Should().ContainSingle()
            .Which.Should().Contain("RoofWeb:ControllerUrl");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(61)]
    public void Validate_ARefreshOutsideOneToSixtySeconds_IsRefused(int seconds)
    {
        new RoofWebOptions { StatusRefreshSeconds = seconds }.Validate(false).Should().ContainSingle()
            .Which.Should().Contain("StatusRefreshSeconds must be between 1 and 60");
    }

    [TestMethod]
    public void Validate_Https_OutsideDevelopment_NeedsACertificate()
    {
        var options = new RoofWebOptions { Urls = "https://+:8088" };

        options.Validate(isDevelopment: false).Should().ContainSingle().Which.Should().Contain("RoofWeb:Certificate:Path");
        options.Validate(isDevelopment: true).Should().BeEmpty("development falls back to the developer certificate");
    }

    [TestMethod]
    public void Validate_CertificateFilesThatDoNotExist_AreNamed()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var options = new RoofWebOptions
        {
            Urls = "https://+:8088",
            Certificate = { Path = directory.File("missing.pfx"), PasswordFile = directory.File("missing-password") },
        };

        options.Validate(false).Should().HaveCount(2)
            .And.Contain(problem => problem.Contains("missing.pfx", StringComparison.Ordinal))
            .And.Contain(problem => problem.Contains("missing-password", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SplitUrls_TrimsAndDropsEmptyEntries()
    {
        RoofWebOptions.SplitUrls(" http://+:8088 ;; https://+:8089 ").Should().Equal("http://+:8088", "https://+:8089");
        RoofWebOptions.SplitUrls(null).Should().BeEmpty();
    }
}
