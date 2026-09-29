using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// A controller with a self-signed certificate, reached over real HTTPS on a loopback port (#44): the pin is what lets
/// a request, Stop and the status hub's WebSocket through, and without it (or with another certificate's) all three
/// are refused at the TLS handshake. Every other client test goes through the in-memory server, which has no TLS.
/// </summary>
[TestClass]
public sealed class RoofCertificatePinTests
{
    private static X509Certificate2 _controller = null!;
    private static X509Certificate2 _other = null!;
    private static PinnedController _host = null!;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _controller = CreateCertificate("CN=roof.local");
        _other = CreateCertificate("CN=other.local");
        _host = await PinnedController.StartAsync(_controller);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        _controller.Dispose();
        _other.Dispose();
    }

    [TestMethod]
    public async Task WithThePin_ARequest_Stop_AndTheStatusHub_AllConnect()
    {
        using var client = CreatePinnedClient(RoofCertificatePin.GetSha256(_controller));

        var caller = await client.Auth.GetCallerAsync();
        caller.Name.Should().Be("pin-test");

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        stop.Message.Should().Be(RoofStopText.AcknowledgedVerified);

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot over the pinned WebSocket");
        feed.Current!.InstanceId.Should().Be(PinnedController.InstanceId);
        feed.LastError.Should().BeNull();
        _host.HubKeys.Should().Contain(TestApiKeys.Viewer, "the key reaches the hub over the platform's WebSocket, not only over HTTP");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "another certificate's pin")]
    [DataRow(false, DisplayName = "no pin")]
    public async Task WithoutTheRightPin_ARequest_Stop_AndTheStatusHub_AreAllRefusedAtTheHandshake(bool otherPin)
    {
        using var client = CreatePinnedClient(otherPin ? RoofCertificatePin.GetSha256(_other) : null);

        var request = await FluentActions.Awaiting(() => client.Auth.GetCallerAsync()).Should().ThrowAsync<HttpRequestException>();
        IsCertificateRefusal(request.Which).Should().BeTrue("the request fails on the certificate, not on the network");

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Failed);
        stop.Message.Should().Be(RoofStopText.Failed(RoofText.Unreachable));
        IsCertificateRefusal(stop.Error).Should().BeTrue();

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.LastError is not null, "the hub's refusal");
        IsCertificateRefusal(feed.LastError).Should().BeTrue();
        feed.State.Should().NotBe(RoofStatusFeedState.Connected);
        feed.ConnectionCount.Should().Be(0);
        feed.Current.Should().BeNull();
    }

    [TestMethod]
    public void ThePin_IsTheCertificatesSha256_AndAcceptsColons()
    {
        var pin = RoofCertificatePin.GetSha256(_controller);
        pin.Should().Be(Convert.ToHexString(SHA256.HashData(_controller.RawData)));
        var withColons = string.Join(':', Enumerable.Range(0, pin.Length / 2).Select(i => pin.Substring(i * 2, 2)));
        FluentActions.Invoking(() => CreatePinnedClient(withColons).Dispose()).Should().NotThrow();
    }

    private static RoofControllerClient CreatePinnedClient(string? pin) => new(new RoofConnectionOptions
    {
        BaseAddress = _host.BaseAddress,
        Credential = new RoofApiKeyCredential(TestApiKeys.Viewer),
        ServerCertificateSha256 = pin,
        StatusFeed = FastFeed
    });

    private static bool IsCertificateRefusal(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCertificateRefusal))
            {
                return true;
            }
        }

        return false;
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Through PKCS#12, so the key is one the TLS stack can use on every platform.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Only what the pin test needs from a controller: who am I, Stop, and a hub that offers WebSockets only.</summary>
    private sealed class PinnedController : IAsyncDisposable
    {
        public const string InstanceId = "pin-test-instance";

        private readonly WebApplication _app;

        private PinnedController(WebApplication app, ConcurrentQueue<string> hubKeys)
        {
            _app = app;
            HubKeys = hubKeys;
            BaseAddress = new Uri(app.Urls.Single());
        }

        public Uri BaseAddress { get; }

        public ConcurrentQueue<string> HubKeys { get; }

        public static async Task<PinnedController> StartAsync(X509Certificate2 certificate)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            builder.Logging.ClearProviders();
            var hubKeys = new ConcurrentQueue<string>();
            builder.Services.AddSingleton(hubKeys);
            builder.Services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions = RoofClientJson.Create());

            var app = builder.Build();
            app.MapGet("api/v4.0/Auth/Me", () => Results.Json(
                new RoofCallerResponse("pin-test", RoofControllerApiContract.ViewerRole, RoofCredentialKind.ApiKey, null, null, null, false),
                RoofClientJson.Options));
            app.MapPost("api/v4.0/RoofControl/Stop", () => Results.Json(
                RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Verified),
                RoofClientJson.Options));
            // WebSockets only, so a connected feed proves the pin reached the WebSocket as well as the negotiate request.
            app.MapHub<StatusHub>(RoofStatusHubContract.Path, hub => hub.Transports = HttpTransportType.WebSockets);
            await app.StartAsync();
            return new PinnedController(app, hubKeys);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed class StatusHub(ConcurrentQueue<string> hubKeys) : Hub
        {
            public override async Task OnConnectedAsync()
            {
                hubKeys.Enqueue(Context.GetHttpContext()!.Request.Headers[RoofControllerApiContract.ApiKeyHeaderName].ToString());
                await Clients.Caller.SendAsync(
                    RoofStatusHubContract.StatusMethod,
                    new RoofStatusHubMessage(RoofServiceMock.Snapshot(), 1, DateTimeOffset.UtcNow, InstanceId));
            }
        }
    }
}
