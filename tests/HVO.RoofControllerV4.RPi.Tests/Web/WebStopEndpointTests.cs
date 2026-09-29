using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web.Roof;
using HVO.RoofControllerV4.Web.Security;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// Every page's Stop (<c>POST /stop</c>): sent for the person with their session, for a signed-out page with no
/// credential, and refused without the page's form token. Its answer is what the controller said, in the shared words.
/// </summary>
[TestClass]
public sealed partial class WebStopEndpointTests
{
    private static readonly HttpMethod Post = HttpMethod.Post;

    [TestMethod]
    public async Task ASignedInPage_SendsStopWithThePersonsSession_AndShowsTheControllersAnswer()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);

        var (status, answer) = await StopAsync(browser, await browser.GetFormTokenAsync("/"));

        status.Should().Be(HttpStatusCode.OK);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Acknowledged), RoofStopText.AcknowledgedVerified));
        var sent = host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().ContainSingle().Subject;
        sent.Authorization.Should().Be("Bearer token-1-olga");
        sent.HasApiKey.Should().BeFalse("the web UI has no Stop key here");
        host.Sessions.TryFind("session-1", out var session).Should().BeTrue();
        session.StopsSent.Should().Be(1, "every page of the person hears of it");
    }

    [TestMethod]
    public async Task ASignedOutPage_SendsStopWithoutACredential_AndTheControllerDecides()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();

        var (status, answer) = await StopAsync(browser, await browser.GetFormTokenAsync("/signin"));

        status.Should().Be(HttpStatusCode.OK);
        answer.Outcome.Should().Be(nameof(RoofStopOutcome.Acknowledged));
        var sent = host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().ContainSingle().Subject;
        sent.Authorization.Should().BeNull();
        sent.HasApiKey.Should().BeFalse();
    }

    [TestMethod]
    public async Task ARelayRegisterThatCouldNotBeVerified_IsNotCalledAcknowledged()
    {
        var unverified = true;
        await using var host = await StartAsync(_ => unverified
            ? Stopped(RoofRelayRegisterState.Unverified)
            : FakeController.Problem(HttpStatusCode.ServiceUnavailable, RoofControllerErrorCode.RelayStateUnverified));
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);
        var token = await browser.GetFormTokenAsync("/");

        var accepted = await StopAsync(browser, token);
        unverified = false;
        var refused = await StopAsync(browser, token);

        accepted.Should().Be((HttpStatusCode.ServiceUnavailable, new WebStopResponse(nameof(RoofStopOutcome.RelayUnverified), RoofStopText.AcknowledgedUnverified)));
        refused.Should().Be((HttpStatusCode.ServiceUnavailable, new WebStopResponse(nameof(RoofStopOutcome.RelayUnverified), RoofStopText.SentUnverified)));
    }

    [TestMethod]
    public async Task AControllerThatCannotBeReached_IsAFailure_ThatSendsThePersonToTheRoof()
    {
        await using var host = await StartAsync(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);

        var (status, answer) = await StopAsync(browser, await browser.GetFormTokenAsync("/"));

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.Failed(RoofText.Unreachable)));
    }

    [TestMethod]
    public async Task AnErrorInTheWebUi_IsAFailure_ThatSendsThePersonToTheRoof()
    {
        await using var host = await StartAsync(_ => throw new InvalidOperationException("broken"));
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);

        var (status, answer) = await StopAsync(browser, await browser.GetFormTokenAsync("/"));

        status.Should().Be(HttpStatusCode.InternalServerError);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.Failed(RoofText.DescribeFailure(new InvalidOperationException()))));
        answer.Message.Should().NotContain("broken", "an exception's own text is for the log");
    }

    [TestMethod]
    public async Task ASessionTheControllerEnded_StillReachesStop_AndThePageIsToldItIsSignedOut()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);
        var token = await browser.GetFormTokenAsync("/");
        host.Controller.EndSession("token-1-olga");

        var (status, answer) = await StopAsync(browser, token);

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.PageSignedOut));
        host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().ContainSingle().Which.Authorization.Should().Be("Bearer token-1-olga");
    }

    [TestMethod]
    public async Task WithAStopKey_StopIsSentForThePerson_EvenForASessionTheWebUiNoLongerHolds()
    {
        using var directory = new WebTestSupport.TempDirectory();
        await File.WriteAllTextAsync(directory.File("stop-key"), "stop-key-0123456789abcdef\n");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { OtherAnswer = request => IsStop(request) ? Stopped() : null };
        await using var host = await WebHost.StartAsync(
            [$"--RoofWeb:StopKeyFile={directory.File("stop-key")}"],
            controller,
            builder => builder.Services.AddSingleton<TimeProvider>(clock));
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);
        var token = await browser.GetFormTokenAsync("/");

        // The session's last moment: its cookie is still good, but the web UI has already let it go.
        clock.AdvanceWithoutTimers(TimeSpan.FromHours(12));
        host.Sessions.Sweep();
        host.Sessions.TryFind("session-1", out _).Should().BeFalse();

        var (status, answer) = await StopAsync(browser, token);

        status.Should().Be(HttpStatusCode.OK);
        answer.Outcome.Should().Be(nameof(RoofStopOutcome.Acknowledged));
        var sent = controller.Logged(Post, RoofApiRoutesTest.Stop).Should().ContainSingle().Subject;
        sent.Authorization.Should().Be("Bearer token-1-olga");
        sent.HasApiKey.Should().BeTrue();
    }

    [TestMethod]
    public async Task WithoutThePagesToken_ASignedInPage_IsOutOfDate_AndNothingIsSent()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);

        var (status, answer) = await StopAsync(browser, token: null);

        status.Should().Be(HttpStatusCode.BadRequest);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.PageOutOfDate));
        host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().BeEmpty();
    }

    [TestMethod]
    public async Task WithoutThePagesToken_ASignedOutPage_IsSignedOut_AndNothingIsSent()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();

        var (status, answer) = await StopAsync(browser, token: null);

        status.Should().Be(HttpStatusCode.Unauthorized);
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.PageSignedOut));
        host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().BeEmpty();
    }

    [TestMethod]
    public async Task APageSignedOutSinceItWasShown_IsToldSo_AndNothingIsSent()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);
        var token = await browser.GetFormTokenAsync("/");
        (await browser.PostFormAsync("/account/signout", new Dictionary<string, string>())).Dispose();

        var (status, answer) = await StopAsync(browser, token);

        status.Should().Be(HttpStatusCode.Unauthorized, "the page's token was for olga, and nobody is signed in now");
        answer.Should().Be(new WebStopResponse(nameof(RoofStopOutcome.Failed), RoofStopText.PageSignedOut));
        host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().BeEmpty();
    }

    [TestMethod]
    public async Task AStopFromAnotherSite_IsRefused_WithACodeThePageHasWordsFor()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();
        await browser.SignInAsync("olga", FakeController.AdaPassword);
        var token = await browser.GetFormTokenAsync("/");

        using var response = await browser.PostFormAsync(
            WebAuthentication.StopPostPath,
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token },
            origin: "https://elsewhere.example");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = problem.RootElement.GetProperty(RoofControllerApiContract.ProblemCodeExtension).GetString();
        code.Should().Be(OriginCheck.ProblemCode);
        Texts().GetProperty("codes").GetProperty(code!).GetString().Should().Be(RoofStopText.Failed(WebStopTexts.OriginRefused));
        host.Controller.Logged(Post, RoofApiRoutesTest.Stop).Should().BeEmpty();
    }

    [TestMethod]
    public void Texts_HaveEveryMessageTheScriptShows_InTheSharedWords()
    {
        var texts = Texts();

        texts.GetProperty("timeoutMilliseconds").GetInt32().Should().Be(25_000);
        texts.GetProperty("sending").GetString().Should().Be(RoofStopText.Sending);
        texts.GetProperty("signedOut").GetString().Should().Be(RoofStopText.PageSignedOut);
        texts.GetProperty("timedOut").GetString().Should().Be(RoofStopText.Failed(WebStopTexts.TimedOut));
        texts.GetProperty("unreachable").GetString().Should().Be(RoofStopText.Failed(WebStopTexts.Unreachable));
        texts.GetProperty("serverError").GetString().Should().Be(RoofStopText.Failed(RoofText.DescribeRefusal(500, null, null)));
        texts.GetProperty("other").GetString().Should().Contain("(HTTP {status})").And.NotContain("HTTP 0").And.StartWith("Stop failed: ");

        var codes = texts.GetProperty("codes").EnumerateObject().ToDictionary(code => code.Name, code => code.Value.GetString());
        codes.Keys.Should().BeEquivalentTo(Enum.GetNames<RoofControllerErrorCode>().Append(OriginCheck.ProblemCode));
        codes[nameof(RoofControllerErrorCode.FaultLatched)].Should().Be(RoofStopText.Failed(RoofText.DescribeRefusal(0, RoofControllerErrorCode.FaultLatched, null)));

        var statuses = texts.GetProperty("statuses").EnumerateObject().ToDictionary(status => status.Name, status => status.Value.GetString());
        statuses.Keys.Should().Equal("400", "403", "404", "408", "429", "503");
        statuses["429"].Should().Be(RoofStopText.Failed(RoofText.DescribeRefusal(429, null, null)));

        codes.Values.Concat(statuses.Values).Append(texts.GetProperty("other").GetString())
            .Should().OnlyContain(text => text!.StartsWith("Stop failed: ", StringComparison.Ordinal) && text.EndsWith(RoofStopText.UseRoofStop, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThePage_WaitsLongerThanTheWebUiWaitsForTheController()
    {
        // The web UI may wait for the controller twice: once on the session's client, again on a new one (WebSession).
        WebStopTexts.PageTimeout.Should().BeGreaterThan(new RoofConnectionOptions { BaseAddress = new Uri("http://localhost") }.StopTimeout * 2);
    }

    [TestMethod]
    public async Task EveryPage_HasTheStopBar_AndTheReconnectDialogsStop_WithTheTexts()
    {
        await using var host = await StartAsync(_ => Stopped());
        using var browser = host.Browser();

        foreach (var page in new[] { "/signin", "/" })
        {
            if (page == "/")
            {
                await browser.SignInAsync("olga", FakeController.AdaPassword);
            }

            using var response = await browser.GetAsync(page);
            var html = await response.Content.ReadAsStringAsync();

            var forms = StopFormPattern().Matches(html).Select(form => form.Value).ToList();
            forms.Should().HaveCount(2, $"{page} has the Stop bar and the reconnect dialog's Stop");
            forms.Should().OnlyContain(form => form.Contains("action=\"stop\"", StringComparison.Ordinal)
                && form.Contains("__RequestVerificationToken", StringComparison.Ordinal)
                && form.Contains(RoofStopText.ButtonLabel, StringComparison.Ordinal)
                && !form.Contains("disabled", StringComparison.Ordinal));
            TextsPattern().Matches(html).Select(texts => WebUtility.HtmlDecode(texts.Groups[1].Value)).ToList()
                .Should().HaveCount(2).And.OnlyContain(texts => texts == WebStopTexts.Json);
            html.Should().Contain("js/stop.");
        }
    }

    [TestMethod]
    public async Task ThePagesStylesAndScripts_AreServed_TheThemeIncluded()
    {
        // The files are where the build put them (as in development); a published web UI has them in its own wwwroot.
        await using var host = await WebHost.StartAsync(customize: builder => builder.WebHost.UseStaticWebAssets());
        using var browser = host.Browser();
        using var page = await browser.GetAsync("/signin");
        var html = await page.Content.ReadAsStringAsync();

        var assets = AssetPattern().Matches(html).Select(asset => asset.Groups[1].Value).ToList();

        assets.Should().Contain(asset => asset.Contains("hvo-dark", StringComparison.Ordinal))
            .And.Contain(asset => asset.Contains("js/stop", StringComparison.Ordinal))
            .And.Contain(asset => asset.Contains("css/app", StringComparison.Ordinal));
        foreach (var asset in assets)
        {
            using var response = await browser.GetAsync("/" + asset);
            response.StatusCode.Should().Be(HttpStatusCode.OK, asset);
            (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty(asset);
        }

        using var theme = await browser.GetAsync("/" + assets.Single(asset => asset.Contains("hvo-dark", StringComparison.Ordinal)));
        (await theme.Content.ReadAsStringAsync()).Should().Contain(":root[data-theme=\"hvo-dark\"]").And.Contain("--hvo-body-bg");
    }

    private static Task<WebHost> StartAsync(Func<HttpRequestMessage, HttpResponseMessage> stop)
        => WebHost.StartAsync(controller: new FakeController { OtherAnswer = request => IsStop(request) ? stop(request) : null });

    private static bool IsStop(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/" + RoofApiRoutesTest.Stop;

    private static HttpResponseMessage Stopped(RoofRelayRegisterState relay = RoofRelayRegisterState.Verified)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped, relayState: relay), options: RoofClientJson.Options),
        };

    private static async Task<(HttpStatusCode Status, WebStopResponse Answer)> StopAsync(WebBrowserClient browser, string? token)
    {
        var fields = token is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        using var response = await browser.PostFormAsync(WebAuthentication.StopPostPath, fields);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<WebStopResponse>())!);
    }

    private static JsonElement Texts() => JsonDocument.Parse(WebStopTexts.Json).RootElement;

    [GeneratedRegex("<form[^>]*data-web-stop[\\s\\S]*?</form>")]
    private static partial Regex StopFormPattern();

    [GeneratedRegex("data-web-stop-texts=\"([^\"]*)\"")]
    private static partial Regex TextsPattern();

    [GeneratedRegex("(?:<link rel=\"stylesheet\" href|<script src)=\"([^\"]+)\"")]
    private static partial Regex AssetPattern();
}
