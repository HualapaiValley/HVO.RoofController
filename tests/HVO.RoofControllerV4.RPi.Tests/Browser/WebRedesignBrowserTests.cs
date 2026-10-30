using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>Production UI layout, keyboard access and direct Stop at the redesign's minimum width.</summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebRedesignBrowserTests
{
    private WebBrowser? _browser;
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
    }

    [TestMethod]
    [DataRow(320)]
    [DataRow(1440)]
    public async Task RoutesAndNestedEditors_Reflow_AndKeepRedKeyboardStop(int width)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Desktop);
        var page = browser.Page;
        using var admin = browser.AdminClient();
        var longName = "design-person-" + new string('x', 45);
        await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = "design-device-" + new string('k', 40), Role = RoofControllerApiContract.ViewerRole });
        await admin.Identity.AddUserAsync(new RoofUserCreateRequest { Name = longName, Role = RoofControllerApiContract.AdminRole, Password = TestSecrets.Password, Pin = TestSecrets.Pin });
        await page.SetViewportSizeAsync(width, 900);
        await page.GotoAsync("/signin");
        await FitAndStopAsync();
        await browser.SignInAsync(longName);
        foreach (var route in new[] { "/", "/health", "/settings", "/people", "/system", "/account/keys", "/account/password", "/account/names", "/denied", "/missing-redesign-address" })
        {
            await page.GotoAsync(route);
            await Expect(page.Locator("main h1")).ToHaveCountAsync(1);
            await FitAndStopAsync();
        }

        await page.GotoAsync("/people");
        await page.GetByTestId("user-add").ClickAsync();
        await FitAndStopAsync();
        await page.GetByTestId("user-add-send").ClickAsync();
        await Expect(page.GetByTestId("panel-error")).ToBeVisibleAsync();
        await FitAndStopAsync();
        await Expect(page.GetByTestId("user-name")).ToHaveAttributeAsync("aria-invalid", "true");
        await page.GetByTestId("user-name").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.GetByTestId("user-add-form")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("user-add")).ToBeFocusedAsync();
        foreach (var action in new[] { "user-role-change", "user-password-set", "user-pin-set", "user-pin-remove", "user-remove" })
        {
            await page.GetByTestId(action).First.ClickAsync();
            await Expect(page.GetByTestId("user-panel")).ToBeVisibleAsync();
            await FitAndStopAsync();
            await page.GetByTestId("panel-cancel").ClickAsync();
            await Expect(page.GetByTestId(action).First).ToBeFocusedAsync();
        }
        await page.Locator("[data-testid=people-tab][data-tab=keys]").ClickAsync();
        await page.GetByTestId("key-add").ClickAsync();
        await FitAndStopAsync();
        await page.GetByTestId("panel-cancel").ClickAsync();
        foreach (var action in new[] { "key-change", "key-rotate", "key-remove" })
        {
            await page.GetByTestId(action).First.ClickAsync();
            await Expect(page.GetByTestId("key-panel")).ToBeVisibleAsync();
            await FitAndStopAsync();
            await page.GetByTestId("panel-cancel").ClickAsync();
        }
        await page.Locator("[data-testid=people-tab][data-tab=sessions]").ClickAsync();
        await page.GetByTestId("session-end").First.ClickAsync();
        await FitAndStopAsync();
        await page.GetByTestId("panel-cancel").ClickAsync();

        await page.GotoAsync("/settings");
        foreach (var group in await page.GetByTestId("settings-group").AllAsync())
        {
            await group.ClickAsync();
            await FitAndStopAsync();
            foreach (var action in await page.GetByTestId("setting-change").AllAsync())
            {
                await action.ClickAsync();
                await FitAndStopAsync();
                await page.GetByTestId("setting-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            }
        }
        await page.GotoAsync("/system");
        await page.GetByTestId("restart-start").ClickAsync();
        await Expect(page.GetByTestId("restart-confirm")).ToBeVisibleAsync();
        await FitAndStopAsync();
        await page.GetByTestId("panel-cancel").ClickAsync();
        await page.GetByTestId("forced-restart-start").ClickAsync();
        await Expect(page.GetByTestId("forced-restart-send")).ToBeDisabledAsync();
        await page.GetByTestId("forced-restart-word").FillAsync("restart");
        await Expect(page.GetByTestId("forced-restart-send")).ToBeEnabledAsync();
        await FitAndStopAsync();
        await page.GetByTestId("panel-cancel").ClickAsync();
    }

    [TestMethod]
    public async Task FullscreenCamera_KeepsTheSameDirectStop_AndRestoresItOnExit()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0) with { Camera = true }, WebDevices.Desktop);
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Operator);
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 15_000 });
        await browser.Open.ClickAsync();
        await browser.Rig.WaitForControllerAsync(status => status.IsMoving, "the emulated roof to move before fullscreen Stop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Fullscreen", Exact = true }).ClickAsync();
        await Expect(page.Locator(":fullscreen [data-testid=stop]")).ToBeVisibleAsync();
        await FitAndStopAsync();
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        browser.Rig.Controller.GetCurrentStatusSnapshot().ShouldBeDeenergized(browser.Rig);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(":fullscreen")).ToHaveCountAsync(0);
        await Expect(page.Locator("body > .web-stop-bar [data-testid=stop]")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("stop")).ToHaveCountAsync(1);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Fullscreen", Exact = true })).ToBeFocusedAsync();
    }

    [TestMethod]
    [DataRow(1440, 900)]
    [DataRow(320, 720)]
    [DataRow(568, 320)]
    public async Task BothAcknowledgements_PreserveActualMessage_ThroughFocus_AndReconnect(int width, int height)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Desktop, cuttableConnection: true);
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Viewer);
        var message = RoofStopText.Acknowledged;
        await page.RouteAsync("**/stop", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "application/json",
            Body = JsonSerializer.Serialize(new { outcome = "Acknowledged", message })
        }));
        foreach (var actual in new[] { RoofStopText.Acknowledged, RoofStopText.AcknowledgedVerified })
        {
            message = actual;
            await browser.Stop.ClickAsync();
            await Expect(browser.StopOutcome).ToHaveTextAsync(actual);
            await FitAndStopAsync();
        }
        await page.SetViewportSizeAsync(width, height);
        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        foreach (var state in new[] { "show", "failed", "rejected", "paused", "resume-failed" })
        {
            // Measure the initial position before Playwright can scroll/focus the action into view.
            await browser.ReconnectDialog.EvaluateAsync("""
                (dialog, state) => {
                  dialog.className = 'components-reconnect-' + state;
                  dialog.scrollTop = 0;
                  for (const child of dialog.querySelectorAll('.reconnect-dialog, .reconnect-state')) child.scrollTop = 0;
                }
                """, state);
            await browser.NextFrameAsync();
            var bounds = await browser.DialogStop.BoundingBoxAsync();
            bounds.Should().NotBeNull();
            bounds!.X.Should().BeGreaterThanOrEqualTo(0, state);
            bounds.Y.Should().BeGreaterThanOrEqualTo(0, state);
            (bounds.X + bounds.Width).Should().BeLessThanOrEqualTo(width, state);
            (bounds.Y + bounds.Height).Should().BeLessThanOrEqualTo(height, state);
            var placement = await browser.PlacementOfAsync(browser.DialogStop);
            placement.Reachable.Should().BeTrue("Stop must be initially uncovered in {0}: {1}", state, placement);
            await Expect(browser.DialogStop).ToBeEnabledAsync();
        }
        message = RoofStopText.Acknowledged;
        await browser.DialogStop.ClickAsync();
        await Expect(browser.ReconnectDialog.Locator("[data-web-stop-result]")).ToHaveTextAsync(RoofStopText.Acknowledged);
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.Acknowledged);
    }

    private async Task FitAndStopAsync()
    {
        var browser = _browser!;
        var page = browser.Page;
        await browser.NextFrameAsync();
        (await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue("the page and inline editor must fit without horizontal scrolling");
        var undersized = await page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('main button, main input:not([type=hidden],[type=checkbox],[type=radio]), main select, main .form-check-label, main label:has(input[type=checkbox])')]
              .filter(e => { const r=e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && (r.width < 48 || r.height < 48); })
              .map(e => e.tagName + '#' + e.id)
            """);
        undersized.Should().BeEmpty("actions and fields retain the 48px touch floor in every editor");
        await Expect(browser.Stop).ToBeEnabledAsync();
        await page.Keyboard.PressAsync("Tab");
        await browser.Stop.FocusAsync();
        var colors = await browser.Stop.EvaluateAsync<string[]>("e => { const s=getComputedStyle(e); return [s.color,s.backgroundColor,s.borderTopColor,s.outlineStyle]; }");
        colors.Should().Equal("rgb(254, 202, 202)", "rgb(66, 34, 38)", "rgb(248, 81, 73)", "solid");
        (await browser.PlacementOfAsync(browser.Stop)).Reachable.Should().BeTrue("Stop remains visible and uncovered");
    }
}
