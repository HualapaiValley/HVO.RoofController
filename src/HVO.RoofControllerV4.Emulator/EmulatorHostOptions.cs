using System.Net;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;

namespace HVO.RoofControllerV4.Emulator;

/// <summary>The emulator's settings (section <c>Emulator</c>; environment variables <c>Emulator__RegisterPort</c> and so on).</summary>
public sealed class EmulatorHostOptions
{
    public const string SectionName = "Emulator";

    /// <summary>The control API's default URL: loopback only.</summary>
    public const string DefaultControlUrl = "http://127.0.0.1:5290";

    /// <summary>The address the register port listens on: loopback by default. An IP address, not a host name.</summary>
    public string RegisterAddress { get; set; } = "127.0.0.1";

    /// <summary>The register port the controller's <c>HatEmulator:Port</c> points at. 0 picks a free port (tests).</summary>
    public int RegisterPort { get; set; } = HatEmulatorOptions.DefaultPort;

    /// <summary>How many times as fast as real time the roof runs; the controller's timing is not scaled.</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Distance between the limits' operating points, metres (20 s of travel at the default 2.0).</summary>
    public double? TravelMeters { get; set; }

    /// <summary>Where the roof starts, metres from the closed limit's operating point (just past the closed limit by default).</summary>
    public double? InitialPosition { get; set; }

    /// <summary>The register endpoint; throws when the address or port is not valid.</summary>
    public IPEndPoint RegisterEndPoint()
    {
        if (!IPAddress.TryParse(RegisterAddress, out var address))
        {
            throw new InvalidOperationException($"{SectionName}:RegisterAddress must be an IP address, such as 127.0.0.1 or 0.0.0.0.");
        }

        if (RegisterPort is < 0 or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"{SectionName}:RegisterPort must be between 0 and {IPEndPoint.MaxPort}.");
        }

        return new IPEndPoint(address, RegisterPort);
    }

    /// <summary>The session the settings describe: the documented installation, with the travel and start position given.</summary>
    public HatEmulatorSessionOptions SessionOptions()
    {
        var plant = new RoofPlantOptions();
        if (TravelMeters is { } travel)
        {
            plant = plant with { Mechanics = plant.Mechanics with { TravelMeters = travel } };
        }

        if (InitialPosition is { } position)
        {
            plant = plant with { InitialPosition = position };
        }

        var options = new HatEmulatorSessionOptions { Plant = plant, TimeScale = TimeScale };
        options.Validate();
        return options;
    }
}
