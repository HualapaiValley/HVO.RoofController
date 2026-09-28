using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Camera;
using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>The emulator host on a free loopback register port, with any other settings given.</summary>
internal sealed class EmulatorHost(Dictionary<string, string?>? settings = null) : WebApplicationFactory<HVO.RoofControllerV4.Emulator.Program>
{
    private readonly List<SocketI2cRegisterClient> _clients = [];

    public HatEmulatorSession Session => Services.GetRequiredService<HatEmulatorSession>();

    public HatEmulatorServer Register => Services.GetRequiredService<HatEmulatorServer>();

    public EmulatedCamera Camera => Services.GetRequiredService<EmulatedCamera>();

    /// <summary>A socket client, as the controller builds it, for the register port.</summary>
    public SocketI2cRegisterClient ConnectRegisterClient()
    {
        var client = new SocketI2cRegisterClient(
            new HatEmulatorOptions { Enabled = true, Host = "127.0.0.1", Port = Register.LocalEndPoint.Port, RequestTimeout = TimeSpan.FromSeconds(10) },
            busId: 1,
            address: 0x0E,
            new CapturingLogger<SocketI2cRegisterClient>());
        _clients.Add(client);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        var values = new Dictionary<string, string?>
        {
            ["Emulator:RegisterAddress"] = "127.0.0.1",
            ["Emulator:RegisterPort"] = "0"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await base.DisposeAsync();
    }
}
