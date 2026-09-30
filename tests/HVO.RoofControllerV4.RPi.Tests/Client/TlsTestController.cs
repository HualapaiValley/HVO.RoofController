using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Only what the TLS tests need from a controller, over real HTTPS on a loopback port: liveness, who am I, Stop, and a status hub
/// that offers WebSockets only, so a connected feed proves the certificate check reached the WebSocket as well as the
/// negotiate request. <see cref="Present"/> changes the certificate for the connections that follow.
/// </summary>
internal sealed class TlsTestController : IAsyncDisposable
{
    public const string CallerName = "tls-test";

    public const string InstanceId = "tls-test-instance";

    private readonly WebApplication _app;
    private SslStreamCertificateContext _certificate;

    private TlsTestController(WebApplication app, SslStreamCertificateContext certificate, ConcurrentQueue<string> hubKeys)
    {
        _app = app;
        _certificate = certificate;
        HubKeys = hubKeys;
    }

    /// <summary>The address on 127.0.0.1, once started.</summary>
    public Uri BaseAddress => new(_app.Urls.Single());

    /// <summary>The same port, reached as <c>localhost</c>.</summary>
    public Uri LocalhostAddress => new UriBuilder(BaseAddress) { Host = "localhost" }.Uri;

    /// <summary>The API key each hub connection sent.</summary>
    public ConcurrentQueue<string> HubKeys { get; }

    /// <summary>The thumbprint of the certificate each TLS handshake presented.</summary>
    public ConcurrentQueue<string> Presented { get; } = new();

    /// <summary>
    /// Starts on a free loopback port with <paramref name="certificate"/>, sending <paramref name="intermediates"/> with
    /// it.
    /// </summary>
    public static async Task<TlsTestController> StartAsync(X509Certificate2 certificate, params X509Certificate2[] intermediates)
    {
        TlsTestController? controller = null;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = _ =>
            {
                var context = controller!._certificate;
                controller.Presented.Enqueue(context.TargetCertificate.Thumbprint);
                return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = context });
            }
        })));
        builder.Logging.ClearProviders();
        var hubKeys = new ConcurrentQueue<string>();
        builder.Services.AddSingleton(hubKeys);
        builder.Services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions = RoofClientJson.Create());

        var app = builder.Build();
        app.MapGet(RoofApiRoutes.HealthLive, () => Results.Text("Healthy"));
        app.MapGet("api/v4.0/Auth/Me", () => Results.Json(
            new RoofCallerResponse(CallerName, RoofControllerApiContract.ViewerRole, RoofCredentialKind.ApiKey, null, null, null, false),
            RoofClientJson.Options));
        app.MapPost("api/v4.0/RoofControl/Stop", () => Results.Json(
            RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Verified),
            RoofClientJson.Options));
        app.MapHub<StatusHub>(RoofStatusHubContract.Path, hub => hub.Transports = HttpTransportType.WebSockets);

        controller = new TlsTestController(app, CreateContext(certificate, intermediates), hubKeys);
        await app.StartAsync();
        return controller;
    }

    /// <summary>Presents <paramref name="certificate"/> from the next handshake on.</summary>
    public void Present(X509Certificate2 certificate, params X509Certificate2[] intermediates)
        => Volatile.Write(ref _certificate, CreateContext(certificate, intermediates));

    // Offline and with the intermediates given, so only those are sent: nothing is looked up in the machine's stores.
    private static SslStreamCertificateContext CreateContext(X509Certificate2 certificate, X509Certificate2[] intermediates)
        => SslStreamCertificateContext.Create(certificate, new X509Certificate2Collection(intermediates), offline: true);

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
