using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The web UI's Alpaca panel in Chromium against the whole controller and the emulated roof (#220): an operator who grants
/// an Alpaca Close steps away from the operator lease, the page's script does not count the click that sent the grant as
/// input, and only the person's next click takes the lease back, which drops the grant.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebAlpacaBrowserTests
{
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
    public async Task AGrant_StepsTheOperatorAway_TheConfirmingClickIsNotInput_AndTheirNextClickTakesTheLeaseBack()
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{RoofAlpacaCommandOptions.SectionName}:AllowCommands"] = nameof(RoofAlpacaCommandMode.CloseOnly)
        };
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(settings: settings), WebDevices.Desktop);
        var page = browser.Page;
        var alpaca = browser.Rig.App.Services.GetRequiredService<RoofAlpacaCommands>();
        await browser.SignInAsync(WebBrowser.Operator);
        await ClientTestSupport.WaitUntilAsync(() => alpaca.GetStatus().OperatorLease is { Held: true, Holder: WebBrowser.Operator }, "the page to take the lease", TimeSpan.FromSeconds(15));
        alpaca.RaiseAlert(
            "Refused",
            RoofAlpacaCommandKind.Close,
            "An Alpaca Close was refused: Telescope 'Main' is not parked.",
            [RoofAlpacaContract.ReasonKey(RoofAlpacaContract.ScopeNotClearKind, "Main")],
            new RoofAlpacaClient(7, 3, "192.0.2.50", "header"));

        await page.GetByTestId("alpaca-grant-ask").ClickAsync();
        await page.GetByTestId("alpaca-question-confirm").ClickAsync();

        await Expect(page.GetByTestId("alpaca-lease-notice")).ToContainTextAsync("You stepped away: Alpaca may close the roof once in the next");
        await Expect(page.GetByTestId("alpaca-lease-banner")).ToContainTextAsync("Click or type on this page to take control back; that cancels the grant.");
        alpaca.Grants().Should().ContainSingle();
        alpaca.GetStatus().OperatorLease.Held.Should().BeFalse("the operator stepped away with the grant");

        // More than a heartbeat later the lease is still released: the click that sent the grant, and the page's renders
        // since, were not input.
        await Task.Delay(RoofAlpacaText.LeaseHeartbeatInterval + TimeSpan.FromSeconds(2));
        alpaca.GetStatus().OperatorLease.Held.Should().BeFalse();
        alpaca.Grants().Should().ContainSingle("the grant waits for an Alpaca Close");
        await Expect(page.GetByTestId("alpaca-lease-notice")).ToBeVisibleAsync();

        await page.GetByTestId("alpaca-mode").ClickAsync();

        await ClientTestSupport.WaitUntilAsync(() => alpaca.GetStatus().OperatorLease is { Held: true, Holder: WebBrowser.Operator }, "the next click to take the lease back", TimeSpan.FromSeconds(15));
        alpaca.Grants().Should().BeEmpty("taking the lease back drops the grant");
        await Expect(page.GetByTestId("alpaca-lease-notice")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("alpaca-lease-banner")).ToHaveCountAsync(0);
        await Expect(browser.Stop).ToBeEnabledAsync();
    }
}
