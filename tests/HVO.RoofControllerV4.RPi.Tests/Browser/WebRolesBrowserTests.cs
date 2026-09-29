using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.Web.Sessions;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// What each role is offered in the web UI, against the controller that decides it: the admin pages are an admin's only,
/// the mode banner says on every page that the roof does not move, a new key's secret is shown once and works at the
/// controller, and a restart is asked for before it is sent and stops a moving roof.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebRolesBrowserTests
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
    [DataRow(WebBrowser.Operator, "Operator")]
    [DataRow(WebBrowser.Viewer, "Viewer")]
    public async Task WithoutTheAdminRole_TheAdminPagesAreNotOffered_AndOpeningOneSaysSo_AndTheModeBannerIsOnEveryPage(string name, string role)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Desktop);
        var page = browser.Page;

        // Before signing in too: the sign-in page asks the controller how it drives the roof.
        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        await Expect(browser.ModeBanner).ToHaveTextAsync(RoofStatusText.EmulatedHat);

        await browser.SignInAsync(name);
        await Expect(page.GetByTestId("signed-in-role")).ToHaveTextAsync(role);

        var links = page.Locator(".web-nav-links .nav-link");
        await Expect(links).ToHaveTextAsync(["Roof", "Health", "Settings"]);
        await Expect(browser.ModeBanner).ToHaveTextAsync(RoofStatusText.EmulatedHat);

        foreach (var path in new[] { "/health", "/settings", "/account/password" })
        {
            await page.GotoAsync(path);
            await Expect(page.Locator("main h1").First).ToBeVisibleAsync();
            await Expect(browser.ModeBanner).ToHaveTextAsync(RoofStatusText.EmulatedHat);
            await Expect(page.GetByTestId("role-denied")).ToHaveCountAsync(0);
        }

        foreach (var path in new[] { "/people", "/system" })
        {
            await page.GotoAsync(path);
            await Expect(page.GetByTestId("role-denied")).ToBeVisibleAsync();
            await Expect(browser.ModeBanner).ToHaveTextAsync(RoofStatusText.EmulatedHat);
            await Expect(page.GetByTestId("users")).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("restart")).ToHaveCountAsync(0);
        }

        // Stop is not a role's: it is on the page that turned the person away too.
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
    }

    [TestMethod]
    public async Task AnAdmin_AddsAKey_ItsSecretIsShownOnce_AndTheKeyWorksAtTheController()
    {
        const string keyName = "browser-key";
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Tablet);
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Admin);
        await Expect(page.Locator(".web-nav-links .nav-link")).ToHaveTextAsync(["Roof", "Health", "Settings", "People", "System"]);

        var keysTab = page.Locator("[data-testid=people-tab][data-tab=keys]");
        await page.GotoAsync("/people");
        await Expect(page.GetByTestId("users")).ToBeVisibleAsync();
        await keysTab.ClickAsync();
        await Expect(page.GetByTestId("keys")).ToBeVisibleAsync();
        await page.GetByTestId("key-add").ClickAsync();
        await page.GetByTestId("key-name").FillAsync(keyName);
        await page.GetByTestId("key-role").SelectOptionAsync(WebRoles.Operator);
        await page.GetByTestId("key-add-send").ClickAsync();

        var secret = page.GetByTestId("key-secret-value");
        await Expect(secret).ToBeVisibleAsync();
        await Expect(page.Locator($"[data-testid=key][data-name={keyName}]")).ToBeVisibleAsync();
        var value = await secret.InputValueAsync();
        value.Should().NotBeNullOrWhiteSpace();

        using (var client = browser.Rig.CreateApiClient(value))
        {
            using var answer = await client.PostAsync(RoofApiRoutes.Stop, null);
            answer.StatusCode.Should().Be(HttpStatusCode.OK, "the key the page showed is the controller's new operator key");
        }

        // Shown once: a reload of the page does not show it again, and nor does the controller.
        await page.ReloadAsync();
        await Expect(page.GetByTestId("users")).ToBeVisibleAsync();
        await keysTab.ClickAsync();
        await Expect(page.Locator($"[data-testid=key][data-name={keyName}]")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("key-secret")).ToHaveCountAsync(0);
        (await page.ContentAsync()).Should().NotContain(value, "the secret is not in the page once it has been shown");
    }

    [TestMethod]
    public async Task ARestart_IsAskedForFirst_AndStopsTheMovingRoof()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), WebDevices.Desktop);
        var rig = browser.Rig;
        var page = browser.Page;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(WebBrowser.Admin);

        await page.GotoAsync("/system");
        await Expect(page.GetByTestId("system-fact").First).ToBeVisibleAsync();
        await page.GetByTestId("restart-start").ClickAsync();
        var confirm = page.GetByTestId("restart-confirm");
        await Expect(confirm).ToContainTextAsync(RoofSystemText.RestartQuestion);
        await confirm.GetByTestId("panel-cancel").ClickAsync();
        await Expect(confirm).ToHaveCountAsync(0);
        rig.Logs.Entries.Should().NotContain(e => e.Message.StartsWith("AUDIT controller restart", StringComparison.Ordinal));

        await client.AcceptedAsync("Open");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        await page.GetByTestId("restart-start").ClickAsync();
        await confirm.GetByTestId("restart-send").ClickAsync();

        var message = page.GetByTestId("page-message");
        await Expect(message).ToContainTextAsync("The roof is stopped. The controller is restarting");
        await Expect(message).ToContainTextAsync(RoofSystemText.RestartComingBack);
        rig.Logs.Entries.Should().Contain(e => e.Message.StartsWith($"AUDIT controller restart requested by", StringComparison.Ordinal)
            && e.Message.Contains(WebBrowser.Admin, StringComparison.Ordinal));
        // The controller has exited (the supervisor would start it again), so the plant is what says the roof stopped.
        var stopped = await rig.WaitForPlantAsync(p => p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0, "the restart's stop");
        stopped.RelayRegister.Should().Be(0, "the HAT's relay register is all off");
        stopped.RelayContacts.Should().AllBeEquivalentTo(false, "every relay contact is open");
        stopped.OpenLimitActuated.Should().BeFalse("the restart stopped the roof before the open limit");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
