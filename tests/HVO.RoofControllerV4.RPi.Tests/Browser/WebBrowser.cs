using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.RPi.Tests.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WebProgram = HVO.RoofControllerV4.Web.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The screens the web UI is tested on: a phone and a tablet, each held upright and sideways, a small phone held sideways
/// (Playwright's device descriptors: viewport, scale, touch, mobile user agent), and a desktop browser window. All run
/// in Chromium. The phones reach the web UI over HTTPS, as a phone on the observatory's network does; the tablets and
/// the desktop over plain HTTP, which the web UI also serves (<c>ALLOW_INSECURE_HTTP</c>).
/// </summary>
internal static class WebDevices
{
    /// <summary>390 x 664 CSS pixels.</summary>
    public const string Phone = "iPhone 13";

    /// <summary>750 x 342 CSS pixels.</summary>
    public const string PhoneLandscape = "iPhone 13 landscape";

    /// <summary>568 x 320 CSS pixels: short, and narrow enough for the narrow-screen Stop bar (576 px or less).</summary>
    public const string SmallPhoneLandscape = "iPhone SE landscape";

    /// <summary>810 x 1080 CSS pixels.</summary>
    public const string Tablet = "iPad (gen 7)";

    /// <summary>1080 x 810 CSS pixels.</summary>
    public const string TabletLandscape = "iPad (gen 7) landscape";

    /// <summary>A desktop browser window of 1440 x 900 CSS pixels, with a mouse.</summary>
    public const string Desktop = "Desktop 1440x900";

    public static IReadOnlyList<string> All { get; } = [Phone, PhoneLandscape, SmallPhoneLandscape, Tablet, TabletLandscape, Desktop];

    /// <summary>Whether the browser emulating <paramref name="device"/> reaches the web UI over HTTPS (the phones).</summary>
    public static bool UsesHttps(string device) => device is Phone or PhoneLandscape or SmallPhoneLandscape;

    public static BrowserNewContextOptions Options(IPlaywright playwright, string device)
        => device == Desktop
            ? new BrowserNewContextOptions { ViewportSize = new() { Width = 1440, Height = 900 }, DeviceScaleFactor = 1 }
            : new BrowserNewContextOptions(playwright.Devices[device]);
}

/// <summary>
/// Where a control is in the viewport at the page's current scroll position, and whether a tap at its centre lands
/// on it (nothing, such as a dialog, covers it there).
/// </summary>
internal sealed record ControlPlacement(
    double CenterX,
    double CenterY,
    double Top,
    double Bottom,
    double ViewportWidth,
    double ViewportHeight,
    double ScrollY,
    string? ElementAtCenter,
    bool Reachable)
{
    public override string ToString()
        => $"top {Top:0}, bottom {Bottom:0} in a {ViewportWidth:0} x {ViewportHeight:0} viewport scrolled to {ScrollY:0}; at its centre: {ElementAtCenter ?? "nothing (outside the viewport)"}";
}

/// <summary>
/// The web UI as it is deployed, driven from a headless Chromium: an <see cref="EmulatedRoofRig"/> (the controller
/// against the emulated plant) on a loopback port, and the web UI (<see cref="WebProgram.BuildApp"/>) on another,
/// reaching the controller over HTTP with its own Stop key. For a phone the web UI serves HTTPS with a certificate of its
/// own, which the browser accepts as a phone told to trust the Pi's certificate does (<see cref="WebDevices.UsesHttps"/>). The controller has three people with the test password:
/// <see cref="Admin"/>, <see cref="Operator"/> and <see cref="Viewer"/>. The browser emulates one of
/// <see cref="WebDevices"/> and records a trace. Disposing it after a failed test saves a screenshot, the trace and the
/// logs to the test's results; then it closes the browser and stops the web UI and the rig.
/// </summary>
/// <remarks>
/// In a browser started with <c>cuttableConnection</c>, the page's live connection (the Blazor circuit's WebSocket on
/// <c>/_blazor</c>) runs through Playwright's WebSocket route, so a test can cut it as a lost network does: Chromium's
/// offline mode fails new requests but leaves an open WebSocket connected. While the connection is cut, the circuit's
/// reconnection requests fail too, so the page stays disconnected (and its reconnect dialog shown) until
/// <see cref="RestoreConnection"/>. Such a test stays on the page it opened: Playwright 1.62's .NET client fails when a
/// routed WebSocket is closed by the page navigating away.
/// </remarks>
internal sealed class WebBrowser : IAsyncDisposable
{
    public const string Admin = "ada";

    public const string Operator = "olga";

    public const string Viewer = "vic";

    /// <summary>The name of the controller's API key the web UI sends Stop with once a session has ended.</summary>
    public const string StopKeyName = "web-stop";

    /// <summary>How long an expectation waits for the page by default (Playwright's is 5 s).</summary>
    public static readonly TimeSpan ExpectTimeout = TimeSpan.FromSeconds(10);

    private const string PlacementScript = """
        element => {
          const r = element.getBoundingClientRect();
          const x = r.left + r.width / 2;
          const y = r.top + r.height / 2;
          const inside = x >= 0 && y >= 0 && x < window.innerWidth && y < window.innerHeight;
          const hit = inside ? document.elementFromPoint(x, y) : null;
          return {
            centerX: x, centerY: y, top: r.top, bottom: r.bottom,
            viewportWidth: window.innerWidth, viewportHeight: window.innerHeight, scrollY: window.scrollY,
            elementAtCenter: hit ? `${hit.tagName.toLowerCase()}${hit.id ? '#' + hit.id : ''}.${[...hit.classList].join('.')}` : null,
            reachable: hit !== null && (hit === element || element.contains(hit))
          };
        }
        """;

    // Playwright 1.62's .NET client fails (and loses its connection to the browser) when a routed page's WebSocket is
    // closed without a code or reason, as SignalR closes it; this gives such calls the values a browser reports.
    private const string CloseDefaultsScript = """
        (() => {
          const patch = type => {
            if (typeof type !== 'function' || !type.prototype || type.prototype.__closeDefaults) return type;
            const close = type.prototype.close;
            type.prototype.close = function (code, reason) { return close.call(this, code ?? 1000, reason ?? ''); };
            type.prototype.__closeDefaults = true;
            return type;
          };
          let current = patch(globalThis.WebSocket);
          Object.defineProperty(globalThis, 'WebSocket', { configurable: true, get: () => current, set: value => { current = patch(value); } });
        })();
        """;

    private readonly TestContext _testContext;
    // The web UI's Stop key file, data protection keys and certificate.
    private readonly WebTestSupport.TempDirectory _directory = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly List<(IWebSocketRoute Page, IWebSocketRoute Server)> _circuitSockets = [];
    private WebApplication? _web;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private volatile bool _connectionCut;

    private WebBrowser(TestContext testContext, EmulatedRoofRig rig, string device, bool cuttableConnection)
    {
        _testContext = testContext;
        Rig = rig;
        Device = device;
        CuttableConnection = cuttableConnection;
    }

    public EmulatedRoofRig Rig { get; }

    public string Device { get; }

    /// <summary>Whether <see cref="CutConnectionAsync"/> can cut the page's live connection.</summary>
    public bool CuttableConnection { get; }

    /// <summary>Whether the web UI serves the browser over HTTPS (<see cref="WebDevices.UsesHttps"/>).</summary>
    public bool Https => WebDevices.UsesHttps(Device);

    /// <summary>The web UI's address, which the browser's relative URLs go to.</summary>
    public Uri WebAddress { get; private set; } = null!;

    /// <summary>What the web UI has logged.</summary>
    public RecordingLoggerProvider WebLogs { get; } = new();

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("The browser has not started.");

    public IPage Page => _page ?? throw new InvalidOperationException("The browser has not started.");

    /// <summary>The roof page (<c>Dashboard.razor</c>); <c>data-feed</c> names where its status comes from.</summary>
    public ILocator Dashboard => Page.GetByTestId("dashboard");

    public ILocator Position => Page.GetByTestId("position");

    public ILocator Commanded => Page.GetByTestId("commanded");

    public ILocator Open => Page.GetByTestId("open");

    public ILocator Close => Page.GetByTestId("close");

    /// <summary>The Stop bar's button, at the bottom of every page (<c>App.razor</c>).</summary>
    public ILocator Stop => Page.GetByTestId("stop");

    /// <summary>What the Stop bar says the controller answered.</summary>
    public ILocator StopOutcome => Page.GetByTestId("stop-outcome");

    /// <summary>The banners above the roof page's controls (the feed, a latched fault, relays and inputs unverified).</summary>
    public ILocator Banners => Page.Locator(".rc2-banners");

    /// <summary>The roof page's notices, newest first.</summary>
    public ILocator Notices => Page.GetByTestId("notice");

    /// <summary>The badge the roof page shows while it renews the operator lease.</summary>
    public ILocator Lease => Page.GetByTestId("lease");

    /// <summary>The warning on every page that the controller is not driving the roof as in normal use.</summary>
    public ILocator ModeBanner => Page.GetByTestId("mode-banner");

    public ILocator ReconnectDialog => Page.Locator("#components-reconnect-modal");

    /// <summary>The reconnect dialog's Stop, which posts <c>/stop</c> without the live connection (C15).</summary>
    public ILocator DialogStop => Page.GetByTestId("reconnect-stop");

    public ILocator DialogStopResult => ReconnectDialog.Locator(".reconnect-stop-result");

    public ILocator CameraStatus => Page.GetByTestId("camera-status");

    public ILocator CameraOverlay => Page.GetByTestId("camera-overlay");

    /// <summary>
    /// Starts the rig with <paramref name="options"/> and the web UI (with <paramref name="webSettings"/> as well), each
    /// on a loopback port, and opens a browser emulating <paramref name="device"/> on the web UI, not yet signed in; with
    /// <paramref name="cuttableConnection"/>, a test can cut the page's live connection; <paramref name="webServices"/>
    /// adds to the web UI's services.
    /// </summary>
    public static async Task<WebBrowser> StartAsync(
        TestContext testContext,
        EmulatedRoofRigOptions options,
        string device,
        IReadOnlyDictionary<string, string?>? webSettings = null,
        bool cuttableConnection = false,
        Action<IServiceCollection>? webServices = null)
    {
        var rig = await EmulatedRoofRig.StartAsync(options with { Kestrel = true });
        var browser = new WebBrowser(testContext, rig, device, cuttableConnection);
        try
        {
            await browser.StartWebAsync(webSettings, webServices);
            await browser.LaunchAsync();
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }

        return browser;
    }

    /// <summary>A client of the controller with the test admin key, as an admin's tools reach it.</summary>
    public RoofControllerClient AdminClient()
        => new(new RoofConnectionOptions { BaseAddress = Rig.BaseAddress, Credential = new RoofApiKeyCredential(TestApiKeys.Admin) });

    /// <summary>
    /// Opens the roof page as a signed-out visitor does (it sends them to the sign-in page), signs in as
    /// <paramref name="name"/> with the test password and waits for the live page to show the roof.
    /// </summary>
    public async Task SignInAsync(string name)
    {
        await Page.GotoAsync("/");
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"/signin\?returnUrl=%2F$"));
        await SubmitSignInAsync(name, TestSecrets.Password);
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"^[^?]*/$"));
        await ExpectLiveAsync();
    }

    /// <summary>Enters <paramref name="name"/> and <paramref name="password"/> on the sign-in page and presses Sign in.</summary>
    public async Task SubmitSignInAsync(string name, string password)
    {
        await Page.GetByLabel("Name").FillAsync(name);
        await Page.GetByLabel("Password").FillAsync(password);
        await Page.GetByTestId("sign-in").ClickAsync();
    }

    /// <summary>Waits for the roof page's status to come from the live feed.</summary>
    public async Task ExpectLiveAsync()
    {
        await Assertions.Expect(Dashboard).ToHaveAttributeAsync("data-feed", "live", new() { Timeout = 15_000 });
        await Assertions.Expect(Position).Not.ToHaveTextAsync("Unknown");
    }

    /// <summary>Where <paramref name="control"/> is at the page's current scroll position, without scrolling to it.</summary>
    public async Task<ControlPlacement> PlacementOfAsync(ILocator control)
    {
        var p = await control.EvaluateAsync<JsonElement>(PlacementScript);
        return new ControlPlacement(
            p.GetProperty("centerX").GetDouble(),
            p.GetProperty("centerY").GetDouble(),
            p.GetProperty("top").GetDouble(),
            p.GetProperty("bottom").GetDouble(),
            p.GetProperty("viewportWidth").GetDouble(),
            p.GetProperty("viewportHeight").GetDouble(),
            p.GetProperty("scrollY").GetDouble(),
            p.GetProperty("elementAtCenter").ValueKind == JsonValueKind.Null ? null : p.GetProperty("elementAtCenter").GetString(),
            p.GetProperty("reachable").GetBoolean());
    }

    /// <summary>
    /// Waits until the browser has drawn two more frames: a ResizeObserver (the Stop bar's room at the end of the page)
    /// answers a change of size when the next frame is drawn, not when the change is made.
    /// </summary>
    public Task NextFrameAsync()
        => Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

    /// <summary>
    /// Taps the centre of <paramref name="placement"/> on a touchscreen device, or clicks it with the mouse on the
    /// desktop (which has no touchscreen).
    /// </summary>
    public Task TapAsync(ControlPlacement placement)
        => Device == WebDevices.Desktop
            ? Page.Mouse.ClickAsync((float)placement.CenterX, (float)placement.CenterY)
            : Page.Touchscreen.TapAsync((float)placement.CenterX, (float)placement.CenterY);

    /// <summary>
    /// Cuts the page's live connection as a lost network does: both ends of the circuit's WebSocket close, and
    /// reconnection attempts fail, until <see cref="RestoreConnection"/>.
    /// </summary>
    public async Task CutConnectionAsync()
    {
        if (!CuttableConnection)
        {
            throw new InvalidOperationException("Start the browser with cuttableConnection to cut the page's live connection.");
        }

        List<(IWebSocketRoute Page, IWebSocketRoute Server)> open;
        lock (_gate)
        {
            _connectionCut = true;
            open = [.. _circuitSockets];
            _circuitSockets.Clear();
        }

        Log($"connection cut ({open.Count} circuit socket(s) open)");
        foreach (var (page, server) in open)
        {
            // The page's side first, with an abnormal code, so the page sees a lost connection (not a clean close).
            await page.CloseAsync(new() { Code = 4000, Reason = "Connection cut by the test" });
            await server.CloseAsync(new() { Code = 4000, Reason = "Connection cut by the test" });
        }
    }

    /// <summary>Lets the page's next reconnection attempt through.</summary>
    public void RestoreConnection()
    {
        lock (_gate)
        {
            _connectionCut = false;
        }

        Log("connection restored");
    }

    /// <summary>Waits for the reconnect dialog to cover the page, reconnecting.</summary>
    public async Task ExpectReconnectDialogAsync()
    {
        await Assertions.Expect(ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-show\b"));
        await Assertions.Expect(ReconnectDialog.GetByRole(AriaRole.Heading, new() { Name = "Reconnecting…" })).ToBeVisibleAsync();
        await Assertions.Expect(DialogStop).ToBeVisibleAsync();
    }

    /// <summary>Waits for the page to reconnect and its dialog to close.</summary>
    public async Task ExpectReconnectedAsync()
    {
        // Blazor retries at once ten times, then every 5 s (then every 30 s after 20 attempts).
        await Assertions.Expect(ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-hide\b"), new() { Timeout = 15_000 });
        await Assertions.Expect(DialogStop).ToBeHiddenAsync();
    }

    /// <summary>Waits for the web UI to log <paramref name="message"/>.</summary>
    public Task WaitForWebLogAsync(string message)
        => WaitForWebLogAsync(logged => string.Equals(logged, message, StringComparison.Ordinal), $"\"{message}\"");

    /// <summary>Waits for the web UI to log a message that <paramref name="pattern"/> matches.</summary>
    public Task WaitForWebLogAsync(Regex pattern)
        => WaitForWebLogAsync(pattern.IsMatch, $"a message matching /{pattern}/");

    private async Task WaitForWebLogAsync(Func<string, bool> match, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!WebLogs.Entries.Any(e => match(e.Message)))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"The web UI did not log {description} within 5 s.");
            }

            await Task.Delay(50);
        }
    }

    public void Log(string message) => _log.Enqueue($"{_clock.Elapsed.TotalSeconds,8:0.000}s {message}");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_context is not null)
            {
                if (_testContext.CurrentTestOutcome == UnitTestOutcome.Passed)
                {
                    await _context.Tracing.StopAsync();
                }
                else
                {
                    await SaveFailureArtifactsAsync(_context);
                }
            }
        }
        catch (Exception ex)
        {
            _testContext.WriteLine($"Could not save the browser's artifacts: {ex.Message}");
        }
        finally
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync();
            }

            _playwright?.Dispose();
            if (_web is not null)
            {
                await _web.DisposeAsync();
            }

            await Rig.DisposeAsync();
            _directory.Dispose();
        }
    }

    // The web UI as the container's supervisor starts it (Production), with the people and the Stop key an admin sets up.
    private async Task StartWebAsync(IReadOnlyDictionary<string, string?>? webSettings, Action<IServiceCollection>? webServices)
    {
        using (var admin = AdminClient())
        {
            foreach (var (name, role) in new[]
            {
                (Admin, RoofControllerApiContract.AdminRole),
                (Operator, RoofControllerApiContract.OperatorRole),
                (Viewer, RoofControllerApiContract.ViewerRole),
            })
            {
                await admin.Identity.AddUserAsync(new RoofUserCreateRequest { Name = name, Role = role, Password = TestSecrets.Password });
            }

            var stopKey = await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = StopKeyName, Role = RoofControllerApiContract.ViewerRole });
            await File.WriteAllTextAsync(_directory.File("stop-key"), stopKey.Secret + "\n");
        }

        Directory.CreateDirectory(_directory.File("keys"));
        var args = new List<string>
        {
            "--environment=Production",
            $"--RoofWeb:Urls={(Https ? "https" : "http")}://127.0.0.1:0",
            $"--RoofWeb:ControllerUrl={Rig.BaseAddress}",
            $"--RoofWeb:StopKeyFile={_directory.File("stop-key")}",
            $"--RoofWeb:DataProtectionPath={_directory.File("keys")}",
        };
        if (Https)
        {
            var (certificate, passwordFile) = WriteCertificate();
            args.Add($"--RoofWeb:Certificate:Path={certificate}");
            args.Add($"--RoofWeb:Certificate:PasswordFile={passwordFile}");
        }

        args.AddRange((webSettings ?? new Dictionary<string, string?>()).Select(setting => $"--{setting.Key}={setting.Value}"));

        _web = WebProgram.BuildApp([.. args], builder =>
        {
            // The scripts and styles are the build's static web assets, which a host serves by itself only in
            // Development (a published image has them in wwwroot).
            builder.WebHost.UseStaticWebAssets();
            builder.Logging.AddProvider(WebLogs);
            webServices?.Invoke(builder.Services);
        });
        await _web.StartAsync();
        WebAddress = new Uri(_web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Log($"web UI at {WebAddress}, controller at {Rig.BaseAddress}");
    }

    // The web UI's certificate for 127.0.0.1, with a password, as the supervisor gives it the controller's.
    private (string Certificate, string PasswordFile) WriteCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        File.WriteAllBytes(_directory.File("web.pfx"), certificate.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(_directory.File("web.pfx.password"), password + "\n");
        return (_directory.File("web.pfx"), _directory.File("web.pfx.password"));
    }

    private async Task LaunchAsync()
    {
        Assertions.SetDefaultExpectTimeout((float)ExpectTimeout.TotalMilliseconds);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        var options = WebDevices.Options(_playwright, Device);
        options.BaseURL = WebAddress.ToString();
        options.IgnoreHTTPSErrors = Https;
        _context = await _browser.NewContextAsync(options);
        await _context.Tracing.StartAsync(new() { Title = _testContext.TestDisplayName, Screenshots = true, Snapshots = true });

        _page = await _context.NewPageAsync();
        await _page.AddInitScriptAsync(CloseDefaultsScript);
        _page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                Log($"console {message.Type}: {message.Text}");
            }
        };
        _page.PageError += (_, error) => Log($"page error: {error}");
        _page.RequestFailed += (_, request) => Log($"request failed: {request.Method} {request.Url} ({request.Failure})");
        if (!CuttableConnection)
        {
            return;
        }

        await _page.RouteWebSocketAsync(IsCircuitUrl, socket =>
        {
            lock (_gate)
            {
                if (_connectionCut)
                {
                    Log($"circuit WebSocket refused: {socket.Url}");
                    _ = socket.CloseAsync(new() { Code = 4000, Reason = "Connection cut by the test" });
                    return;
                }

                _circuitSockets.Add((socket, socket.ConnectToServer()));
            }

            Log($"circuit WebSocket connected: {socket.Url}");
        });
        await _page.RouteAsync(IsCircuitUrl, route => _connectionCut ? route.AbortAsync("internetdisconnected") : route.FallbackAsync());
    }

    private static bool IsCircuitUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith("/_blazor", StringComparison.Ordinal);

    private async Task SaveFailureArtifactsAsync(IBrowserContext context)
    {
        var directory = Path.Combine(_testContext.TestRunResultsDirectory ?? Path.GetTempPath(), "browser");
        Directory.CreateDirectory(directory);
        var name = Regex.Replace(_testContext.TestDisplayName ?? _testContext.TestName ?? "browser-test", @"[^A-Za-z0-9_.-]+", "-").Trim('-');
        var files = new List<string>();

        if (_page is { IsClosed: false } page)
        {
            var screenshot = Path.Combine(directory, $"{name}.png");
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
            files.Add(screenshot);
        }

        var trace = Path.Combine(directory, $"{name}-trace.zip");
        await context.Tracing.StopAsync(new() { Path = trace });
        files.Add(trace);

        var log = Path.Combine(directory, $"{name}-browser.log");
        await File.WriteAllLinesAsync(log, _log
            .Append($"controller: {Rig.Controller.GetCurrentStatusSnapshot()}")
            .Append($"plant: {Rig.Plant}")
            .Concat(Rig.Logs.Serious.Select(e => $"controller {e.Level} {e.Category}: {e.Message}"))
            .Concat(WebLogs.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => $"web {e.Level} {e.Category}: {e.Message}")));
        files.Add(log);

        foreach (var file in files)
        {
            _testContext.AddResultFile(file);
        }

        _testContext.WriteLine($"Browser artifacts (open the trace with: pwsh playwright.ps1 show-trace <file>): {string.Join(", ", files)}");
    }
}
