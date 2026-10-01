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
/// negotiate request. <see cref="Present"/> changes the certificate for the connections that follow, and
/// <see cref="ServedAuthority"/> is the CA it serves at <c>GET /ca.crt</c>.
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

    /// <summary>The CA <c>GET /ca.crt</c> serves as PEM, as the controller does; null answers 404.</summary>
    public X509Certificate2? ServedAuthority { get; set; }

    /// <summary>What <c>GET /ca.crt</c> serves instead of <see cref="ServedAuthority"/>, when set.</summary>
    public string? ServedCaText { get; set; }

    /// <summary>
    /// When set, <c>GET /ca.crt</c> sends its headers and the first bytes of a longer body, then
    /// <see cref="CaBodyEnd.Stalls"/> (sends nothing more) or <see cref="CaBodyEnd.IsCutOff"/> (closes the connection).
    /// </summary>
    public CaBodyEnd? CaBodyEnds { get; set; }

    /// <summary>For each <c>GET /ca.crt</c>, whether it carried a credential (an API key or a bearer token).</summary>
    public ConcurrentQueue<bool> CaRequestCredentials { get; } = new();

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
        app.MapGet("ca.crt", async (HttpContext http) =>
        {
            var request = http.Request;
            controller!.CaRequestCredentials.Enqueue(
                request.Headers.ContainsKey(RoofControllerApiContract.ApiKeyHeaderName) || request.Headers.ContainsKey("Authorization"));
            if (controller.CaBodyEnds is { } end)
            {
                http.Response.ContentType = "application/x-x509-ca-cert";
                http.Response.ContentLength = 4000;
                await http.Response.Body.WriteAsync("-----BEGIN CERTIFICATE-----\n"u8.ToArray());
                await http.Response.Body.FlushAsync();
                if (end == CaBodyEnd.IsCutOff)
                {
                    // Time for the headers to be read first: an abort that overtakes them fails the request itself.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), http.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
                    http.Abort();
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, http.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
                }

                return Results.Empty;
            }

            return controller.ServedCaText is { } text ? Results.Text(text, "application/x-x509-ca-cert")
                : controller.ServedAuthority is { } authority ? Results.Text(authority.ExportCertificatePem() + "\n", "application/x-x509-ca-cert")
                : Results.NotFound();
        });
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

/// <summary>How <see cref="TlsTestController"/>'s <c>GET /ca.crt</c> ends a body it does not finish.</summary>
public enum CaBodyEnd
{
    Stalls,
    IsCutOff
}
