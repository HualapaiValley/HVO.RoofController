using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.Simulation.Camera;
using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// The emulator's MJPEG camera endpoint on its own loopback Kestrel port, as the controller's camera proxy reaches
/// Blue Iris: over a real socket, so the proxy's connect, header and idle timeouts all apply.
/// </summary>
internal sealed class EmulatedCameraHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EmulatedCameraHost(WebApplication app, EmulatedCamera camera)
    {
        _app = app;
        Camera = camera;
        BaseAddress = new Uri(app.Urls.Single());
    }

    public EmulatedCamera Camera { get; }

    /// <summary>The camera server's address, for <c>BlueIris:BaseUrl</c>.</summary>
    public Uri BaseAddress { get; }

    public static async Task<EmulatedCameraHost> StartAsync(Func<HatEmulatorStatus> plant, double framesPerSecond = 5)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var camera = new EmulatedCamera(plant, framesPerSecond);
        builder.Services.AddSingleton(camera);

        var app = builder.Build();
        EmulatedCameraEndpoint.Map(app);
        await app.StartAsync();
        return new EmulatedCameraHost(app, camera);
    }

    public async ValueTask DisposeAsync()
    {
        // Open streams would hold up the server's stop until its timeout.
        Camera.DisconnectAll();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
