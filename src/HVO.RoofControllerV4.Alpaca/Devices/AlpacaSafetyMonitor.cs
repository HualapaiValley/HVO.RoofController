using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// The observatory's weather as Alpaca SafetyMonitor 0 (ISafetyMonitorV3, #220 phase 2): <c>IsSafe</c> is true only while
/// the controller's last published status says the weather alone is Safe (<see cref="RoofSafetyStatus.WeatherState"/>:
/// at least one required weather source, every required one fresh and safe). It never includes the telescopes: an
/// observing program parks its mount and closes the roof when its safety monitor turns unsafe, so one that turned unsafe
/// whenever a mount unparked would end every session. Anything short of Safe reads false: unsafe or unknown weather, a
/// controller that does not report it, and an error (not connected, the controller unreachable or silent for 3 s), whose
/// Value is false too. Reading it never reaches the roof. It has its own connections, apart from the Dome's.
/// </summary>
/// <remarks>
/// It says safe only while the controller lists its UniqueID among its own Alpaca devices
/// (<see cref="RoofSafetyStatus.OwnAlpacaUniqueIds"/>, <c>RoofSafety:OwnAlpacaUniqueIds</c>): a controller that lists it
/// refuses it as a weather source, so the monitor is never read back into the weather it publishes (#220). A controller
/// that does not list it gets false, with why in the status action, the setup page and the log.
/// </remarks>
public sealed class AlpacaSafetyMonitor : AlpacaDevice
{
    /// <summary>The device type in URLs and the management API.</summary>
    public const string DeviceType = "safetymonitor";

    /// <summary>The Alpaca device type name.</summary>
    public const string DeviceTypeName = "SafetyMonitor";

    /// <summary>ISafetyMonitorV3.</summary>
    public const int InterfaceVersion = 3;

    /// <summary>The device's name.</summary>
    public const string DeviceName = "HVO observatory weather";

    /// <summary>The device's description; ASCOM allows at most 64 characters.</summary>
    public const string DeviceDescription = "Hualapai Valley Observatory weather, from the roof controller";

    private readonly AlpacaRoof _roof;
    private readonly ILogger<AlpacaSafetyMonitor> _logger;
    private int _listed = -1;

    public AlpacaSafetyMonitor(AlpacaRoof roof, AlpacaDeviceIds ids, AlpacaOptions options, TimeProvider time, ILogger<AlpacaSafetyMonitor> logger)
        : base(new AlpacaConnections(options, time), time, "the observatory's weather from the roof controller's weather sources, never its telescopes")
    {
        _roof = roof;
        Ids = ids;
        _logger = logger;
    }

    /// <summary>The server's devices' identities: this monitor's is <see cref="AlpacaDeviceIds.SafetyMonitor"/>.</summary>
    public AlpacaDeviceIds Ids { get; }

    /// <summary>The controller settings that list this server's devices as the controller's own, for the setup page and the log.</summary>
    public string ControllerSettings
        => $"RoofSafety__OwnAlpacaUniqueIds__0={Ids.Dome}, RoofSafety__OwnAlpacaUniqueIds__1={Ids.SafetyMonitor}";

    public override string UrlType => DeviceType;

    public override string TypeName => DeviceTypeName;

    public override string DisplayName => DeviceName;

    public override string DescriptionText => DeviceDescription;

    public override int AlpacaInterfaceVersion => InterfaceVersion;

    /// <summary>
    /// True only when <paramref name="status"/> reports the weather alone Safe and lists this monitor's UniqueID among the
    /// controller's own Alpaca devices.
    /// </summary>
    public bool IsSafe(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Safety is { WeatherState: RoofSafetyState.Safe } && IsListed(status);
    }

    /// <summary>True when the controller lists this monitor's UniqueID among its own (so it refuses to read it back).</summary>
    public bool IsListed(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var listed = status.Safety?.OwnAlpacaUniqueIds.Any(id => string.Equals(id?.Trim(), Ids.SafetyMonitor, StringComparison.OrdinalIgnoreCase)) == true;
        if (Interlocked.Exchange(ref _listed, listed ? 1 : 0) != (listed ? 1 : 0))
        {
            if (listed)
            {
                _logger.LogInformation("The roof controller lists this server's SafetyMonitor ({UniqueId}) as its own, so it never reads it back: IsSafe follows the weather", Ids.SafetyMonitor);
            }
            else
            {
                _logger.LogWarning(
                    "The roof controller does not list this server's SafetyMonitor ({UniqueId}) as its own, so it could read it back as its weather: IsSafe stays false until it does. Give the controller: {Settings}",
                    Ids.SafetyMonitor, ControllerSettings);
            }
        }

        return listed;
    }

    /// <summary>The <c>status</c> action's text: the weather and why it is not safe, as the controller words it.</summary>
    public string DescribeWeather(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Safety is not { } safety)
        {
            return "Weather: Unknown: the roof controller does not report its safety conditions.";
        }

        var reasons = safety.WeatherState != RoofSafetyState.Safe && safety.WeatherReasons.Count > 0
            ? $": {string.Join("; ", safety.WeatherReasons)}"
            : string.Empty;
        var weather = $"Weather: {RoofStatusText.DescribeSafetyState(safety.WeatherState)}{reasons}.";
        return IsListed(status)
            ? weather
            : $"{weather} IsSafe is false: the roof controller does not list this monitor's UniqueID {Ids.SafetyMonitor} in RoofSafety:OwnAlpacaUniqueIds, so it could read this monitor back as its own weather.";
    }

    protected override void AddMembers(IDictionary<string, Member> members)
        => members["issafe"] = Connected(_ => AlpacaResult.Of(IsSafe(_roof.GetCurrent())), False);

    protected override AlpacaResult DeviceState()
    {
        var safe = IsSafe(_roof.GetCurrent());
        return State(("IsSafe", w => w.WriteBooleanValue(safe)));
    }

    protected override string DescribeNow() => DescribeWeather(_roof.GetCurrent());
}
