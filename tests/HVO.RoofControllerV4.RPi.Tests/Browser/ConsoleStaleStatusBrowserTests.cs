using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The console must not show a status it can no longer vouch for: when the controller loses its HAT, the console says
/// the inputs and relays are unverified; when the console loses the controller, its reconnect dialog covers the stale
/// status until it reconnects and shows the roof as it now is.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class ConsoleStaleStatusBrowserTests
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
    public async Task WhenTheControllerLosesTheHat_TheConsoleFlagsItsStatusAsUnverified_AndStopSaysToCheckAtTheRoof()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(), ConsoleDevices.TabletLandscape);
        var rig = browser.Rig;
        await browser.SignInAsync(TestApiKeys.Operator);
        await Expect(browser.Banners).ToBeEmptyAsync();

        rig.Server.Outage = true;

        await Expect(browser.StatusBadges.Filter(new() { HasText = "Inputs unhealthy" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(browser.StatusBadges.Filter(new() { HasText = "Relay unverified" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(browser.Banners).ToContainTextAsync("Safety inputs unhealthy.");
        await Expect(browser.Banners).ToContainTextAsync("Relay register unverified.");
        await Expect(browser.Banners).ToContainTextAsync("Fault latched");
        await Expect(browser.FooterStatus).ToContainTextAsync("Inputs unhealthy");

        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToContainTextAsync(
            "Stop sent, but the relay register could not be verified. Confirm at the roof that the motor has stopped.",
            new() { Timeout = 15_000 });

        rig.Server.Outage = false;

        await Expect(browser.StatusBadges.Filter(new() { HasText = "Inputs unhealthy" })).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(browser.StatusBadges.Filter(new() { HasText = "Relay unverified" })).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(browser.Banners).Not.ToContainTextAsync("Safety inputs unhealthy.");
        await Expect(browser.Banners).ToContainTextAsync("Fault latched", new() { Timeout = 1_000 });
        rig.Controller.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue("a lost relay register latches a fault an operator must clear");
    }

    [TestMethod]
    public async Task WhenTheConsoleLosesItsConnection_TheReconnectDialogCoversTheStaleStatus_AndTheConsoleShowsTheRoofAsItIsOnReconnecting()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(), ConsoleDevices.Tablet);
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(TestApiKeys.Operator);
        await Expect(browser.Position).ToHaveTextAsync("Closed");

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        (await browser.PlacementOfAsync(browser.Position)).Reachable.Should().BeFalse("the dialog covers the console's stale status");
        var dialogStop = await browser.PlacementOfAsync(browser.DialogStop);
        dialogStop.Reachable.Should().BeTrue("the dialog's Stop must be usable: {0}", dialogStop);

        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await Expect(browser.ReconnectDialog).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bcomponents-reconnect-show\b"));

        browser.RestoreConnection();
        await browser.ExpectReconnectedAsync();
        await Expect(browser.Position).ToHaveTextAsync("Open");
        await Expect(browser.FooterStatus).ToContainTextAsync("Roof: Open");
    }
}
