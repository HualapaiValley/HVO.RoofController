using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// Web console sign-in: POST /account/login exchanges an access key for an HttpOnly, SameSite=Strict cookie. The cookie
/// works for console-only surfaces (health details, camera, Blazor hub, the reconnect dialog's POST /console/stop) but
/// never for the api/* command routes.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConsoleLoginTests
{
    private static TimeSpan _originalDelay;
    private RoofApiTestHost _host = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _originalDelay = RoofAccountEndpoints.FailedLoginDelay;
        RoofAccountEndpoints.FailedLoginDelay = TimeSpan.Zero;
    }

    [ClassCleanup]
    public static void ClassCleanup() => RoofAccountEndpoints.FailedLoginDelay = _originalDelay;

    [TestInitialize]
    public void TestInitialize() => _host = new RoofApiTestHost();

    [TestCleanup]
    public void TestCleanup() => _host.Dispose();

    [TestMethod]
    public async Task Login_ValidKey_SetsStrictHttpOnlyCookieAndRedirectsToReturnUrl()
    {
        using var client = _host.CreateApiClient();

        var response = await client.SendAsync(LoginRequest(TestApiKeys.Viewer, "/roof"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/roof", response.Headers.Location?.OriginalString);
        var setCookie = ConsoleSetCookie(response);
        setCookie.Should().ContainEquivalentOf("httponly");
        setCookie.Should().ContainEquivalentOf("samesite=strict");
        setCookie.Should().NotContain(TestApiKeys.Viewer);
    }

    [TestMethod]
    public async Task Login_ExternalReturnUrl_RedirectsToRoot()
    {
        using var client = _host.CreateApiClient();

        var response = await client.SendAsync(LoginRequest(TestApiKeys.Viewer, "https://evil.example/"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task Login_UnknownKey_RedirectsBackWithError1()
    {
        using var client = _host.CreateApiClient();

        var response = await client.SendAsync(LoginRequest("test-wrong-console-key-000000000000", "/roof"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login?error=1&returnUrl=%2Froof", response.Headers.Location?.OriginalString);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [TestMethod]
    public async Task Login_WithoutAntiforgeryToken_RedirectsBackWithError2()
    {
        using var client = _host.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, RoofControllerSecurityDefaults.LoginPostPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [RoofControllerSecurityDefaults.AccessKeyFormField] = TestApiKeys.Viewer,
                [RoofControllerSecurityDefaults.ReturnUrlFormField] = "/roof"
            })
        };

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login?error=2&returnUrl=%2Froof", response.Headers.Location?.OriginalString);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [TestMethod]
    public async Task Login_NotAForm_Returns400()
    {
        using var client = _host.CreateApiClient();

        var response = await client.PostAsync(
            RoofControllerSecurityDefaults.LoginPostPath,
            new StringContent("{\"accessKey\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Login_CrossOrigin_Returns403()
    {
        using var client = _host.CreateApiClient();
        using var request = LoginRequest(TestApiKeys.Viewer, "/");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("origin_not_allowed", problem.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Login_SameOrigin_IsAccepted()
    {
        using var client = _host.CreateApiClient();
        using var request = LoginRequest(TestApiKeys.Viewer, "/");
        request.Headers.Add("Origin", "http://localhost");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [TestMethod]
    public async Task ConsoleCookie_WorksForConsoleSurfacesOnly()
    {
        var cookie = await SignInAsync(TestApiKeys.Admin);
        using var client = _host.CreateApiClient();

        var health = await SendWithCookieAsync(client, HttpMethod.Get, "/health", cookie);
        var camera = await SendWithCookieAsync(client, HttpMethod.Get, "/api/v1.0/Camera/1/mjpeg", cookie);
        var status = await SendWithCookieAsync(client, HttpMethod.Get, "/api/v4.0/RoofControl/Status", cookie);
        var stop = await SendWithCookieAsync(client, HttpMethod.Post, "/api/v4.0/RoofControl/Stop", cookie);
        var system = await SendWithCookieAsync(client, HttpMethod.Get, "/api/v1.0/System/info", cookie);

        health.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, camera.StatusCode, "authorized; the proxy is simply not configured in this test");
        Assert.AreEqual(HttpStatusCode.Unauthorized, status.StatusCode, "api/* routes accept only X-Api-Key");
        Assert.AreEqual(HttpStatusCode.Unauthorized, stop.StatusCode, "a cookie can never drive the roof cross-site");
        Assert.AreEqual(HttpStatusCode.Unauthorized, system.StatusCode);
        _host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<Common.Models.RoofControllerStopReason>()), Moq.Times.Never);
    }

    [TestMethod]
    public async Task Logout_ClearsCookieAndRedirectsToLogin()
    {
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var client = _host.CreateApiClient();

        var response = await SendWithCookieAsync(client, HttpMethod.Post, RoofControllerSecurityDefaults.LogoutPostPath, cookie);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(RoofControllerSecurityDefaults.LoginPath, response.Headers.Location?.OriginalString);
        ConsoleSetCookie(response).Should().ContainEquivalentOf("expires=Thu, 01 Jan 1970");
    }

    [TestMethod]
    public async Task BlazorHub_RequiresSignedInViewer()
    {
        using var client = _host.CreateApiClient();
        var cookie = await SignInAsync(TestApiKeys.Viewer);

        var anonymous = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
        var signedIn = await SendWithCookieAsync(client, HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", cookie);

        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [TestMethod]
    public async Task BlazorHub_CrossOrigin_Returns403()
    {
        using var client = _host.CreateApiClient();
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", "http://evil.example");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Console_EndToEnd_AnonymousRedirectsToLoginFormAndSignedInUserSeesConsole()
    {
        using var client = _host.CreateApiClient();

        var anonymous = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);
        var location = anonymous.Headers.Location!;
        var loginUri = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
        Assert.AreEqual(RoofControllerSecurityDefaults.LoginPath, loginUri.AbsolutePath);
        StringAssert.Contains(loginUri.Query, RoofControllerSecurityDefaults.ReturnUrlFormField + "=%2F");

        var loginPage = await client.GetAsync(loginUri.PathAndQuery);
        Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode);
        var html = await loginPage.Content.ReadAsStringAsync();
        StringAssert.Contains(html, $"action=\"{RoofControllerSecurityDefaults.LoginPostPath}\"");
        StringAssert.Contains(html, $"name=\"{RoofControllerSecurityDefaults.AccessKeyFormField}\"");
        StringAssert.Contains(html, $"name=\"{RoofControllerSecurityDefaults.ReturnUrlFormField}\"");
        StringAssert.Contains(html, "__RequestVerificationToken");

        var cookie = await SignInAsync(TestApiKeys.Viewer);
        var console = await SendWithCookieAsync(client, HttpMethod.Get, "/", cookie);
        Assert.AreEqual(HttpStatusCode.OK, console.StatusCode);
    }

    [TestMethod]
    public async Task LoginPage_ExpiredFormError_ShowsDistinctMessage()
    {
        using var client = _host.CreateApiClient();

        var html = await client.GetStringAsync($"{RoofControllerSecurityDefaults.LoginPath}?error={RoofAccountEndpoints.ExpiredFormError}");

        StringAssert.Contains(html, "The sign-in form expired");
    }

    [TestMethod]
    public async Task ConsoleStop_SignedInViewerWithTheDialogForm_StopsTheRoof()
    {
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var client = _host.CreateApiClient();
        using var request = await ConsoleStopRequestAsync(client, cookie);
        request.Headers.Add("Origin", "http://localhost");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiJson.ReadAsync<ConsoleStopResponse>(response);
        Assert.AreEqual(nameof(RoofStopOutcome.Acknowledged), body.Outcome);
        body.Message.Should().Contain("Relay register verified");
        _host.RoofService.Verify(s => s.Stop(RoofControllerStopReason.NormalStop), Moq.Times.Once);
    }

    [TestMethod]
    public async Task ConsoleStop_RelayUnverified_Returns503WithTheWarning()
    {
        _host.RoofService.Setup(s => s.Stop(Moq.It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(
                RoofControllerErrorCode.RelayStateUnverified,
                "Relay read-back did not confirm all relays off.",
                RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified, relayMask: null))));
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var client = _host.CreateApiClient();
        using var request = await ConsoleStopRequestAsync(client, cookie);

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await ApiJson.ReadAsync<ConsoleStopResponse>(response);
        Assert.AreEqual(nameof(RoofStopOutcome.RelayUnverified), body.Outcome);
        body.Message.Should().Contain("Confirm at the roof");
    }

    [TestMethod]
    public async Task ConsoleStop_WithoutTheFormToken_Returns400AndDoesNotStop()
    {
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var client = _host.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, RoofControllerSecurityDefaults.ConsoleStopPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>())
        };
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ApiJson.ReadAsync<ConsoleStopResponse>(response);
        Assert.AreEqual(nameof(RoofStopOutcome.Failed), body.Outcome);
        body.Message.Should().Contain("out of date");
        _host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<RoofControllerStopReason>()), Moq.Times.Never);
    }

    [TestMethod]
    public async Task ConsoleStop_AnonymousOrApiKey_Returns401AndDoesNotStop()
    {
        using var anonymous = _host.CreateApiClient();
        using var withKey = _host.CreateApiClient(TestApiKeys.Admin);

        var anonymousResponse = await anonymous.PostAsync(RoofControllerSecurityDefaults.ConsoleStopPath, content: null);
        var keyResponse = await withKey.PostAsync(RoofControllerSecurityDefaults.ConsoleStopPath, content: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, keyResponse.StatusCode, "the endpoint accepts only the console cookie");
        _host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<RoofControllerStopReason>()), Moq.Times.Never);
    }

    [TestMethod]
    public async Task ConsoleStop_CrossOrigin_Returns403AndDoesNotStop()
    {
        var cookie = await SignInAsync(TestApiKeys.Viewer);
        using var client = _host.CreateApiClient();
        using var request = await ConsoleStopRequestAsync(client, cookie);
        request.Headers.Add("Origin", "http://evil.example");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        _host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<RoofControllerStopReason>()), Moq.Times.Never);
    }

    /// <summary>
    /// Loads the console as the signed-in user and builds the POST the reconnect dialog's form sends: the rendered
    /// antiforgery field plus the antiforgery cookie issued with the page.
    /// </summary>
    private async Task<HttpRequestMessage> ConsoleStopRequestAsync(HttpClient client, string consoleCookie)
    {
        var page = await SendWithCookieAsync(client, HttpMethod.Get, "/", consoleCookie);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        var form = Regex.Match(html, "<form[^>]*data-console-stop[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline);
        form.Success.Should().BeTrue("the reconnect dialog renders the stop form");
        form.Value.Should().Contain($"action=\"{RoofControllerSecurityDefaults.ConsoleStopPath}\"");
        var fieldName = _host.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.FormFieldName;
        var field = Regex.Match(form.Groups["body"].Value, $"<input[^>]*name=\"{Regex.Escape(fieldName)}\"[^>]*>");
        field.Success.Should().BeTrue("the stop form carries an antiforgery field");
        var token = WebUtility.HtmlDecode(Regex.Match(field.Value, "value=\"(?<value>[^\"]+)\"").Groups["value"].Value);
        token.Should().NotBeNullOrEmpty();

        var antiforgeryCookieName = _host.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;
        page.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue("the page issues the antiforgery cookie");
        var antiforgeryCookie = setCookies!.Single(v => v.StartsWith(antiforgeryCookieName + "=", StringComparison.Ordinal)).Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Post, RoofControllerSecurityDefaults.ConsoleStopPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { [fieldName] = token })
        };
        request.Headers.Add("Cookie", $"{consoleCookie}; {antiforgeryCookie}");
        return request;
    }

    private HttpRequestMessage LoginRequest(string accessKey, string returnUrl)
    {
        var antiforgery = _host.Services.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(new DefaultHttpContext { RequestServices = _host.Services });
        var cookieName = _host.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;

        var request = new HttpRequestMessage(HttpMethod.Post, RoofControllerSecurityDefaults.LoginPostPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [RoofControllerSecurityDefaults.AccessKeyFormField] = accessKey,
                [RoofControllerSecurityDefaults.ReturnUrlFormField] = returnUrl,
                [tokens.FormFieldName] = tokens.RequestToken!
            })
        };
        request.Headers.Add("Cookie", $"{cookieName}={tokens.CookieToken}");
        return request;
    }

    private async Task<string> SignInAsync(string accessKey)
    {
        using var client = _host.CreateApiClient();
        using var request = LoginRequest(accessKey, "/");
        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        return ConsoleSetCookie(response).Split(';')[0];
    }

    private static async Task<HttpResponseMessage> SendWithCookieAsync(HttpClient client, HttpMethod method, string path, string cookie)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static string ConsoleSetCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue("the response should set the console cookie");
        return values!.Single(v => v.StartsWith(RoofSecurityServiceCollectionExtensions.ConsoleCookieName + "=", StringComparison.Ordinal));
    }
}
