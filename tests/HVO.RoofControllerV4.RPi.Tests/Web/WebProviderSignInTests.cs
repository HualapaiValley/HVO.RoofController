using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>Actual web form routing against an explicit controller double; native PAM is tested in the disposable guest.</summary>
[TestClass]
public sealed class WebProviderSignInTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProviderSelection_IsShownOnlyWhenControllerEnablesIt(bool enabled)
    {
        var controller = Controller(enabled);
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();
        var html = await (await browser.GetAsync("/signin")).Content.ReadAsStringAsync();
        if (enabled)
        {
            html.Should().Contain("signin-provider").And.Contain("Linux account").And.Contain("Roof Controller account");
        }
        else
        {
            html.Should().NotContain("signin-provider");
        }
    }

    [TestMethod]
    [DataRow("linux")]
    [DataRow("virtual")]
    public async Task ExplicitProvider_IsForwardedWithoutACredentialOrFallback(string provider)
    {
        var controller = Controller(true);
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();
        var token = await browser.GetFormTokenAsync("/signin");
        using var response = await browser.PostFormAsync("/account/signin", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["provider"] = provider,
            ["name"] = "ada", ["password"] = "test-inert-password"
        });
        response.Headers.Location!.OriginalString.Should().Be("/");
        var request = controller.Logged(HttpMethod.Post, "api/v4.0/Auth/ProviderSession").Single();
        request.Authorization.Should().BeNull();
        request.HasApiKey.Should().BeFalse();
        JsonSerializer.Deserialize<RoofProviderSignInRequest>(request.Body!, JsonSerializerOptions.Web)!.Provider.Should().Be(provider);
        controller.SignIns.Should().Be(0, "the legacy provider is never retried");
        browser.Cookie(WebAuthentication.CookieName).Should().NotBeNull();
    }

    [TestMethod]
    public async Task UnknownProvider_IsRefusedBeforeSendingPassword()
    {
        var controller = Controller(true);
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();
        var token = await browser.GetFormTokenAsync("/signin");
        using var response = await browser.PostFormAsync("/account/signin", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["provider"] = "local",
            ["name"] = "ada", ["password"] = "test-inert-password"
        });
        response.Headers.Location!.OriginalString.Should().Be("/signin?message=refused");
        controller.Logged(HttpMethod.Post, "api/v4.0/Auth/ProviderSession").Should().BeEmpty();
        controller.SignIns.Should().Be(0);
    }

    [TestMethod]
    public async Task RefusedLinuxPassword_DoesNotTryVirtualOrLegacy()
    {
        var controller = Controller(true);
        controller.OtherAnswer = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v4.0/Auth/Providers" => Json(new RoofAuthProvidersResponse(true, ["virtual", "linux"])),
            "/api/v4.0/Auth/ProviderSession" => FakeController.Problem(HttpStatusCode.Unauthorized, RoofControllerErrorCode.SignInFailed),
            _ => null
        };
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();
        var token = await browser.GetFormTokenAsync("/signin");
        using var response = await browser.PostFormAsync("/account/signin", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["provider"] = "linux",
            ["name"] = "ada", ["password"] = "test-wrong-password"
        });
        response.Headers.Location!.OriginalString.Should().Be("/signin?message=failed");
        controller.Logged(HttpMethod.Post, "api/v4.0/Auth/ProviderSession").Should().ContainSingle();
        controller.SignIns.Should().Be(0);
        browser.Cookie(WebAuthentication.CookieName).Should().BeNull();
    }

    private static FakeController Controller(bool enabled) => new()
    {
        OtherAnswer = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v4.0/Auth/Providers" => Json(new RoofAuthProvidersResponse(enabled, enabled ? ["virtual", "linux"] : ["virtual"])),
            "/api/v4.0/Auth/ProviderSession" => Json(new RoofSessionResponse("test-provider-session", "provider-fixture",
                "ada", "RoofAdmin", RoofCredentialKind.Session, DateTimeOffset.UtcNow.AddHours(1), null)),
            _ => null
        }
    };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: RoofClientJson.Options)
    };
}
