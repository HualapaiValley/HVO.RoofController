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
/// mode banner shown) with no secret in it: the key panel is never opened, and the controller's host name and addresses
/// are masked.
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

    /// <summary>
    /// Where the web UI is reached, as the deploy script would pass it (RoofHost__*), with a documentation address
    /// (RFC 5737): the System page shows these, never this machine's own addresses.
    /// </summary>
    private static readonly Dictionary<string, string?> DocumentedHost = new()
    {
        ["RoofHost:Addresses"] = "192.0.2.10",
        ["RoofHost:WebUiUrls"] = "https://192.0.2.10:8088/",
        ["RoofHost:WebUiAccess"] = "Network"
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
        var options = Scenario.Production(travelMeters: 2.0, settings: DocumentedHost) with { Camera = true };
        var browser = _browser = await WebBrowser.StartAsync(TestContext, options, device);
        var page = browser.Page;
        var shots = new Screens(this, browser, name);
        await browser.Rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = 30 });

        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        // The controller's status, and the mode banner with it, follow the streamed page.
        await Expect(page.GetByTestId("controller-status")).ToBeVisibleAsync();
        await shots.TakeAsync("01-signin");

        await browser.SignInAsync(WebBrowser.Admin);
        await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 15_000 });
        await Expect(browser.Position).ToHaveTextAsync("Closed");
        // #208: dim Stop while idle; Open offered in green, Close neutral while the roof is closed.
        await Expect(browser.Stop).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.StopButton));
        await Expect(browser.Open).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.MotionAvailable));
        await Expect(page.GetByTestId("close")).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.MotionDisabled));
        await Expect(page.GetByTestId("close")).ToHaveCSSAsync("opacity", "1");
        await shots.TakeAsync("02-roof");
        if (name == "desktop")
        {
            await page.EvaluateAsync("() => { window.MediaRecorder = undefined; }");
            await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync();
            await Expect(page.GetByTestId("camera-control-message")).ToContainTextAsync("Recording could not start");
            await shots.TakeAsync("14-camera-control-error");
            await page.ReloadAsync();
            await browser.ExpectLiveAsync();
            await Expect(browser.CameraStatus).ToHaveTextAsync("Live", new() { Timeout = 15_000 });
        }

        // A move shows the lease the page renews and the travel; Stop ends it before the images that follow.
        await browser.Open.ClickAsync();
        await browser.Rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        await browser.Rig.WaitForPlantAsync(p => p.PositionMeters > 0.5, "the roof to be well under way");
        await Expect(browser.Lease).ToBeVisibleAsync();
        // #208: bright Stop while the roof moves (it stays enabled); the running Open in bright green.
        await Expect(browser.Stop).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.StopButtonActive));
        await Expect(browser.Stop).ToBeEnabledAsync();
        await Expect(browser.Open).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.MotionRunning));
        await shots.TakeAsync("03-roof-opening");
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        await browser.Rig.WaitForRestAsync("the Stop after the opening image");
        await Expect(browser.Stop).ToHaveCSSAsync("background-color", WebBrowser.Rgb(RoofUiPalette.StopButton), new() { Timeout = 15_000 });

        await page.GotoAsync("/health");
        await Expect(page.GetByTestId("health-check").First).ToBeVisibleAsync();
        await Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync();
        await shots.TakeAsync("04-health");

        await page.GotoAsync("/settings");
        await Expect(page.GetByTestId("settings-group").First).ToBeVisibleAsync();
        // The first screen: the settings run on for several screens of fields.
        await shots.TakeAsync("05-settings", wholePage: false);

        // One person with both names, so the list shows them (#209); the names are synthetic.
        using (var admin = browser.AdminClient())
        {
            await admin.Identity.UpdateUserAsync(WebBrowser.Operator, new HVO.RoofControllerV4.Common.Models.RoofUserUpdateRequest
            {
                Role = HVO.RoofControllerV4.Common.Models.RoofControllerApiContract.OperatorRole,
                DisplayName = "Olga Example",
                ShortName = "Olga"
            });
        }

        await page.GotoAsync("/people");
        await Expect(page.GetByTestId("user").First).ToBeVisibleAsync();
        await shots.TakeAsync("06-people");

        await page.GotoAsync("/system");
        await Expect(page.Locator("[data-testid=system-summary] [data-testid=system-fact]").First).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=system-information] [data-testid=system-fact]").First).ToBeVisibleAsync();
        await Expect(page.GetByTestId("restart")).ToBeVisibleAsync();
        // The web UI's address and the host's are the documentation address above; checked before the image is taken,
        // so a setting that did not apply cannot publish this machine's. Its host name is masked.
        await Expect(page.Locator("[data-testid=system-summary] [data-testid=system-fact][data-label='Web UI']")).ToHaveTextAsync("https://192.0.2.10:8088/");
        await Expect(page.Locator("[data-testid=system-summary] [data-testid=system-fact][data-label=Addresses]")).ToHaveTextAsync("192.0.2.10");
        await shots.TakeAsync("07-system", mask: page.Locator("[data-testid=system-fact][data-label=Host]"));

        // The commands sent above: the Open and the Stop, by the admin from this web UI.
        await page.GotoAsync("/history");
        await Expect(page.GetByTestId("history-entry").First).ToBeVisibleAsync();
        await shots.TakeAsync("17-history");

        // Secondary workflows use the same real components; leave all private inputs empty.
        await page.GotoAsync("/people");
        await page.GetByTestId("user-add").ClickAsync();
        await shots.TakeAsync("09-person-editor", wholePage: false);
        await page.GotoAsync("/account/password");
        await shots.TakeAsync("11-password");
        // A person's own names (#209), typed but not saved; the names are synthetic.
        await page.GotoAsync("/account/names");
        await Expect(page.GetByTestId("names-form")).ToBeVisibleAsync();
        await page.GetByTestId("names-display-name").FillAsync("Ada Example");
        await page.GetByTestId("names-short-name").FillAsync("Ada");
        await page.GetByTestId("names-short-name").BlurAsync();
        await shots.TakeAsync("16-names");
        await page.GotoAsync("/settings");
        await page.Locator("[data-testid=settings-group][data-group=roof]").ClickAsync();
        var safety = page.Locator("[data-testid=setting][data-key$=':AtSpeedConfirmationTimeout']");
        await safety.GetByTestId("setting-change").ClickAsync();
        await page.GetByTestId("setting-input").FillAsync("");
        await page.GetByTestId("setting-save").ClickAsync();
        await Expect(page.GetByTestId("setting-review")).ToBeVisibleAsync();
        await page.GetByTestId("setting-review").ScrollIntoViewIfNeededAsync();
        await shots.TakeAsync("08-setting-review", wholePage: false, startAtTop: false);

    }

    [TestMethod]
    public async Task EnrolledLinuxAccess_IsCapturedWithoutCredentials()
    {
        using var directory = new TemporaryDirectory();
        var options = Scenario.Production(travelMeters: 2.0) with
        {
            Settings = new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
                ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true"
            }
        };
        var browser = _browser = await WebBrowser.StartAsync(TestContext, options, WebDevices.Desktop);
        using var admin = browser.AdminClient();
        await admin.Identity.EnrollPrincipalAsync(new HVO.RoofControllerV4.Common.Models.RoofPrincipalEnrollRequest
        {
            Subject = Guid.NewGuid().ToString(), EnrollmentStamp = Guid.NewGuid().ToString(), Name = "fixture-linux", Role = "RoofOperator"
        });
        var page = browser.Page;
        var shots = new Screens(this, browser, "desktop");
        await page.GotoAsync("/signin");
        await Expect(page.Locator("#signin-provider")).ToBeVisibleAsync();
        await shots.TakeAsync("13-provider-signin");
        await browser.SignInAsync(WebBrowser.Admin);
        await page.GotoAsync("/people");
        await page.Locator("[data-testid=people-tab][data-tab=linux]").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Read Linux users" }).ClickAsync();
        await Expect(page.GetByTestId("linux-user")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Role / access" }).ClickAsync();
        await Expect(page.GetByTestId("linux-role")).ToBeVisibleAsync();
        await shots.TakeAsync("10-linux-users");
        await page.GotoAsync("/account/keys");
        await Expect(page.Locator("#owned-name")).ToBeVisibleAsync();
        await shots.TakeAsync("12-personal-keys");
    }

    [TestMethod]
    public async Task ReconnectLandscape_KeepsStopVisible_BeforeScrolling()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Desktop, cuttableConnection: true);
        await browser.SignInAsync(WebBrowser.Viewer);
        await browser.Page.SetViewportSizeAsync(568, 320);
        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        await browser.ReconnectDialog.EvaluateAsync("dialog => { dialog.className = 'components-reconnect-resume-failed'; dialog.scrollTop = 0; }");
        await browser.NextFrameAsync();
        var placement = await browser.PlacementOfAsync(browser.DialogStop);
        placement.Top.Should().BeGreaterThanOrEqualTo(0);
        placement.Bottom.Should().BeLessThanOrEqualTo(320);
        placement.Reachable.Should().BeTrue("no scrolling or focus has exposed Stop");
        await Expect(browser.DialogStop).ToBeEnabledAsync();
        await browser.DialogStop.ClickAsync();
        await Expect(browser.ReconnectDialog.Locator("[data-web-stop-result]")).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        placement = await browser.PlacementOfAsync(browser.DialogStop);
        placement.Bottom.Should().BeLessThanOrEqualTo(320);
        placement.Reachable.Should().BeTrue("the actual answer cannot push Stop offscreen");
        await new Screens(this, browser, "landscape").TakeAsync("15-reconnect", wholePage: false);
    }

    private sealed class Screens(WebScreenshotTests test, WebBrowser browser, string device)
    {
        private static readonly string[] Secrets = [TestApiKeys.Viewer, TestApiKeys.Operator, TestApiKeys.Admin, TestSecrets.Password];

        private readonly string _directory = Environment.GetEnvironmentVariable("WEB_SCREENS_OUT") is { Length: > 0 } output
            ? output
            : Path.Combine(test.TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "browser", "screens");

        public async Task TakeAsync(string screen, bool wholePage = true, ILocator? mask = null, bool startAtTop = true)
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
            if (startAtTop) await page.EvaluateAsync("() => window.scrollTo(0, 0)");
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
