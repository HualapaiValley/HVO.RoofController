using FluentAssertions;
using HVO.RoofControllerV4.Web;

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
        options.Validate(isDevelopment: false).Should().BeEmpty();
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
