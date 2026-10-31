namespace HVO.RoofControllerV4.Common.Safety;

/// <summary>
/// A source the controller's safety service reads (docs/safety-service.md). The service calls one source's read at a
/// time, on its own schedule and with its own timeout, so an adapter does no polling or caching of its own: it reads
/// the device when asked, and throws when it cannot (a <see cref="RoofSafetySourceException"/> when it can say why in
/// words fit to show).
/// </summary>
public interface IRoofSafetySource
{
    /// <summary>The name reasons quote ("Main mount"). Give each source its own.</summary>
    string Name { get; }

    /// <summary>
    /// How old this source's last reading may be and still count; null takes the service's
    /// <c>RoofSafety:MaxReadingAge</c>. A source that updates slowly (a cloud sensor, say) may need longer.
    /// </summary>
    TimeSpan? MaxReadingAge { get; }
}

/// <summary>
/// A telescope mount's park state, from which the safety service judges whether the mount is clear of the roof. The
/// controller's adapter reads an ASCOM Alpaca <c>Telescope.AtPark</c> (<c>RoofSafety:Telescopes</c>); the simulation's
/// <c>SimulatedTelescopeParkSource</c> stands in for one in the tests.
/// </summary>
public interface ITelescopeParkSource : IRoofSafetySource
{
    /// <summary>
    /// True (the default) when this mount can reach the roof's path, so the roof may open only while it is shown clear of
    /// the roof; false for one that cannot, which is shown but never decides the conditions.
    /// </summary>
    bool BlocksRoof => true;

    /// <summary>Reads the park state now. Throws, or ends with <paramref name="cancellationToken"/>, when it cannot.</summary>
    Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Whether the weather is safe. The controller's adapter reads an ASCOM Alpaca <c>SafetyMonitor.IsSafe</c>
/// (<c>RoofSafety:WeatherSources</c>); the simulation's <c>SimulatedWeatherSafetySource</c> stands in for one in the
/// tests.
/// </summary>
public interface IWeatherSafetySource : IRoofSafetySource
{
    /// <summary>
    /// Whether the conditions need this source (<see cref="RoofWeatherSourceRequirement.Required"/>, the default) or only
    /// show it (<see cref="RoofWeatherSourceRequirement.Ignored"/>).
    /// </summary>
    RoofWeatherSourceRequirement Requirement => RoofWeatherSourceRequirement.Required;

    /// <summary>Reads the weather's safety now. Throws, or ends with <paramref name="cancellationToken"/>, when it cannot.</summary>
    Task<WeatherSafetyReading> ReadWeatherSafetyAsync(CancellationToken cancellationToken);
}

/// <summary>What a weather source's reading does to the safety conditions (docs/safety-service.md).</summary>
public enum RoofWeatherSourceRequirement
{
    /// <summary>
    /// The conditions are Safe only while this source has a recent reading that says safe: unsafe makes them Unsafe, and
    /// no recent reading makes them Unknown.
    /// </summary>
    Required = 0,

    /// <summary>Shown with its state and reason, but it never makes the conditions Safe, Unsafe or Unknown.</summary>
    Ignored = 1
}

/// <summary>One reading of a telescope mount.</summary>
/// <param name="IsParked">True when the mount reports it is parked.</param>
/// <param name="ObservedUtc">
/// When the device observed it, when it says (wall-clock UTC); null means now. An older observation makes the reading
/// older for <see cref="IRoofSafetySource.MaxReadingAge"/>; a time in the future counts as now.
/// </param>
/// <param name="Detail">Extra words to show with the reading, if any. Never a credential or an address.</param>
public sealed record TelescopeParkReading(bool IsParked, DateTimeOffset? ObservedUtc = null, string? Detail = null);

/// <summary>One reading of a weather safety source.</summary>
/// <param name="IsSafe">True when the source reports the weather safe for an open roof.</param>
/// <param name="ObservedUtc">When the device observed it, when it says (wall-clock UTC); null means now.</param>
/// <param name="Detail">Extra words to show with the reading (the sensor value that made it unsafe, say), if any.</param>
public sealed record WeatherSafetyReading(bool IsSafe, DateTimeOffset? ObservedUtc = null, string? Detail = null);

/// <summary>
/// A read that failed for a reason the adapter can say in words safe to log and to show every client: "the device is
/// not connected", say. The message never carries an address, a credential or the device's own text; the safety service
/// quotes it in the source's reason. Any other exception is logged by its type alone.
/// </summary>
public class RoofSafetySourceException : Exception
{
    public RoofSafetySourceException(string message)
        : base(message)
    {
    }

    public RoofSafetySourceException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
