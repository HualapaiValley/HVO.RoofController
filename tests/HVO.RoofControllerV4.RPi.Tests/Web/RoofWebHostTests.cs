using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebProgram = HVO.RoofControllerV4.Web.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The web UI's host: its settings, liveness, the status page and HTTPS.</summary>
[TestClass]
public sealed class RoofWebHostTests
{
    [TestMethod]
    public async Task HealthLive_AnswersHealthy_WithTheSecurityHeaders()
    {
        await using var app = await StartAsync([]);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(WebProgram.HealthLivePath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
    }

    [TestMethod]
    public async Task HealthLive_Answers_WhileTheControllerDoesNot()
    {
        await using var app = await StartAsync([], WebTestSupport.ControllerAnswering(_ => throw new HttpRequestException("Connection refused")));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(WebProgram.HealthLivePath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Home_ShowsTheControllerStatus_WithExactlyOneTitle()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();
        (await browser.SignInAsync("ada", FakeController.AdaPassword)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Regex.Matches(html, "<title>").Should().ContainSingle();
        html.Should().Contain("<title>Roof controller</title>");
        Regex.Matches(html, "<h1[ >]").Should().ContainSingle();
        html.Should().Contain("The controller is ready")
            .And.Contain("The web UI is not running under the container&#x27;s supervisor.");
    }

    [TestMethod]
    public async Task Home_UnderTheSupervisor_ShowsACrashLoop()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("supervisor.json");
        await File.WriteAllTextAsync(path, WebTestSupport.SupervisorState(SupervisedProcess.States.CrashLoop, 5));
        await using var host = await WebHost.StartAsync(
            [$"--RoofWeb:SupervisorStatePath={path}"],
            customize: builder => builder.Services.AddSingleton(WebTestSupport.ControllerAnswering(_ => throw new HttpRequestException("Connection refused"))));
        using var browser = host.Browser();
        await browser.SignInAsync("ada", FakeController.AdaPassword);

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        html.Should().Contain("The controller is stopped after repeated crashes")
            .And.Contain("It crashed 5 times within 120 s")
            .And.Contain("Supervisor running: controller crash-loop");
    }

    [TestMethod]
    public async Task UnknownAddresses_AreNotFound_WithOneTitle()
    {
        await using var app = await StartAsync([]);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task TheControllerClient_UsesControllerUrl()
    {
        await using var app = await StartWithItsOwnClientAsync(["--RoofWeb:ControllerUrl=http://127.0.0.1:18080"]);

        app.Services.GetRequiredService<RoofControllerClient>().BaseAddress.Should().Be(new Uri("http://127.0.0.1:18080/"));
        app.Services.GetRequiredService<RoofControllerClient>().Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public void InvalidSettings_StopTheStart_NamingThem()
    {
        var build = () => WebProgram.BuildApp(["--RoofWeb:StatusRefreshSeconds=0", "--RoofWeb:ControllerUrl=ftp://localhost"]);

        build.Should().Throw<RoofWebSettingsException>()
            .Which.Message.Should().Contain("RoofWeb:StatusRefreshSeconds").And.Contain("RoofWeb:ControllerUrl");
    }

    [TestMethod]
    public void Https_WithoutACertificate_OutsideDevelopment_IsRefused()
    {
        var build = () => WebProgram.BuildApp(["--RoofWeb:Urls=https://+:8088", "--environment=Production"]);

        build.Should().Throw<RoofWebSettingsException>().Which.Message.Should().Contain("RoofWeb:Certificate:Path");
    }

    [TestMethod]
    [DoNotParallelize]
    public void Main_WithInvalidSettings_ExitsOne_WithAMessage()
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            WebProgram.Main(["--RoofWeb:StatusRefreshSeconds=0"]).Should().Be(1);
        }
        finally
        {
            Console.SetError(original);
        }

        error.ToString().Should().StartWith("The roof controller's web UI did not start: RoofWeb:StatusRefreshSeconds must be between 1 and 60");
    }

    [TestMethod]
    public async Task Https_ServesTheConfiguredCertificate()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var certificate = CreateCertificate();
        var (pfx, passwordFile) = WriteCertificate(directory, certificate, "correct horse");

        await using var app = WebProgram.BuildApp(
        [
            "--RoofWeb:Urls=https://127.0.0.1:0",
            $"--RoofWeb:Certificate:Path={pfx}",
            $"--RoofWeb:Certificate:PasswordFile={passwordFile}",
        ],
        builder => builder.Services.AddSingleton(WebTestSupport.ControllerReady()));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        X509Certificate2? served = null;
        using var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, remote, _, _) =>
                {
                    served = remote is null ? null : new X509Certificate2(remote);
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(new Uri(new Uri(address), WebProgram.HealthLivePath));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Should().NotBeNull();
        served!.Thumbprint.Should().Be(certificate.Thumbprint);
        served.Dispose();
    }

    [TestMethod]
    public void LoadCertificate_WithTheWrongPassword_IsRefused_WithoutThePassword()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var certificate = CreateCertificate();
        var (pfx, _) = WriteCertificate(directory, certificate, "correct horse");
        var wrong = directory.File("wrong-password");
        File.WriteAllText(wrong, "battery staple\n");

        var load = () => WebProgram.LoadCertificate(new RoofWebCertificateOptions { Path = pfx, PasswordFile = wrong });

        load.Should().Throw<RoofWebSettingsException>()
            .Which.Message.Should().Contain(pfx).And.Contain("CryptographicException").And.NotContain("battery").And.NotContain("correct horse");
    }

    [TestMethod]
    public void LoadCertificate_IgnoresTheTrailingLineBreak_OfThePasswordFile()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var certificate = CreateCertificate();
        var (pfx, passwordFile) = WriteCertificate(directory, certificate, "correct horse");
        File.WriteAllText(passwordFile, "correct horse\r\n");

        using var loaded = WebProgram.LoadCertificate(new RoofWebCertificateOptions { Path = pfx, PasswordFile = passwordFile });

        loaded.Thumbprint.Should().Be(certificate.Thumbprint);
        loaded.HasPrivateKey.Should().BeTrue();
    }

    /// <summary>
    /// Starts the web UI on a test server, with <paramref name="controller"/> (by default one that answers ready) in
    /// place of the controller client, so no test reaches a controller that happens to listen on this machine.
    /// </summary>
    private static Task<WebApplication> StartAsync(string[] args, RoofControllerClient? controller = null)
        => StartHostAsync(args, controller ?? WebTestSupport.ControllerReady());

    /// <summary>Starts the web UI with its own controller client, built from the settings (it is not called).</summary>
    private static Task<WebApplication> StartWithItsOwnClientAsync(string[] args) => StartHostAsync(args, controller: null);

    private static async Task<WebApplication> StartHostAsync(string[] args, RoofControllerClient? controller)
    {
        var app = WebProgram.BuildApp(args, builder =>
        {
            builder.WebHost.UseTestServer();
            if (controller is not null)
            {
                builder.Services.AddSingleton(controller);
            }
        });
        await app.StartAsync();
        return app;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=roof-web.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static (string Pfx, string PasswordFile) WriteCertificate(WebTestSupport.TempDirectory directory, X509Certificate2 certificate, string password)
    {
        var pfx = directory.File("certificate.pfx");
        var passwordFile = directory.File("certificate-password");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(passwordFile, password + "\n");
        return (pfx, passwordFile);
    }
}
