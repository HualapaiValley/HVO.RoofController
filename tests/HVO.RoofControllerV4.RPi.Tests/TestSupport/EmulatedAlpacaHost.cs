using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// The emulator's ASCOM Alpaca devices on their own loopback Kestrel port, as the controller's safety sources reach an
/// Alpaca server: over a real socket, through the controller's own HTTP client (no proxy, no redirect, its timeouts).
/// </summary>
internal sealed class EmulatedAlpacaHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EmulatedAlpacaHost(WebApplication app, EmulatedAlpacaServer alpaca)
    {
        _app = app;
        Alpaca = alpaca;
        BaseAddress = new Uri(app.Urls.Single());
    }

    /// <summary>The devices, to park, unpark, make unsafe or make fail.</summary>
    public EmulatedAlpacaServer Alpaca { get; }

    /// <summary>The Alpaca server's address, for a source's <c>Url</c>.</summary>
    public Uri BaseAddress { get; }

    public static async Task<EmulatedAlpacaHost> StartAsync(int telescopes = 1, int safetyMonitors = 1)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var alpaca = new EmulatedAlpacaServer(telescopes, safetyMonitors);
        builder.Services.AddSingleton(alpaca);

        var app = builder.Build();
        EmulatedAlpacaEndpoint.Map(app);
        await app.StartAsync();
        return new EmulatedAlpacaHost(app, alpaca);
    }

    public async ValueTask DisposeAsync()
    {
        // A device left hanging would hold up the server's stop until its timeout.
        Alpaca.Reset();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
