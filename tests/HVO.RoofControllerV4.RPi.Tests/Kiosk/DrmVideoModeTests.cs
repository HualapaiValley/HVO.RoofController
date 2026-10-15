using Avalonia;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Kiosk;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class DrmVideoModeTests
{
    [TestMethod]
    public void RequestedPixelMode_ReachesDrm_WithoutChangingScaleOrWindowSize()
    {
        var options = new KioskOptions { DeviceKeyFile = "/test-key", VideoMode = "1920x1080", Rotation = 0 };
        options.Validate().Should().BeEmpty();
        options.DrmOptions().VideoMode.Should().Be(new PixelSize(1920, 1080));
        options.DrmOptions().Scaling.Should().Be(1);
        options.WindowSize.Should().Be("1280x720");
        options.PixelsPerMillimetre.Should().Be(KioskOptions.DefaultPixelsPerMillimetre);
        options.VideoMode = "";
        options.DrmOptions().VideoMode.Should().BeNull();
        var answers = new InstallAnswers { Roles = [HVO.RoofControllerV4.Installer.Roles.InstallRole.Kiosk], Kiosk = new() { VideoMode = "1920x1080" } };
        InstallAnswers.Parse(answers.ToJson()).Kiosk!.VideoMode.Should().Be("1920x1080");
    }

    [TestMethod]
    [DataRow("1920")][DataRow("1920x0")][DataRow("1920x1080@60")][DataRow(" 1920x1080")][DataRow("8001x1080")][DataRow("-1x1080")]
    public void MalformedModes_AreRefusedByKioskAndInstaller(string mode)
    {
        RoofDisplayMode.TryParse(mode, out _, out _).Should().BeFalse();
        new KioskOptions { DeviceKeyFile = "/test-key", VideoMode = mode }.Validate().Should().Contain(problem => problem.Contains("VideoMode"));
        new KioskSettings { VideoMode = mode }.Problems().Should().NotBeEmpty();
    }
}
