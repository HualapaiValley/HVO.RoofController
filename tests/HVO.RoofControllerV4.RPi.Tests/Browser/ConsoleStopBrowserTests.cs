using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The console's sign-in page and its Stop, on a phone and a tablet each held upright and sideways, against the
/// documented 2 m roof (about 21 s from limit to limit), so the roof is still moving when Stop is pressed.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class ConsoleStopBrowserTests
{
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
    public async Task TheSignInPage_RefusesAWrongKey_AndAViewerMayStopTheRoofButNotMoveIt()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(), ConsoleDevices.Phone);
        var page = browser.Page;

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex(@"/login\?returnUrl=%2F$"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Roof Controller" })).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Access key")).ToBeFocusedAsync();

        await browser.SubmitAccessKeyAsync("not-an-access-key");
        await Expect(page.GetByTestId("login-error")).ToHaveTextAsync("The access key was not accepted. Check it and try again.");
        await Expect(page).ToHaveURLAsync(new Regex("/login"));

        await browser.SubmitAccessKeyAsync(TestApiKeys.Viewer);
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await Expect(page.GetByTestId("role-hint")).ToHaveTextAsync(
            "Signed in as Viewer. Open, Close and Clear Fault require the Operator role. Stop is always available.");
        await Expect(browser.Open).ToBeDisabledAsync();
        await Expect(browser.Close).ToBeDisabledAsync();
        await Expect(browser.Stop).ToBeEnabledAsync();

        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToContainTextAsync("Stop acknowledged. Relay register verified de-energized.");
        browser.Rig.Controller.GetCurrentStatusSnapshot().ShouldBeDeenergized(browser.Rig);
    }

    [TestMethod]
    [DataRow(ConsoleDevices.Phone)]
    [DataRow(ConsoleDevices.PhoneLandscape)]
    [DataRow(ConsoleDevices.Tablet)]
    [DataRow(ConsoleDevices.TabletLandscape)]
    public async Task AnOperator_SignsIn_OpensTheRoof_AndStopsItDuringTheMove(string device)
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), device);
        var rig = browser.Rig;
        await browser.SignInAsync(TestApiKeys.Operator);
        await Expect(browser.Position).ToHaveTextAsync("Closed");

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        await Expect(browser.Commanded).ToHaveTextAsync("Opening");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.Stop.ClickAsync();

        await Expect(browser.StopOutcome).ToContainTextAsync("Stop acknowledged. Relay register verified de-energized.");
        var stopped = await rig.WaitForRestAsync("the console's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.IsFaultLatched.Should().BeFalse();
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("Stop ended the move before the open limit");
        await Expect(browser.Position).ToHaveTextAsync(RoofConsoleRules.DescribePosition(stopped.Status));
        await Expect(browser.Commanded).ToHaveTextAsync("None");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// The roof is started elsewhere (the API), so the console is as it loaded, not scrolled: Stop must be in the
    /// viewport and uncovered (the footer is fixed to the bottom), and a tap where it is must stop the roof. The layout
    /// measured is the installation's: the emulator's banner, which the physical HAT does not show, is hidden first.
    /// </summary>
    [TestMethod]
    [DataRow(ConsoleDevices.Phone)]
    [DataRow(ConsoleDevices.PhoneLandscape)]
    [DataRow(ConsoleDevices.SmallPhoneLandscape)]
    [DataRow(ConsoleDevices.Tablet)]
    [DataRow(ConsoleDevices.TabletLandscape)]
    public async Task WhileTheRoofMoves_StopIsInViewWithoutScrolling_AndATapOnItStopsTheRoof(string device)
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), device);
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(TestApiKeys.Operator);

        await client.AcceptedAsync("Open");
        await Expect(browser.Position).ToHaveTextAsync("Opening");

        var withBanner = await browser.PlacementOfAsync(browser.Stop);
        await browser.Page.AddStyleTagAsync(new() { Content = "[data-testid=emulated-hat-banner] { display: none !important; }" });
        var stop = await browser.PlacementOfAsync(browser.Stop);
        stop.ScrollY.Should().Be(0);
        stop.Reachable.Should().BeTrue(
            "Stop must be in view without scrolling on the {0} while the roof moves: Stop is at {1} (with the emulator's banner: {2})",
            device,
            stop,
            withBanner);

        await browser.Page.Touchscreen.TapAsync((float)stop.CenterX, (float)stop.CenterY);

        await Expect(browser.StopOutcome).ToContainTextAsync("Stop acknowledged. Relay register verified de-energized.");
        var stopped = await rig.WaitForRestAsync("the tap on Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
