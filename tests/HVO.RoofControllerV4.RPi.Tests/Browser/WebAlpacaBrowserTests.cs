using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The web UI's Alpaca panel in Chromium against the whole controller and the emulated roof (#220): an operator who grants
/// an Alpaca Close (or Open) steps away from the operator lease, the page's script does not count the click that sent the
/// grant as input, and only the person's next click takes the lease back, which drops the grant; and once the safety
/// interlock turns to Block, a refused Open's grant is disabled, with the controller's reason.
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

        await Expect(page.GetByTestId("alpaca-lease-notice")).ToContainTextAsync("You stepped away from the operator lease: Alpaca may close the roof once in the next");
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

    [TestMethod]
    public async Task AnOpenGrant_StepsTheOperatorAway_AndInBlockMode_ARefusedOpensGrantIsDisabledWithTheReason()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(settings: AlpacaOpenUiSupport.OpenAndClose()), WebDevices.Desktop);
        var page = browser.Page;
        var alpaca = browser.Rig.App.Services.GetRequiredService<RoofAlpacaCommands>();
        await browser.SignInAsync(WebBrowser.Operator);
        await ClientTestSupport.WaitUntilAsync(() => alpaca.GetStatus().OperatorLease is { Held: true, Holder: WebBrowser.Operator }, "the page to take the lease", TimeSpan.FromSeconds(15));
        string[] reasons = ["weather-unsafe:Sky", RoofAlpacaContract.NoBlockingScopesKey];
        alpaca.RaiseAlert("Refused", RoofAlpacaCommandKind.Open, $"An Alpaca Open was refused: {RoofAlpacaText.DescribeReasons(reasons)}.", reasons,
            new RoofAlpacaClient(7, 3, "192.0.2.50", "header"));

        await Expect(page.GetByTestId("alpaca-grant-ask")).ToHaveTextAsync(RoofAlpacaText.GrantOpenLabel);
        await page.GetByTestId("alpaca-grant-ask").ClickAsync();
        await Expect(page.Locator("#alpaca-question-title")).ToHaveTextAsync("Allow one Alpaca Open?");
        await page.GetByTestId("alpaca-question-confirm").ClickAsync();

        await Expect(page.GetByTestId("alpaca-lease-notice")).ToContainTextAsync("You stepped away from the operator lease: Alpaca may open the roof once in the next");
        var grant = alpaca.Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Open);
        grant.ReasonKeys.Should().Equal(reasons.Order(StringComparer.Ordinal));
        alpaca.GetStatus().OperatorLease.Held.Should().BeFalse("the operator stepped away with the grant");

        await page.GetByTestId("alpaca-mode").ClickAsync();

        await ClientTestSupport.WaitUntilAsync(() => alpaca.GetStatus().OperatorLease is { Held: true, Holder: WebBrowser.Operator }, "the next click to take the lease back", TimeSpan.FromSeconds(15));
        alpaca.Grants().Should().BeEmpty("taking the lease back drops the grant");
        await Expect(page.GetByTestId("alpaca-lease-notice")).ToHaveCountAsync(0);

        // The safety interlock turns to Block: the next status says no Open grant can be made.
        browser.Rig.App.Services.Reconfigure(RoofApiTestHost.SafetyInterlockModeKey, nameof(RoofSafetyInterlockMode.Block));
        alpaca.RaiseAlert("Refused", RoofAlpacaCommandKind.Open, "An Alpaca Open was refused: Weather monitor 'Sky' state unknown.", ["weather-unknown:Sky"],
            new RoofAlpacaClient(7, 4, "192.0.2.50", "header"));
        var ask = page.Locator("[data-testid=alpaca-grant-ask][data-kind=Open]").First;
        await Expect(ask).ToBeDisabledAsync(new() { Timeout = 15_000 });
        var reason = page.GetByTestId("alpaca-grant-unavailable").First;
        await Expect(reason).ToHaveTextAsync(RoofAlpacaCommands.OpenGrantsBlocked);
        (await ask.GetAttributeAsync("aria-describedby")).Should().Be(await reason.GetAttributeAsync("id"));
        alpaca.Grants().Should().BeEmpty();
        await Expect(browser.Stop).ToBeEnabledAsync();
    }
}
