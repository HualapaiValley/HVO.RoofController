using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.Simulation.Camera;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The console's camera view against the emulated camera through the controller's camera proxy: a camera that stops
/// sending frames or goes offline is shown as such over the last frame, the roof's controls keep working, and the view
/// recovers by itself when the camera does.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class ConsoleCameraBrowserTests
{
    private static readonly Regex LastFrame = new(@"No live video — last frame \d\d:\d\d:\d\d");

    private ConsoleBrowser? _browser;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task WhenTheCameraStallsOrGoesOffline_TheConsoleSaysSo_StopStillWorks_AndTheViewRecoversByItself()
    {
        var options = Scenario.Production(travelMeters: 2.0) with { Camera = true };
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, options, ConsoleDevices.TabletLandscape);
        var rig = browser.Rig;
        await browser.SignInAsync(TestApiKeys.Operator);
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 15_000 });
        await Expect(browser.CameraOverlay).ToHaveCountAsync(0);

        // An encoder that hangs: the stream stays open without frames.
        rig.Camera.Mode = EmulatedCameraMode.Frozen;
        await Expect(browser.CameraStatus).ToHaveTextAsync("Stalled");
        await Expect(browser.CameraOverlay).ToContainTextAsync(LastFrame);
        rig.Camera.Mode = EmulatedCameraMode.Live;
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live");
        await Expect(browser.CameraOverlay).ToHaveCountAsync(0);

        // The camera server loses the camera: the stream ends and each retry is refused.
        rig.Camera.Mode = EmulatedCameraMode.Unavailable;
        await Expect(browser.CameraStatus).ToHaveTextAsync("Offline");
        await Expect(browser.CameraOverlay).ToContainTextAsync(LastFrame);

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToContainTextAsync("Stop acknowledged. Relay register verified de-energized.");
        var stopped = await rig.WaitForRestAsync("the console's Stop with the camera offline");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);

        // The view retries with a backoff (up to 8 s by now) and goes live again without a reload.
        rig.Camera.Mode = EmulatedCameraMode.Live;
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 30_000 });
        await Expect(browser.CameraOverlay).ToHaveCountAsync(0);
        await Expect(browser.Page.GetByText("The camera view failed")).ToHaveCountAsync(0);
    }
}
