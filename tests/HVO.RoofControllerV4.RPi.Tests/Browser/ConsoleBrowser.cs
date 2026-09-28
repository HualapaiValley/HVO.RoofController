using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Playwright;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The phone and the tablet the console is tested on, each held upright and sideways, and a small phone held sideways:
/// Playwright's device descriptors (viewport, scale, touch, mobile user agent), run in Chromium.
/// </summary>
internal static class ConsoleDevices
{
    /// <summary>390 x 664 CSS pixels.</summary>
    public const string Phone = "iPhone 13";

    /// <summary>750 x 342 CSS pixels.</summary>
    public const string PhoneLandscape = "iPhone 13 landscape";

    /// <summary>568 x 320 CSS pixels: short, and as narrow as the footer's narrow-screen layout (576 px or less).</summary>
    public const string SmallPhoneLandscape = "iPhone SE landscape";

    /// <summary>810 x 1080 CSS pixels.</summary>
    public const string Tablet = "iPad (gen 7)";

    /// <summary>1080 x 810 CSS pixels.</summary>
    public const string TabletLandscape = "iPad (gen 7) landscape";
}

/// <summary>
/// Where a control is in the viewport at the page's current scroll position, and whether a tap at its centre lands
/// on it (nothing, such as the fixed footer or a dialog, covers it there).
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
/// A headless Chromium on the console of an <see cref="EmulatedRoofRig"/> served on a loopback port, emulating one of
/// <see cref="ConsoleDevices"/> and recording a trace. Disposing it after a failed test saves a screenshot, the trace
/// and the browser's log to the test's results; then it closes the browser and stops the rig.
/// </summary>
/// <remarks>
/// The console's live connection (the Blazor circuit's WebSocket on <c>/_blazor</c>) runs through Playwright's
/// WebSocket route, so a test can cut it as a lost network does: Chromium's offline mode fails new requests but leaves
/// an open WebSocket connected. While the connection is cut, the circuit's reconnection requests fail too, so the
/// console stays disconnected (and its reconnect dialog shown) until <see cref="RestoreConnection"/>.
/// </remarks>
internal sealed class ConsoleBrowser : IAsyncDisposable
{
    /// <summary>How long an expectation waits for the console by default (Playwright's is 5 s).</summary>
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
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<(IWebSocketRoute Page, IWebSocketRoute Server)> _circuitSockets = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private volatile bool _connectionCut;

    private ConsoleBrowser(TestContext testContext, EmulatedRoofRig rig, string device)
    {
        _testContext = testContext;
        Rig = rig;
        Device = device;
    }

    public EmulatedRoofRig Rig { get; }

    public string Device { get; }

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("The browser has not started.");

    public IPage Page => _page ?? throw new InvalidOperationException("The browser has not started.");

    public ILocator Position => Page.GetByTestId("position");

    public ILocator Commanded => Page.GetByTestId("commanded");

    public ILocator Stop => Page.GetByTestId("stop");

    public ILocator Open => Page.GetByTestId("open");

    public ILocator Close => Page.GetByTestId("close");

    public ILocator StopOutcome => Page.GetByTestId("stop-outcome");

    /// <summary>The badges under the console's title (Initialized, Relay unverified, Lease Ns, the last stop's kind…).</summary>
    public ILocator StatusBadges => Page.Locator(".rc2-status-pill");

    /// <summary>The warnings above the controls (inputs unhealthy, relay register unverified, fault latched…).</summary>
    public ILocator Banners => Page.Locator(".rc2-banners");

    /// <summary>The footer's newest notification, "Title: Message" (shown in capitals by the stylesheet).</summary>
    public ILocator LatestNotification => Page.Locator(".global-footer__section--left .footer-chip");

    /// <summary>The footer's summary of the roof ("Roof: Closed • Emulated HAT").</summary>
    public ILocator FooterStatus => Page.GetByTestId("footer-status");

    public ILocator ReconnectDialog => Page.Locator("#components-reconnect-modal");

    /// <summary>The reconnect dialog's Stop, which posts <c>/console/stop</c> without the circuit (C15).</summary>
    public ILocator DialogStop => ReconnectDialog.GetByRole(AriaRole.Button, new() { Name = "Stop roof" });

    public ILocator DialogStopResult => ReconnectDialog.Locator("[data-console-stop-result]");

    public ILocator CameraStatus => Page.GetByTestId("camera-status");

    public ILocator CameraOverlay => Page.GetByTestId("camera-overlay");

    /// <summary>
    /// Starts the rig with <paramref name="options"/> on a loopback port and opens a browser emulating
    /// <paramref name="device"/> on it, not yet signed in.
    /// </summary>
    public static async Task<ConsoleBrowser> StartAsync(TestContext testContext, EmulatedRoofRigOptions options, string device)
    {
        var rig = await EmulatedRoofRig.StartAsync(options with { Kestrel = true });
        var browser = new ConsoleBrowser(testContext, rig, device);
        try
        {
            await browser.LaunchAsync();
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }

        return browser;
    }

    /// <summary>
    /// Opens the console as a signed-out visitor does (it sends them to the sign-in page), signs in with
    /// <paramref name="accessKey"/> and waits for the live console to show the roof.
    /// </summary>
    public async Task SignInAsync(string accessKey)
    {
        await Page.GotoAsync("/");
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"/login\?returnUrl=%2F$"));
        await SubmitAccessKeyAsync(accessKey);
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"^[^?]*/$"));

        // The console renders only once its circuit is live (no prerendering), so a position means it is interactive.
        await Assertions.Expect(Position).Not.ToBeEmptyAsync();
        await Assertions.Expect(StatusBadges).ToContainTextAsync("Initialized");
    }

    /// <summary>Enters <paramref name="accessKey"/> on the sign-in page and presses Sign in.</summary>
    public async Task SubmitAccessKeyAsync(string accessKey)
    {
        await Page.GetByLabel("Access key").FillAsync(accessKey);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
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
    /// Cuts the console's connection as a lost network does: both ends of the circuit's WebSocket close, and
    /// reconnection attempts fail, until <see cref="RestoreConnection"/>.
    /// </summary>
    public async Task CutConnectionAsync()
    {
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
            // The page's side first, with an abnormal code, so the console sees a lost connection (not a clean close).
            await page.CloseAsync(new() { Code = 4000, Reason = "Connection cut by the test" });
            await server.CloseAsync(new() { Code = 4000, Reason = "Connection cut by the test" });
        }
    }

    /// <summary>Lets the console's next reconnection attempt through.</summary>
    public void RestoreConnection()
    {
        lock (_gate)
        {
            _connectionCut = false;
        }

        Log("connection restored");
    }

    /// <summary>Waits for the reconnect dialog to cover the console, reconnecting.</summary>
    public async Task ExpectReconnectDialogAsync()
    {
        await Assertions.Expect(ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-show\b"));
        await Assertions.Expect(ReconnectDialog.GetByRole(AriaRole.Heading, new() { Name = "Reconnecting…" })).ToBeVisibleAsync();
        await Assertions.Expect(DialogStop).ToBeVisibleAsync();
    }

    /// <summary>Waits for the console to reconnect and its dialog to close.</summary>
    public async Task ExpectReconnectedAsync()
    {
        // Blazor retries at once ten times, then every 5 s (then every 30 s after 20 attempts).
        await Assertions.Expect(ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-hide\b"), new() { Timeout = 15_000 });
        await Assertions.Expect(DialogStop).ToBeHiddenAsync();
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
            await Rig.DisposeAsync();
        }
    }

    private async Task LaunchAsync()
    {
        Assertions.SetDefaultExpectTimeout((float)ExpectTimeout.TotalMilliseconds);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions(_playwright.Devices[Device])
        {
            BaseURL = Rig.BaseAddress.ToString()
        });
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
            .Concat(Rig.Logs.Serious.Select(e => $"host {e.Level} {e.Category}: {e.Message}")));
        files.Add(log);

        foreach (var file in files)
        {
            _testContext.AddResultFile(file);
        }

        _testContext.WriteLine($"Browser artifacts (open the trace with: pwsh playwright.ps1 show-trace <file>): {string.Join(", ", files)}");
    }
}
