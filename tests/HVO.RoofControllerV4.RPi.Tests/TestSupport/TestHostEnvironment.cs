using System;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>A host environment with only a name, for code that checks the environment.</summary>
internal sealed class TestHostEnvironment(string environmentName = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "HVO.RoofControllerV4.RPi.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Builds <see cref="RoofHatConnection"/> instances for tests.</summary>
internal static class HatConnections
{
    /// <summary>The connection with the HAT emulator off (the physical HAT, or the register simulation without a bus).</summary>
    public static RoofHatConnection Hardware() => Create(new HatEmulatorOptions());

    /// <summary>The connection in emulator mode (Development), pointed at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static RoofHatConnection Emulated(string host = "127.0.0.1", int port = HatEmulatorOptions.DefaultPort)
        => Create(new HatEmulatorOptions { Enabled = true, Host = host, Port = port });

    public static RoofHatConnection Create(HatEmulatorOptions options, string environment = "Development")
        => new(Options.Create(options), new TestHostEnvironment(environment));
}
