namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Connects the controller to the HAT emulator (<c>HVO.RoofControllerV4.Emulator</c>) over TCP instead of the Pi's I2C bus.
/// The emulator answers the SM-I-010 register accesses from an emulated HAT, drive, limit switches and roof.
/// </summary>
/// <remarks>
/// Emulator mode is for development and testing. Outside the Development environment the controller refuses it unless
/// <see cref="AllowOutsideDevelopment"/> is set, and the deployment check and the deployment script require an explicit
/// operator flag for it. A controller in emulator mode never operates the physical roof.
/// </remarks>
public sealed class HatEmulatorOptions
{
    public const string SectionName = "HatEmulator";

    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int DefaultPort = 5291;
    public static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Use the emulator instead of the Pi's I2C bus.</summary>
    public bool Enabled { get; set; }

    /// <summary>The emulator's host name or address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The emulator's register port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>How long a connection attempt may take.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long one register access may take, from sending the request to the complete response. A late response drops the
    /// connection and fails the access, as a failed I2C transfer does.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Allows emulator mode outside the Development environment (a test rig, never the observatory roof).</summary>
    public bool AllowOutsideDevelopment { get; set; }

    /// <summary>The problems with the connection settings; empty when they are usable. Not checked while disabled.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enabled)
        {
            return problems;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            problems.Add($"{SectionName}:Host must not be empty.");
        }

        if (Port is < MinPort or > MaxPort)
        {
            problems.Add($"{SectionName}:Port must be between {MinPort} and {MaxPort}.");
        }

        if (ConnectTimeout < MinTimeout || ConnectTimeout > MaxTimeout)
        {
            problems.Add($"{SectionName}:ConnectTimeout must be between {MinTimeout.TotalMilliseconds:0} ms and {MaxTimeout.TotalSeconds:0} s.");
        }

        if (RequestTimeout < MinTimeout || RequestTimeout > MaxTimeout)
        {
            problems.Add($"{SectionName}:RequestTimeout must be between {MinTimeout.TotalMilliseconds:0} ms and {MaxTimeout.TotalSeconds:0} s.");
        }

        return problems;
    }

    /// <summary>The emulator endpoint as <c>host:port</c>, for logs and reports.</summary>
    public string Endpoint => $"{Host}:{Port}";
}
