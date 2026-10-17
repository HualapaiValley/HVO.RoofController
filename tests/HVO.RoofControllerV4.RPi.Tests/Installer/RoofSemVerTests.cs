using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Record;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The order in which <c>upgrade</c> refuses an older release (#131): SemVer 2.0's, so a release candidate comes before its
/// release, and a prerelease's numbers are compared as numbers.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class RoofSemVerTests
{
    [TestMethod]
    [DataRow("3.9.0", "4.0.0")]
    [DataRow("4.0.9", "4.0.10")]
    [DataRow("4.9.0", "4.10.0")]
    [DataRow("4.0.0-rc.1", "4.0.0")]
    [DataRow("4.0.0", "4.0.1-rc.1")]
    [DataRow("4.0.1-rc.2", "4.0.1-rc.10")]
    [DataRow("4.0.1-rc", "4.0.1-rc.1")]
    public void AnOlderRelease_IsOlder_AndNotTheOtherWayRound(string older, string newer)
    {
        RoofSemVer.IsOlder(older, newer).Should().BeTrue();
        RoofSemVer.IsOlder(newer, older).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("4.0.1", "4.0.1")]
    [DataRow("4.0.1-rc.1", "4.0.1-rc.1")]
    [DataRow("4.0.1+0123abcd", "4.0.1")]
    public void TheSameRelease_IsNotOlder(string left, string right)
    {
        RoofSemVer.Compare(left, right).Should().Be(0);
        RoofSemVer.IsOlder(left, right).Should().BeFalse();
    }
}
