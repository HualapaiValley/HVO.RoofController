using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Layout;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Components.RoofConsoleHarness;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

/// <summary>An emulator run is marked on every page (the banner) and on the console (the hardware badge and the footer).</summary>
[TestClass]
public sealed class EmulatedHatDisplayTests
{
    private const string Banner = "[data-testid=emulated-hat-banner]";

    [TestMethod]
    public async Task TheBanner_InEmulatorMode_NamesTheEmulator_AndSaysTheRoofDoesNotMove()
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton(HatConnections.Emulated("hat-emulator", 5391));

        var cut = context.Render<EmulatedHatBanner>();

        var banner = cut.Find(Banner);
        banner.GetAttribute("role").Should().Be("alert");
        banner.ClassList.Should().Contain("alert-warning");
        banner.TextContent.Should().Contain("EMULATED HAT").And.Contain("not the physical HAT").And.Contain("The observatory roof does not move.");
        cut.Find($"{Banner} code").TextContent.Should().Be("hat-emulator:5391");
    }

    [TestMethod]
    public async Task TheBanner_WithTheEmulatorOff_RendersNothing()
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton(HatConnections.Hardware());

        var cut = context.Render<EmulatedHatBanner>();

        cut.Markup.Trim().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(RoofHatMode.Emulated, true, "Emulated HAT", "bg-warning")]
    [DataRow(RoofHatMode.Physical, true, "Physical I²C", "bg-primary")]
    [DataRow(RoofHatMode.Simulation, false, "Simulation", "bg-warning")]
    [DataRow(RoofHatMode.Unknown, true, "Physical I²C", "bg-primary")]
    public async Task TheConsoleBadge_ShowsTheHatMode(RoofHatMode mode, bool physical, string label, string badgeClass)
    {
        await using var harness = new RoofConsoleHarness(
            Status(RoofControllerStatus.Closed, 1) with { HatMode = mode, IsUsingPhysicalHardware = physical }).SignInAsViewer();

        var cut = harness.Render();

        var badge = cut.Find(".rc2-status-pill .bi-cpu").ParentElement!;
        badge.TextContent.Trim().Should().Be(label);
        badge.ClassList.Should().Contain(badgeClass);
    }

    [TestMethod]
    public async Task TheFooter_SaysEmulatedHat_NotSimulation()
    {
        await using var harness = new RoofConsoleHarness(
            Status(RoofControllerStatus.Closed, 1) with { HatMode = RoofHatMode.Emulated }).SignInAsViewer();

        var cut = harness.Render();

        cut.WaitForAssertion(() => harness.Footer.Snapshot.Center!.Text.Should().Contain("Emulated HAT").And.NotContain("Simulation"));
    }
}
