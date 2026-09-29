using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The buttons a person taps are big enough, and far enough apart, to hit with a finger on every screen the web UI is
/// tested on: at least 44 x 44 CSS pixels (the size WCAG 2.5.5, Apple and Google give for a touch target), and at least
/// 8 CSS pixels from the buttons next to them. Measured as the browser lays the page out, so a later style that shrinks
/// a button on one screen fails here.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebTouchTargetBrowserTests
{
    /// <summary>The smallest width and height of a button, in CSS pixels.</summary>
    public const double MinimumSize = 44;

    /// <summary>The least room between neighbouring buttons, in CSS pixels.</summary>
    public const double MinimumGap = 8;

    // The reconnect dialog's states other than Reconnecting, each with its own button.
    private static readonly string[] DialogStates =
    [
        "components-reconnect-failed",
        "components-reconnect-rejected",
        "components-reconnect-paused",
        "components-reconnect-resume-failed",
    ];

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

    /// <summary>
    /// Sign in and Stop on the sign-in page; Open, Close, Clear fault, Read status and Stop on the roof page (as an
    /// operator, so they are all offered); and the reconnect dialog's Stop, with the button of each of its states.
    /// </summary>
    [TestMethod]
    [DataRow(WebDevices.Phone)]
    [DataRow(WebDevices.PhoneLandscape)]
    [DataRow(WebDevices.SmallPhoneLandscape)]
    [DataRow(WebDevices.Tablet)]
    [DataRow(WebDevices.TabletLandscape)]
    [DataRow(WebDevices.Desktop)]
    public async Task EveryButtonAPersonTaps_IsBigEnough_AndClearOfItsNeighbours(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), device, cuttableConnection: true);
        var page = browser.Page;

        await page.GotoAsync("/signin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();
        await ExpectUsableAsync(device, "the sign-in page", [("Sign in", page.GetByTestId("sign-in")), ("Stop", browser.Stop)], neighbours: []);

        await browser.SignInAsync(WebBrowser.Operator);
        (string Name, ILocator Button)[] actions =
        [
            ("Open", browser.Open),
            ("Close", browser.Close),
            ("Clear fault", page.GetByTestId("clear-fault")),
            ("Read status", page.GetByTestId("refresh")),
        ];
        await ExpectUsableAsync(device, "the roof page", [.. actions, ("Stop", browser.Stop)], neighbours: actions);

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        await ExpectUsableAsync(device, "the reconnect dialog", [("the dialog's Stop", browser.DialogStop)], neighbours: []);

        // The other states' buttons, laid out by putting the dialog in each state (the page stays disconnected).
        foreach (var state in DialogStates)
        {
            await browser.ReconnectDialog.EvaluateAsync("(dialog, state) => { dialog.className = state; }", state);
            var button = browser.ReconnectDialog.Locator($".{state} .reconnect-actions .btn");
            await Expect(button).ToBeVisibleAsync();
            (string Name, ILocator Button)[] dialog = [($"the {state} button", button), ("the dialog's Stop", browser.DialogStop)];
            await ExpectUsableAsync(device, $"the reconnect dialog ({state})", dialog, neighbours: dialog);
        }
    }

    /// <summary>
    /// Each of <paramref name="buttons"/> is at least <see cref="MinimumSize"/> each way, and each two of
    /// <paramref name="neighbours"/> are at least <see cref="MinimumGap"/> apart.
    /// </summary>
    private async Task ExpectUsableAsync(
        string device,
        string where,
        (string Name, ILocator Button)[] buttons,
        (string Name, ILocator Button)[] neighbours)
    {
        await _browser!.NextFrameAsync();
        foreach (var (name, button) in buttons)
        {
            var box = await BoxOfAsync(button);
            box.Width.Should().BeGreaterThanOrEqualTo(MinimumSize, "{0} on {1} must be wide enough to tap on the {2}: it is at {3}", name, where, device, box);
            box.Height.Should().BeGreaterThanOrEqualTo(MinimumSize, "{0} on {1} must be tall enough to tap on the {2}: it is at {3}", name, where, device, box);
        }

        for (var i = 0; i < neighbours.Length; i++)
        {
            for (var j = i + 1; j < neighbours.Length; j++)
            {
                var (first, second) = (await BoxOfAsync(neighbours[i].Button), await BoxOfAsync(neighbours[j].Button));
                first.GapTo(second).Should().BeGreaterThanOrEqualTo(
                    MinimumGap,
                    "{0} and {1} on {2} must be far enough apart to tap one and not the other on the {3}: they are at {4} and {5}",
                    neighbours[i].Name,
                    neighbours[j].Name,
                    where,
                    device,
                    first,
                    second);
            }
        }
    }

    private static async Task<Box> BoxOfAsync(ILocator button)
    {
        var box = await button.EvaluateAsync<double[]>("button => { const r = button.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; }");
        return new Box(box[0], box[1], box[2], box[3]);
    }

    /// <summary>Where a button is laid out, in CSS pixels.</summary>
    private sealed record Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;

        public double Bottom => Top + Height;

        /// <summary>The room between this box and <paramref name="other"/>: across when they are side by side, down when one is above the other.</summary>
        public double GapTo(Box other)
            => Math.Max(Math.Max(other.Left - Right, Left - other.Right), Math.Max(other.Top - Bottom, Top - other.Bottom));

        public override string ToString() => $"({Left:0.#}, {Top:0.#}) {Width:0.#} x {Height:0.#}";
    }
}
