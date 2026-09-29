using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// Signing in to the web UI: with a name and password at the controller, the session kept in a strict, HttpOnly cookie
/// that holds the token encrypted; every page needs it; problems go back to the sign-in page as messages.
/// </summary>
[TestClass]
public sealed class WebSignInTests
{
    [TestMethod]
    public async Task SignIn_WithTheRightPassword_SetsAStrictHttpOnlyCookie_AndGoesBack()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.SignInAsync("ada", FakeController.AdaPassword, returnUrl: "/account/password");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/account/password");
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith(WebAuthentication.CookieName + "=", StringComparison.Ordinal));
        setCookie.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("samesite=strict")
            .And.NotContainEquivalentOf("expires=", "the cookie goes when the browser closes (and the session's expiry is inside it)");
        setCookie.Should().NotContain("token-1-ada", "the token is encrypted in the cookie");
        host.Sessions.TryGet("session-1", out var session).Should().BeTrue();
        session.Name.Should().Be("ada");
        session.Role.Should().Be(RoofControllerApiContract.AdminRole);

        var page = await (await browser.GetAsync("/account/password")).Content.ReadAsStringAsync();
        page.Should().Contain("Change your password").And.Contain("ada").And.Contain(">Admin<");
    }

    [TestMethod]
    public async Task SignIn_SendsTheNameAndPassword_WithoutACredential()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        await browser.SignInAsync("ada", FakeController.AdaPassword);

        var request = host.Controller.Logged(HttpMethod.Post, RoofApiRoutesTest.Session).Single();
        request.Authorization.Should().BeNull();
        request.HasApiKey.Should().BeFalse();
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("name").GetString().Should().Be("ada");
    }

    [TestMethod]
    [DataRow("ada", "wrong password!", SignInMessages.Failed)]
    [DataRow("nobody", FakeController.AdaPassword, SignInMessages.Failed)]
    [DataRow("ada", "", SignInMessages.Missing)]
    [DataRow("  ", FakeController.AdaPassword, SignInMessages.Missing)]
    public async Task SignIn_Refused_GoesBackToTheSignInPage_WithTheMessage(string name, string password, string message)
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.SignInAsync(name, password, returnUrl: "/account/password");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be($"/signin?message={message}&returnUrl=%2Faccount%2Fpassword");
        browser.Cookie(WebAuthentication.CookieName).Should().BeNull();
        var page = await (await browser.GetAsync(response.Headers.Location.OriginalString)).Content.ReadAsStringAsync();
        page.Should().Contain(SignInMessages.Describe(message)!.Text).And.Contain("value=\"/account/password\"");
    }

    [TestMethod]
    public async Task SignIn_ControllerRefusals_AreNamed()
    {
        var controller = new FakeController();
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();

        async Task<string> MessageAsync()
        {
            using var response = await browser.SignInAsync("ada", FakeController.AdaPassword);
            return response.Headers.Location!.OriginalString;
        }

        controller.SignInAnswer = () => FakeController.Problem(HttpStatusCode.TooManyRequests, RoofControllerErrorCode.SignInLockedOut, TimeSpan.FromSeconds(30));
        (await MessageAsync()).Should().Be("/signin?message=locked");
        controller.SignInAnswer = () => FakeController.Problem(HttpStatusCode.TooManyRequests, RoofControllerErrorCode.SignInBusy);
        (await MessageAsync()).Should().Be("/signin?message=busy");
        controller.SignInAnswer = () => FakeController.Problem(HttpStatusCode.TooManyRequests, null);
        (await MessageAsync()).Should().Be("/signin?message=busy");
        controller.SignInAnswer = () => FakeController.Problem(HttpStatusCode.ServiceUnavailable, null);
        (await MessageAsync()).Should().Be("/signin?message=unreachable");
        controller.SignInAnswer = () => FakeController.Problem(HttpStatusCode.Forbidden, null);
        (await MessageAsync()).Should().Be("/signin?message=refused");
        controller.SignInAnswer = () => throw new HttpRequestException("Connection refused");
        (await MessageAsync()).Should().Be("/signin?message=unreachable");
        host.Sessions.Count.Should().Be(0);
    }

    [TestMethod]
    public async Task SignIn_WithARoleTheWebUiDoesNotKnow_IsRefused_AndTheSessionEnded()
    {
        await using var host = await WebHost.StartAsync();
        host.Controller.RoleOverride = "RoofKiosk";
        using var browser = host.Browser();

        using var response = await browser.SignInAsync("ada", FakeController.AdaPassword);

        response.Headers.Location!.OriginalString.Should().Be("/signin?message=refused");
        host.Controller.Logged(HttpMethod.Delete, RoofApiRoutesTest.Session).Single().Authorization.Should().Be("Bearer token-1-ada");
        browser.Cookie(WebAuthentication.CookieName).Should().BeNull();
    }

    [TestMethod]
    public async Task SignIn_WithoutTheFormsToken_SaysTheFormExpired_WithoutAskingTheController()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.PostFormAsync("/account/signin", new Dictionary<string, string>
        {
            ["name"] = "ada",
            ["password"] = FakeController.AdaPassword,
            ["returnUrl"] = "/account/password",
        });

        response.Headers.Location!.OriginalString.Should().Be("/signin?message=expired&returnUrl=%2Faccount%2Fpassword");
        host.Controller.SignIns.Should().Be(0);
    }

    [TestMethod]
    public async Task SignIn_PastTheLimitForAnAddress_IsRefused_WithoutAskingTheController()
    {
        await using var host = await WebHost.StartAsync(["--RoofWeb:SignInAttemptsPerMinute=2"]);
        using var browser = host.Browser();

        (await browser.SignInAsync("ada", "wrong password!")).Headers.Location!.OriginalString.Should().Be("/signin?message=failed");
        (await browser.SignInAsync("ada", "wrong password!")).Headers.Location!.OriginalString.Should().Be("/signin?message=failed");
        (await browser.SignInAsync("ada", FakeController.AdaPassword)).Headers.Location!.OriginalString.Should().Be("/signin?message=too-many");

        host.Controller.SignIns.Should().Be(2);
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/account/password")]
    [DataRow("/denied")]
    public async Task Pages_WithoutSigningIn_GoToTheSignInPage(string page)
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.GetAsync(page);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.Should().Be("/signin?returnUrl=" + Uri.EscapeDataString(page));
    }

    [TestMethod]
    public async Task SignInPage_IsStatic_WithOneTitle_AndTheForm()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.GetAsync("/signin?returnUrl=%2F%2Fevil.example");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        System.Text.RegularExpressions.Regex.Matches(html, "<h1[ >]").Should().ContainSingle();
        html.Should().Contain("<title>Sign in · HVO Roof Controller</title>")
            .And.Contain("name=\"name\"").And.Contain("name=\"password\"").And.Contain("__RequestVerificationToken")
            .And.Contain("name=\"returnUrl\" value=\"/\"", "an address on another site is never offered back")
            .And.NotContain("<!--Blazor:{", "the sign-in page needs no live connection");
    }

    [TestMethod]
    public async Task SignOut_EndsTheSessionAtTheController_AndClearsTheCookie()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();
        await browser.SignInAsync("ada", FakeController.AdaPassword);

        using var response = await browser.PostFormAsync("/account/signout", new Dictionary<string, string>());

        response.Headers.Location!.OriginalString.Should().Be("/signin?message=signed-out");
        host.Controller.Logged(HttpMethod.Delete, RoofApiRoutesTest.Session).Single().Authorization.Should().Be("Bearer token-1-ada");
        browser.Cookie(WebAuthentication.CookieName).Should().BeNull();
        host.Sessions.TryGet("session-1", out _).Should().BeFalse();
        (await browser.GetAsync("/")).Headers.Location!.PathAndQuery.Should().StartWith("/signin");
    }

    [TestMethod]
    public async Task ACookie_ForASessionThatEnded_IsRemoved_AndTheSignInPageSaysWhy()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();
        await browser.SignInAsync("ada", FakeController.AdaPassword);
        var cookie = browser.Cookie(WebAuthentication.CookieName);
        host.Sessions.TryGet("session-1", out var session).Should().BeTrue();

        session.End();
        using var response = await browser.GetAsync("/account/password");

        response.Headers.Location!.PathAndQuery.Should().Be("/signin?returnUrl=%2Faccount%2Fpassword&message=ended");
        browser.Cookie(WebAuthentication.CookieName).Should().BeNull();

        // The same cookie, kept by someone, does not bring the session back.
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/");
        replay.Headers.Add("Cookie", $"{WebAuthentication.CookieName}={cookie}");
        (await browser.SendAsync(replay)).Headers.Location!.PathAndQuery.Should().StartWith("/signin");
    }

    [TestMethod]
    public async Task ASession_TheControllerRefuses_EndsTheWebSession()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();
        await browser.SignInAsync("ada", FakeController.AdaPassword);
        host.Sessions.TryGet("session-1", out var session).Should().BeTrue();
        host.Controller.EndSession("token-1-ada");

        var call = () => session.Client.Auth.GetCallerAsync();

        await call.Should().ThrowAsync<HVO.RoofControllerV4.Client.RoofApiException>();
        session.IsEnded.Should().BeTrue();
        (await browser.GetAsync("/")).Headers.Location!.PathAndQuery.Should().Be("/signin?returnUrl=%2F&message=ended");
    }

    [TestMethod]
    public async Task AfterARestart_TheCookie_PicksTheSessionUpAgain()
    {
        using var keys = new WebTestSupport.TempDirectory();
        string[] args = [$"--RoofWeb:DataProtectionPath={keys.Path}"];
        var controller = new FakeController();
        string cookie;
        await using (var first = await WebHost.StartAsync(args, controller))
        {
            using var browser = first.Browser();
            await browser.SignInAsync("ada", FakeController.AdaPassword);
            cookie = browser.Cookie(WebAuthentication.CookieName)!;
        }

        var logs = new RecordingLoggerProvider();
        await using var second = await WebHost.StartAsync(args, controller, builder => builder.Logging.AddProvider(logs));
        using var restarted = second.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/password");
        request.Headers.Add("Cookie", $"{WebAuthentication.CookieName}={cookie}");

        using var response = await restarted.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Sessions.TryGet("session-1", out var session).Should().BeTrue();
        session.Credential.Session.Token.Should().Be("token-1-ada");
        session.ExpiresUtc.Should().BeCloseTo(controller.Time.GetUtcNow().AddHours(12), TimeSpan.FromMinutes(1));
        logs.Entries.Select(entry => entry.Message).Should().Contain("Web session for ada (RoofAdmin) picked up again from its cookie");
    }

    [TestMethod]
    public async Task TheLiveConnection_NeedsASignedInPerson()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var anonymous = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1"));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await browser.SignInAsync("vic", FakeController.AdaPassword);
        using var signedIn = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1"));
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task SignIn_FromAnotherSite_IsRefused_BeforeTheController()
    {
        await using var host = await WebHost.StartAsync(["--RoofWeb:AllowedOrigins:0=https://roof.example.org"]);

        async Task<HttpResponseMessage> SignInFromAsync(string origin)
        {
            using var browser = host.Browser();
            var token = await browser.GetFormTokenAsync("/signin");
            return await browser.PostFormAsync(
                "/account/signin",
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["name"] = "ada",
                    ["password"] = FakeController.AdaPassword,
                },
                origin);
        }

        using var foreign = await SignInFromAsync("https://evil.example");
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await foreign.Content.ReadAsStringAsync()).Should().Contain("origin_not_allowed");
        host.Controller.SignIns.Should().Be(0);

        using var same = await SignInFromAsync("http://localhost");
        same.Headers.Location!.OriginalString.Should().Be("/");
        using var allowed = await SignInFromAsync("https://roof.example.org");
        allowed.Headers.Location!.OriginalString.Should().Be("/");
        host.Controller.SignIns.Should().Be(2);
    }

    [TestMethod]
    [DataRow(null, "/")]
    [DataRow("", "/")]
    [DataRow("/account/password", "/account/password")]
    [DataRow("/settings?tab=roof#x", "/settings?tab=roof#x")]
    [DataRow("https://evil.example/", "/")]
    [DataRow("//evil.example/", "/")]
    [DataRow("/\\evil.example/", "/")]
    [DataRow("/a\\b", "/")]
    [DataRow("/a\nb", "/")]
    [DataRow("/account/signin", "/")]
    [DataRow("/account/signout", "/")]
    [DataRow("/ACCOUNT", "/")]
    [DataRow("/signin?returnUrl=/x", "/")]
    [DataRow("relative", "/")]
    [DataRow("/camera/2/mjpeg", "/")]
    [DataRow("/Camera", "/")]
    [DataRow("/cameras", "/cameras")]
    public void GetSafeReturnUrl_KeepsOnlyPagesOfThisSite(string? returnUrl, string expected)
    {
        WebAccountEndpoints.GetSafeReturnUrl(returnUrl).Should().Be(expected);
    }

    [TestMethod]
    public void EveryMessage_HasWords()
    {
        var values = typeof(SignInMessages).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        values.Should().HaveCountGreaterThan(8);
        values.Should().AllSatisfy(value => SignInMessages.Describe(value).Should().NotBeNull());
        SignInMessages.Describe("<script>").Should().BeNull();
    }
}
