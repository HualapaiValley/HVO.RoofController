using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The web UI's sign-in page and its Stop, on phones, tablets and a desktop window, against the documented 2 m roof
/// (about 21 s from limit to limit), so the roof is still moving when Stop is pressed.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebStopBrowserTests
{
    /// <summary>Each page Stop is checked on, with what shows the page has read what it shows.</summary>
    private static readonly (string Path, string Loaded)[] Pages =
    [
        ("/", "[data-testid=dashboard][data-feed=live]"),
        ("/health", "[data-testid=health-overall]"),
        ("/settings", "[data-testid=settings-summary]"),
        ("/people", "[data-testid=users]"),
        ("/system", "[data-testid=system-fact]"),
        ("/account/password", "main h1"),
    ];

    private WebBrowser? _browser;

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
    public async Task TheSignInPage_RefusesAWrongPassword_AndAViewerMayStopTheRoofButNotMoveIt()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Phone);
        var page = browser.Page;

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex(@"/signin\?returnUrl=%2F$"));
        await Expect(page.GetByLabel("Name")).ToBeFocusedAsync();

        await browser.SubmitSignInAsync(WebBrowser.Viewer, "not-the-password");
        await Expect(page.GetByTestId("signin-message")).ToHaveTextAsync(SignInMessages.Describe(SignInMessages.Failed)!.Text);
        await Expect(page).ToHaveURLAsync(new Regex("/signin"));

        await browser.SubmitSignInAsync(WebBrowser.Viewer, TestSecrets.Password);
        await browser.ExpectLiveAsync();
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await Expect(page.GetByTestId("signed-in-role")).ToHaveTextAsync("Viewer");
        await Expect(page.GetByTestId("role-hint")).ToHaveTextAsync(
            "Open, Close and Clear fault need the Operator role. " + RoofStopText.AlwaysAvailable);
        await Expect(browser.Open).ToBeDisabledAsync();
        await Expect(browser.Close).ToBeDisabledAsync();
        await Expect(browser.Stop).ToBeEnabledAsync();

        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        await Expect(browser.StopOutcome).ToHaveAttributeAsync("data-state", "ok");
        browser.Rig.Controller.GetCurrentStatusSnapshot().ShouldBeDeenergized(browser.Rig);
        await browser.WaitForWebLogAsync($"Web stop for {WebBrowser.Viewer}: {RoofStopOutcome.Acknowledged}");
    }

    [TestMethod]
    [DataRow(WebDevices.Phone)]
    [DataRow(WebDevices.PhoneLandscape)]
    [DataRow(WebDevices.Tablet)]
    [DataRow(WebDevices.TabletLandscape)]
    [DataRow(WebDevices.Desktop)]
    public async Task AnOperator_SignsIn_OpensTheRoof_AndStopsItDuringTheMove(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), device);
        var rig = browser.Rig;
        await browser.SignInAsync(WebBrowser.Operator);
        await Expect(browser.Position).ToHaveTextAsync("Closed");

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        await Expect(browser.Commanded).ToHaveTextAsync("Opening");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.Stop.ClickAsync();

        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        var stopped = await rig.WaitForRestAsync("the web UI's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.IsFaultLatched.Should().BeFalse();
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("Stop ended the move before the open limit");
        await Expect(browser.Position).ToHaveTextAsync(RoofText.DescribePosition(stopped.Status));
        await Expect(browser.Commanded).ToHaveTextAsync("None");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// The roof is started elsewhere (the API), so the page is as it loaded, not scrolled: Stop must be in the viewport
    /// and uncovered (the Stop bar is fixed to the bottom), and a tap (a click on the desktop) where it is must stop the
    /// roof.
    /// </summary>
    [TestMethod]
    [DataRow(WebDevices.Phone)]
    [DataRow(WebDevices.PhoneLandscape)]
    [DataRow(WebDevices.SmallPhoneLandscape)]
    [DataRow(WebDevices.Tablet)]
    [DataRow(WebDevices.TabletLandscape)]
    [DataRow(WebDevices.Desktop)]
    public async Task WhileTheRoofMoves_StopIsInViewWithoutScrolling_AndATapOnItStopsTheRoof(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), device);
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(WebBrowser.Operator);

        await client.AcceptedAsync("Open");
        await Expect(browser.Position).ToHaveTextAsync("Opening");

        var stop = await browser.PlacementOfAsync(browser.Stop);
        stop.ScrollY.Should().Be(0);
        stop.Reachable.Should().BeTrue("Stop must be in view without scrolling on the {0} while the roof moves: Stop is at {1}", device, stop);

        await browser.TapAsync(stop);

        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        var stopped = await rig.WaitForRestAsync("the tap on Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// Stop is on every page, the sign-in page too, in view and uncovered as the page loads, and no page scrolls
    /// sideways; and the Stop bar never covers the end of a page: scrolled to the bottom, the page's content ends above
    /// the bar, with the bar as tall as it gets (showing the answer to a Stop).
    /// </summary>
    [TestMethod]
    [DataRow(WebDevices.Phone)]
    [DataRow(WebDevices.PhoneLandscape)]
    [DataRow(WebDevices.SmallPhoneLandscape)]
    [DataRow(WebDevices.Tablet)]
    [DataRow(WebDevices.TabletLandscape)]
    [DataRow(WebDevices.Desktop)]
    public async Task OnEveryPage_StopIsInView_AndTheStopBarNeverCoversTheEndOfThePage(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), device);
        var page = browser.Page;

        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        // A signed-out page sends Stop without a credential, which the controller refuses unless it allows anonymous Stop.
        await ExpectStopPlacedAsync(browser, "/signin", RoofStopText.SignedOut);

        await browser.SignInAsync(WebBrowser.Admin);
        foreach (var (path, loaded) in Pages)
        {
            await page.GotoAsync(path);
            await Expect(page.Locator(loaded).First).ToBeVisibleAsync();
            await ExpectStopPlacedAsync(browser, path, RoofStopText.AcknowledgedVerified);
        }
    }

    /// <summary>
    /// The Stop bar's room is kept at the end of the page, not added to a screen-high page: a page that fits above the
    /// bar on a desktop window does not scroll.
    /// </summary>
    [TestMethod]
    public async Task APageThatFitsAboveTheStopBar_DoesNotScroll()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Desktop);
        var page = browser.Page;

        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        await ExpectNoScrollAsync("/signin");

        await browser.SignInAsync(WebBrowser.Admin);
        await page.GotoAsync("/account/password");
        await Expect(page.Locator("main h1")).ToBeVisibleAsync();
        await ExpectNoScrollAsync("/account/password");

        async Task ExpectNoScrollAsync(string path)
        {
            await browser.NextFrameAsync();
            var sizes = await page.EvaluateAsync<double[]>("() => [document.scrollingElement.scrollHeight, window.innerHeight]");
            var (height, screen) = (sizes[0], sizes[1]);
            height.Should().BeLessThanOrEqualTo(screen, "{0} fits above the Stop bar, so it must not scroll", path);
        }
    }

    private static async Task ExpectStopPlacedAsync(WebBrowser browser, string path, string answer)
    {
        var page = browser.Page;
        var stop = await browser.PlacementOfAsync(browser.Stop);
        stop.Reachable.Should().BeTrue("Stop must be in view as {0} loads on the {1}: Stop is at {2}", path, browser.Device, stop);
        var width = await page.EvaluateAsync<double>("() => document.scrollingElement.scrollWidth");
        width.Should().BeLessThanOrEqualTo(stop.ViewportWidth, "{0} must not scroll sideways on the {1}", path, browser.Device);

        await browser.TapAsync(await browser.PlacementOfAsync(browser.Stop));
        await Expect(browser.StopOutcome).ToHaveTextAsync(answer);
        // The answer makes the bar taller, and the page's room for it follows on the next frame.
        await browser.NextFrameAsync();

        await page.EvaluateAsync("() => window.scrollTo(0, document.scrollingElement.scrollHeight)");
        width = await page.EvaluateAsync<double>("() => document.scrollingElement.scrollWidth");
        width.Should().BeLessThanOrEqualTo(stop.ViewportWidth, "{0} must not scroll sideways on the {1}", path, browser.Device);
        var bar = await browser.PlacementOfAsync(page.Locator("form.web-stop-bar"));
        var content = await page.Locator(path == "/signin" ? "main" : "main.web-main").EvaluateAsync<double>(
            "element => element.getBoundingClientRect().bottom");
        content.Should().BeLessThanOrEqualTo(bar.Top + 0.5,
            "the end of {0} must not be under the Stop bar on the {1}: the page ends at {2:0}, the bar is at {3}", path, browser.Device, content, bar);
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }
}
