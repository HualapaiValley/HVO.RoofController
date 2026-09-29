using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The web UI must not show a status it can no longer vouch for: when the controller loses its HAT, the roof page says
/// the inputs and relays are unverified; when the page loses its connection, the reconnect dialog covers the stale
/// status until the page reconnects and shows the roof as it now is. (A status the web UI stops receiving from the
/// controller is shown as the last known state; the page tests cover that, <c>DashboardTests</c>.)
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebStaleStatusBrowserTests
{
    private static readonly TimeSpan OutageSeen = TimeSpan.FromSeconds(15);

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
    public async Task WhenTheControllerLosesTheHat_ThePageFlagsItsStatusAsUnverified_AndStopSaysToCheckAtTheRoof()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.TabletLandscape);
        var rig = browser.Rig;
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Operator);
        await Expect(browser.Banners.Locator("[role=alert]")).ToHaveCountAsync(0);

        rig.Server.Outage = true;

        var inputs = page.GetByTestId("inputs-banner");
        var relays = page.GetByTestId("relays-banner");
        var fault = page.GetByTestId("fault-banner");
        await Expect(inputs).ToContainTextAsync("Safety inputs: reads failing", new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(inputs).ToContainTextAsync("Motion may be refused.");
        await Expect(relays).ToContainTextAsync("register reads failing", new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(fault).ToBeVisibleAsync();
        await Expect(page.GetByTestId("closed-limit")).ToHaveTextAsync("Closed limit (last read): active");
        await Expect(browser.Open).ToBeDisabledAsync();

        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.SentUnverified, new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(browser.StopOutcome).ToHaveAttributeAsync("data-state", "warn");

        rig.Server.Outage = false;

        await Expect(inputs).ToHaveCountAsync(0, new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(relays).ToHaveCountAsync(0, new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(fault).ToBeVisibleAsync();
        await Expect(page.GetByTestId("closed-limit")).ToHaveTextAsync("Closed limit: active");
        rig.Controller.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue("a lost relay register latches a fault an operator must clear");

        await page.GetByTestId("clear-fault").ClickAsync();
        await Expect(fault).ToHaveCountAsync(0);
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await Expect(browser.Open).ToBeEnabledAsync();
    }

    /// <summary>
    /// On a phone, and a small phone held sideways, the longest status (an unhealthy HAT) wraps: the page does not scroll
    /// sideways, and Stop stays in view.
    /// </summary>
    [TestMethod]
    [DataRow(WebDevices.Phone)]
    [DataRow(WebDevices.SmallPhoneLandscape)]
    public async Task OnANarrowScreen_TheWholeUnhealthyStatusWraps_AndStopStaysInView(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), device);
        var rig = browser.Rig;
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Operator);

        rig.Server.Outage = true;
        await Expect(page.GetByTestId("relays-banner")).ToContainTextAsync("register reads failing", new() { Timeout = (float)OutageSeen.TotalMilliseconds });
        await Expect(page.GetByTestId("inputs-banner")).ToBeVisibleAsync();

        var stop = await browser.PlacementOfAsync(browser.Stop);
        stop.Reachable.Should().BeTrue("Stop must be in view on the {0} with every banner shown: {1}", device, stop);
        var width = await page.EvaluateAsync<double>("() => document.scrollingElement.scrollWidth");
        width.Should().BeLessThanOrEqualTo(stop.ViewportWidth, "the page must not scroll sideways on the {0}", device);
        var clipped = await browser.Banners.EvaluateAsync<string[]>("""
            banners => [...banners.querySelectorAll('[role=alert] span')]
              .filter(e => e.scrollWidth > e.clientWidth + 1 || e.getBoundingClientRect().right > document.documentElement.clientWidth + 0.5)
              .map(e => e.textContent.trim())
            """);
        clipped.Should().BeEmpty("every banner must be shown whole on the {0}", device);
    }

    [TestMethod]
    public async Task WhenThePageLosesItsConnection_TheReconnectDialogCoversTheStaleStatus_AndThePageShowsTheRoofAsItIsOnReconnecting()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Tablet, cuttableConnection: true);
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(WebBrowser.Operator);
        await Expect(browser.Position).ToHaveTextAsync("Closed");

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        (await browser.PlacementOfAsync(browser.Position)).Reachable.Should().BeFalse("the dialog covers the page's stale status");
        (await browser.PlacementOfAsync(browser.Stop)).Reachable.Should().BeFalse("the dialog's own Stop is the one to use");
        var dialogStop = await browser.PlacementOfAsync(browser.DialogStop);
        dialogStop.Reachable.Should().BeTrue("the dialog's Stop must be usable: {0}", dialogStop);

        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await Expect(browser.ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-show\b"));

        browser.RestoreConnection();
        await browser.ExpectReconnectedAsync();
        await Expect(browser.Position).ToHaveTextAsync("Open");
        await Expect(browser.Page.GetByTestId("open-limit")).ToHaveTextAsync("Open limit: active");
        await Expect(browser.Page.GetByTestId("feed-state")).ToHaveTextAsync("Status: live");
    }
}
