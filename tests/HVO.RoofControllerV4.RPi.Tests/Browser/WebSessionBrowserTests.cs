using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Security;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// The person's sign-in as a browser keeps it: the session cookie over HTTPS (a phone) and plain HTTP (a desktop), a
/// session that expires while its page is open, and forms and the live connection opened by a page of another origin.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class WebSessionBrowserTests
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
    [DataRow(WebDevices.Phone, DisplayName = "A phone, over HTTPS")]
    [DataRow(WebDevices.Desktop, DisplayName = "A desktop, over HTTP")]
    public async Task SignIn_KeepsTheSessionInAnHttpOnlyStrictSessionCookie_SentOverHttpsOnly_WhenThePageWasServedOverHttps(string device)
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), device);
        var sockets = new ConcurrentQueue<string>();
        browser.Page.WebSocket += (_, socket) => sockets.Enqueue(socket.Url);

        await browser.SignInAsync(WebBrowser.Operator);

        browser.WebAddress.Scheme.Should().Be(browser.Https ? "https" : "http");
        var cookies = await browser.Context.CookiesAsync();
        var cookie = cookies.Should().ContainSingle(c => c.Name == WebAuthentication.CookieName).Subject;
        cookie.HttpOnly.Should().BeTrue("the page's scripts never read the session");
        cookie.SameSite.Should().Be(SameSiteAttribute.Strict, "another site's requests do not carry it");
        cookie.Secure.Should().Be(browser.Https, "a page served over HTTPS keeps its cookie off plain HTTP");
        cookie.Expires.Should().Be(-1, "the cookie goes when the browser closes; the session's own expiry is inside it");
        sockets.Should().Contain(
            url => url.StartsWith(browser.Https ? "wss://" : "ws://", StringComparison.Ordinal) && url.Contains("/_blazor", StringComparison.Ordinal),
            "the live connection is as secure as the page");
    }

    /// <summary>
    /// A session that expires while its page is open (here after 20 s) signs the page out within the web UI's 30 s check,
    /// and the page says why. The cookie does not sign the person in again, a Stop from the signed-out page goes without
    /// their session, and signing in again gives them the roof back.
    /// </summary>
    [TestMethod]
    public async Task WhenTheSessionExpires_ThePageIsSignedOut_AndSaysWhy_AndSigningInAgainGivesTheRoofBack()
    {
        var options = Scenario.Production(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:SessionLifetime"] = "00:00:20",
        });
        var browser = _browser = await WebBrowser.StartAsync(TestContext, options, WebDevices.Phone);
        var page = browser.Page;
        await browser.SignInAsync(WebBrowser.Operator);

        await Expect(page).ToHaveURLAsync(new Regex(@"/signin\?message=ended$"), new() { Timeout = 60_000 });
        await Expect(page.GetByTestId("signin-message")).ToHaveTextAsync(SignInMessages.Describe(SignInMessages.Ended)!.Text);

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex(@"/signin\?returnUrl=%2F$"));
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.SignedOut);

        await browser.SubmitSignInAsync(WebBrowser.Operator, TestSecrets.Password);
        await browser.ExpectLiveAsync();
        await browser.Stop.ClickAsync();
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
    }

    /// <summary>
    /// A page of another origin that posts the web UI's forms (sign-out, Stop) or opens its live connection is refused
    /// by the origin check, and the person stays signed in.
    /// </summary>
    [TestMethod]
    public async Task APageOfAnotherOrigin_CannotPostTheForms_OrOpenTheLiveConnection_AndThePersonStaysSignedIn()
    {
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(), WebDevices.Phone);
        await browser.SignInAsync(WebBrowser.Operator);
        await using var other = await OtherSite.StartAsync(browser.WebAddress);
        var page = await browser.Context.NewPageAsync();

        foreach (var (button, path) in new[] { ("Sign out", WebAuthentication.SignOutPostPath), ("Stop", WebAuthentication.StopPostPath) })
        {
            await page.GotoAsync(other.Address.ToString());
            var answer = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync(),
                response => new Uri(response.Url).AbsolutePath == path);
            answer.Status.Should().Be(StatusCodes.Status403Forbidden, "{0} came from another origin", path);
            (await answer.TextAsync()).Should().Contain(OriginCheck.ProblemCode);
            await browser.WaitForWebLogAsync($"Refused cross-origin request POST {path} from 127.0.0.1 with origin {other.Origin}");
        }

        await page.GotoAsync(other.Address.ToString());
        var live = new UriBuilder(browser.WebAddress) { Scheme = browser.Https ? "wss" : "ws", Path = "/_blazor" }.Uri;
        var opened = await page.EvaluateAsync<string>(
            "url => new Promise(done => { const s = new WebSocket(url); s.onopen = () => done('open'); s.onerror = () => done('refused'); })",
            live.ToString());
        opened.Should().Be("refused");
        // Over HTTPS the browser opens a WebSocket with HTTP/2's CONNECT when it can; otherwise with an HTTP/1.1 GET.
        await browser.WaitForWebLogAsync(new Regex(
            $"^Refused cross-origin request (GET|CONNECT) /_blazor from 127\\.0\\.0\\.1 with origin {Regex.Escape(other.Origin)}$"));

        browser.WebLogs.Entries.Should().NotContain(e => e.Message.StartsWith("Web stop from", StringComparison.Ordinal));
        await browser.Page.ReloadAsync();
        await browser.ExpectLiveAsync();
        await Expect(browser.Page.GetByTestId("signed-in-role")).ToHaveTextAsync("Operator");
    }

    /// <summary>A page of another origin on loopback with the web UI's sign-out and Stop forms.</summary>
    private sealed class OtherSite : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private OtherSite(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public string Origin => Address.GetLeftPart(UriPartial.Authority);

        public static async Task<OtherSite> StartAsync(Uri web)
        {
            var html = $"""
                <!doctype html>
                <title>Another site</title>
                <form method="post" action="{new Uri(web, WebAuthentication.SignOutPostPath)}"><button>Sign out</button></form>
                <form method="post" action="{new Uri(web, WebAuthentication.StopPostPath)}"><button>Stop</button></form>
                """;
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapGet("/", () => Results.Content(html, "text/html"));
            await app.StartAsync();
            return new OtherSite(app, new Uri(app.Urls.Single()));
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
