using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The web UI's screenshots in <c>docs/web.md</c>: each page as an admin sees it, against the emulated roof and camera.
/// Every page is visited on a phone, a tablet and a desktop and must be whole (no sideways scroll, and once signed in the
/// mode banner shown) with no secret in it: the key panel is never opened, and the controller's host name is masked.
/// The docs keep every page at the desktop, the sign-in and roof pages on the phone, and the roof page on the tablet, as
/// JPEG to keep the repository small. The images go to <c>browser/screens</c> in the test results, or to <c>WEB_SCREENS_OUT</c> when it is
/// set (the refresh in <c>docs/web.md</c> points it at <c>docs/images/web</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebScreenshotTests
{
    /// <summary>The images the docs keep of the phone and tablet; the desktop's are all kept.</summary>
    private static readonly Dictionary<string, string[]> Kept = new()
    {
        ["phone"] = ["01-signin", "02-roof", "03-roof-opening"],
        ["tablet"] = ["02-roof"],
    };

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
    [DataRow(WebDevices.Phone, "phone")]
    [DataRow(WebDevices.Tablet, "tablet")]
    [DataRow(WebDevices.Desktop, "desktop")]
    public async Task EachPage_AsAnAdminSeesIt_IsCaptured_WithoutASecret(string device, string name)
    {
        var options = Scenario.Production(travelMeters: 2.0) with { Camera = true };
        var browser = _browser = await WebBrowser.StartAsync(TestContext, options, device);
        var page = browser.Page;
        var shots = new Screens(this, browser, name);
        await browser.Rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = 30 });

        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        await shots.TakeAsync("01-signin");

        await browser.SignInAsync(WebBrowser.Admin);
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 15_000 });
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        await shots.TakeAsync("02-roof");

        // A move shows the lease the page renews and the travel; Stop ends it before the images that follow.
        await browser.Open.ClickAsync();
        await browser.Rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        await browser.Rig.WaitForPlantAsync(p => p.PositionMeters > 0.5, "the roof to be well under way");
        await Expect(browser.Lease).ToBeVisibleAsync();
        await shots.TakeAsync("03-roof-opening");
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        await browser.Rig.WaitForRestAsync("the Stop after the opening image");

        await page.GotoAsync("/health");
        await Expect(page.GetByTestId("health-check").First).ToBeVisibleAsync();
        await Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync();
        await shots.TakeAsync("04-health");

        await page.GotoAsync("/settings");
        await Expect(page.GetByTestId("settings-group").First).ToBeVisibleAsync();
        // The first screen: the settings run on for several screens of fields.
        await shots.TakeAsync("05-settings", wholePage: false);

        await page.GotoAsync("/people");
        await Expect(page.GetByTestId("user").First).ToBeVisibleAsync();
        await shots.TakeAsync("06-people");

        await page.GotoAsync("/system");
        await Expect(page.GetByTestId("system-fact").First).ToBeVisibleAsync();
        await Expect(page.GetByTestId("restart")).ToBeVisibleAsync();
        await shots.TakeAsync("07-system", mask: page.Locator("[data-testid=system-fact][data-label=Host]"));
    }

    private sealed class Screens(WebScreenshotTests test, WebBrowser browser, string device)
    {
        private static readonly string[] Secrets = [TestApiKeys.Viewer, TestApiKeys.Operator, TestApiKeys.Admin, TestSecrets.Password];

        private readonly string _directory = Environment.GetEnvironmentVariable("WEB_SCREENS_OUT") is { Length: > 0 } output
            ? output
            : Path.Combine(test.TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "browser", "screens");

        public async Task TakeAsync(string screen, bool wholePage = true, ILocator? mask = null)
        {
            var page = browser.Page;

            // Every page says the roof is the emulator's, the sign-in page too.
            await Expect(browser.ModeBanner).ToHaveTextAsync(RoofStatusText.EmulatedHat);

            await page.EvaluateAsync("() => document.fonts.ready");

            var width = await page.EvaluateAsync<double>("() => document.scrollingElement.scrollWidth");
            var viewport = await page.EvaluateAsync<double>("() => document.documentElement.clientWidth");
            width.Should().BeLessThanOrEqualTo(viewport, "{0} must not scroll sideways on the {1}", screen, device);

            var content = await page.ContentAsync();
            content.Should().NotContainAny(Secrets, "{0} must not show a secret", screen);
            await Expect(page.GetByTestId("key-secret")).ToHaveCountAsync(0);
            if (Kept.TryGetValue(device, out var kept) && !kept.Contains(screen))
            {
                return;
            }

            // The whole page as a screen as tall as it shows it: a full-page capture of the device's own screen would draw
            // the fixed Stop bar over the middle of the page, where it is at the bottom of the screen.
            var screenSize = page.ViewportSize!;
            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            for (var i = 0; wholePage && i < 3; i++)
            {
                var height = await page.EvaluateAsync<int>("() => Math.ceil(document.documentElement.scrollHeight)");
                if (height <= page.ViewportSize!.Height)
                {
                    break;
                }

                await page.SetViewportSizeAsync(screenSize.Width, height);
                await browser.NextFrameAsync();
            }

            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, $"{screen}-{device}.jpg");
            await page.ScreenshotAsync(new()
            {
                Path = file,
                Type = ScreenshotType.Jpeg,
                Quality = 90,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Scale = ScreenshotScale.Css,
                Mask = mask is null ? [] : [mask],
                MaskColor = "#2b3035",
            });
            await page.SetViewportSizeAsync(screenSize.Width, screenSize.Height);
            browser.Log($"screenshot {file}");
            test.TestContext.AddResultFile(file);
        }
    }
}
