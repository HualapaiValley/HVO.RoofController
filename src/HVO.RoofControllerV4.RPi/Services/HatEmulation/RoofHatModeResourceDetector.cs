using OpenTelemetry.Resources;

namespace HVO.RoofControllerV4.RPi.Services.HatEmulation;

/// <summary>Adds the HAT mode to the telemetry resource, so traces and metrics from an emulator run are never mistaken for the roof's.</summary>
public sealed class RoofHatModeResourceDetector(RoofHatConnection connection) : IResourceDetector
{
    /// <summary>The resource attribute: <c>emulated</c> or <c>hardware</c> (the physical HAT, or the register simulation where there is no bus).</summary>
    public const string AttributeName = "hvo.roof.hat.mode";

    public const string EmulatedValue = "emulated";
    public const string HardwareValue = "hardware";
    public const string EndpointAttributeName = "hvo.roof.hat.emulator.endpoint";

    public Resource Detect()
    {
        var attributes = new List<KeyValuePair<string, object>>
        {
            new(AttributeName, connection.IsEmulated ? EmulatedValue : HardwareValue)
        };
        if (connection.EmulatorEndpoint is { } endpoint)
        {
            attributes.Add(new(EndpointAttributeName, endpoint));
        }

        return new Resource(attributes);
    }
}
