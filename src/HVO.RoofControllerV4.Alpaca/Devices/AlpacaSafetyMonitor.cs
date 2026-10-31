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

    public AlpacaSafetyMonitor(AlpacaRoof roof, AlpacaOptions options, TimeProvider time)
        : base(new AlpacaConnections(options, time), time, "the observatory's weather from the roof controller's weather sources, never its telescopes")
    {
        _roof = roof;
    }

    public override string UrlType => DeviceType;

    public override string TypeName => DeviceTypeName;

    public override string DisplayName => DeviceName;

    public override string DescriptionText => DeviceDescription;

    public override int AlpacaInterfaceVersion => InterfaceVersion;

    /// <summary>True only when <paramref name="status"/> reports the weather alone Safe.</summary>
    public static bool IsSafe(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Safety is { WeatherState: RoofSafetyState.Safe };
    }

    /// <summary>The <c>status</c> action's text: the weather and why it is not safe, as the controller words it.</summary>
    public static string DescribeWeather(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Safety is not { } safety)
        {
            return "Weather: Unknown: the roof controller does not report its safety conditions.";
        }

        var reasons = safety.WeatherState != RoofSafetyState.Safe && safety.WeatherReasons.Count > 0
            ? $": {string.Join("; ", safety.WeatherReasons)}"
            : string.Empty;
        return $"Weather: {RoofStatusText.DescribeSafetyState(safety.WeatherState)}{reasons}.";
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
