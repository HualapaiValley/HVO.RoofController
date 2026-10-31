using System.Globalization;

namespace HVO.RoofControllerV4.Simulation.Safety;

/// <summary>How an emulated Alpaca device answers.</summary>
public enum EmulatedAlpacaBehaviour
{
    /// <summary>As a working device: HTTP 200 with <c>ErrorNumber</c> 0, or 0x407 for a member read while not connected.</summary>
    Answers,

    /// <summary>HTTP 200 with <c>ErrorNumber</c> 0x500, the first driver-specific error: the driver failed to read its device.</summary>
    DriverError,

    /// <summary>HTTP 500 with a text body: an error the device's server could not recover from.</summary>
    ServerError,

    /// <summary>No answer at all until the client gives up: a device that stopped responding.</summary>
    Hangs
}

/// <summary>One emulated device's state, as the emulator's control API reports it.</summary>
/// <param name="DeviceType">The Alpaca device type, in lower case: <c>telescope</c> or <c>safetymonitor</c>.</param>
/// <param name="DeviceNumber">Its Alpaca device number.</param>
/// <param name="Name">Its name, as the device's <c>name</c> answers.</param>
/// <param name="UniqueId">Its UniqueID in the Management API's <c>configureddevices</c>.</param>
/// <param name="Connected">What <c>connected</c> answers.</param>
/// <param name="Parked">A telescope's <c>atpark</c>; null for a safety monitor.</param>
/// <param name="CanPark">A telescope's <c>canpark</c> (and <c>canunpark</c>); null for a safety monitor.</param>
/// <param name="Safe">A safety monitor's <c>issafe</c>; null for a telescope.</param>
/// <param name="Behaviour">How it answers.</param>
/// <param name="Requests">The Device API requests it has had.</param>
public sealed record EmulatedAlpacaDeviceStatus(
    string DeviceType,
    int DeviceNumber,
    string Name,
    string UniqueId,
    bool Connected,
    bool? Parked,
    bool? CanPark,
    bool? Safe,
    EmulatedAlpacaBehaviour Behaviour,
    long Requests);

/// <summary>Every emulated device's state.</summary>
public sealed record EmulatedAlpacaStatus(IReadOnlyList<EmulatedAlpacaDeviceStatus> Telescopes, IReadOnlyList<EmulatedAlpacaDeviceStatus> SafetyMonitors);

/// <summary>
/// One answer of the emulated Alpaca server: a status code, and either the Alpaca JSON (<see cref="Json"/>) or a text
/// body (<see cref="Text"/>, for 400 and 500); <see cref="Hang"/> asks the host to answer nothing until the client gives up.
/// </summary>
public sealed record EmulatedAlpacaAnswer(int StatusCode, IReadOnlyDictionary<string, object?>? Json, string? Text, bool Hang = false);

/// <summary>
/// The emulator's ASCOM Alpaca devices, for the controller's safety sources: telescope mounts (<c>AtPark</c>) and weather
/// safety monitors (<c>IsSafe</c>). Modelled on the ASCOM Alpaca Device and Management APIs
/// (https://ascom-standards.org/api/) and the ASCOM interface definitions (ISafetyMonitorV3, ITelescopeV4), not on the
/// controller's adapter, so a controller that disagrees with a real device is caught:
/// <list type="bullet">
/// <item>URLs are lower case (<c>/api/v1/{device_type}/{device_number}/{member}</c>). Parameter names in a GET's query
/// are matched whatever their case; in a PUT's form body they are case sensitive, so a misspelt one is not there.</item>
/// <item>A request the device cannot interpret (an unknown device type, device number or member, a member asked with the
/// wrong method, a parameter missing or not of its type, a <c>ClientID</c> or <c>ClientTransactionID</c> that is not a
/// number from 0 to 4294967295) is HTTP 400 with a text message.</item>
/// <item>An answer is HTTP 200 with the JSON <c>Value</c> (a property read), <c>ClientTransactionID</c> (echoed, 0 when
/// not given), <c>ServerTransactionID</c> (from 1, one more each answer), <c>ErrorNumber</c> and <c>ErrorMessage</c>.
/// An error carries no <c>Value</c>, so a client cannot take it for a reading.</item>
/// <item>Only <c>Connected</c>, <c>Connecting</c>, <c>Name</c>, <c>Description</c>, <c>DriverInfo</c>,
/// <c>DriverVersion</c>, <c>InterfaceVersion</c> and <c>SupportedActions</c> answer while not connected; every other
/// member answers <c>ErrorNumber</c> 0x407 (NotConnectedException).</item>
/// </list>
/// The safety monitor is a whole ISafetyMonitorV3. The mount is a whole ITelescopeV4 that can only park: it stands still
/// on the meridian at the celestial equator, not tracking, every capability false but <c>CanPark</c> and
/// <c>CanUnpark</c>; what a capability gates, and every optional member, answers 0x400 (NotImplementedException). A mount
/// set unable to park answers <c>CanPark</c> false, <c>AtPark</c> false and 0x400 to <c>Park</c>. Both pass ASCOM
/// ConformU with no errors or issues (docs/emulator.md). Nothing here moves or reaches hardware, and the devices answer no
/// Alpaca discovery. Safe to use from any thread.
/// </summary>
public sealed class EmulatedAlpacaServer
{
    /// <summary>The telescope device type, in the Alpaca API's lower case.</summary>
    public const string TelescopeType = "telescope";

    /// <summary>The safety monitor device type, in the Alpaca API's lower case.</summary>
    public const string SafetyMonitorType = "safetymonitor";

    /// <summary>The most devices of a type the emulator serves.</summary>
    public const int MaxDevicesPerType = 8;

    /// <summary>ASCOM NotImplementedException.</summary>
    public const int NotImplementedError = 0x400;

    /// <summary>ASCOM InvalidValueException.</summary>
    public const int InvalidValueError = 0x401;

    /// <summary>ASCOM NotConnectedException.</summary>
    public const int NotConnectedError = 0x407;

    /// <summary>ASCOM ParkedException.</summary>
    public const int ParkedError = 0x408;

    /// <summary>ASCOM ActionNotImplementedException.</summary>
    public const int ActionNotImplementedError = 0x40C;

    /// <summary>The first driver-specific error number, which <see cref="EmulatedAlpacaBehaviour.DriverError"/> answers.</summary>
    public const int DriverError = 0x500;

    /// <summary>The emulated mount's site: made-up coordinates, not the observatory's.</summary>
    public const double SiteLatitude = 30.0;

    /// <summary>The emulated mount's site longitude, degrees east.</summary>
    public const double SiteLongitude = -110.0;

    /// <summary>The emulated mount's site elevation, metres.</summary>
    public const double SiteElevation = 1000.0;

    private static readonly DateTimeOffset J2000 = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // What may be read while not connected (ASCOM Platform 7, "Members available when not connected").
    private static readonly HashSet<string> ReadableWhileNotConnected =
        ["connected", "connecting", "description", "driverinfo", "driverversion", "interfaceversion", "name", "supportedactions"];

    // Every member, by "GET name" or "PUT name", with its parameters: IAscomDeviceV2's, then each interface's.
    private static readonly Dictionary<string, Parameter[]> CommonMembers = Members(
        "GET connected", "GET connecting", "GET description", "GET driverinfo", "GET driverversion", "GET interfaceversion",
        "GET name", "GET supportedactions", "GET devicestate",
        "PUT connected Connected:bool", "PUT connect", "PUT disconnect", "PUT action Action:text Parameters:text",
        "PUT commandblind Command:text Raw:bool", "PUT commandbool Command:text Raw:bool", "PUT commandstring Command:text Raw:bool");

    private static readonly Dictionary<string, Parameter[]> SafetyMonitorMembers = Members("GET issafe");

    private static readonly Dictionary<string, Parameter[]> TelescopeMembers = Members(
        "GET alignmentmode", "GET altitude", "GET aperturearea", "GET aperturediameter", "GET athome", "GET atpark", "GET azimuth",
        "GET canfindhome", "GET canpark", "GET canpulseguide", "GET cansetdeclinationrate", "GET cansetguiderates", "GET cansetpark",
        "GET cansetpierside", "GET cansetrightascensionrate", "GET cansettracking", "GET canslew", "GET canslewaltaz",
        "GET canslewaltazasync", "GET canslewasync", "GET cansync", "GET cansyncaltaz", "GET canunpark", "GET declination",
        "GET declinationrate", "GET doesrefraction", "GET equatorialsystem", "GET focallength", "GET guideratedeclination",
        "GET guideraterightascension", "GET ispulseguiding", "GET rightascension", "GET rightascensionrate", "GET sideofpier",
        "GET siderealtime", "GET siteelevation", "GET sitelatitude", "GET sitelongitude", "GET slewing", "GET slewsettletime",
        "GET targetdeclination", "GET targetrightascension", "GET tracking", "GET trackingrate", "GET trackingrates", "GET utcdate",
        "GET axisrates Axis:int", "GET canmoveaxis Axis:int", "GET destinationsideofpier RightAscension:number Declination:number",
        "PUT declinationrate DeclinationRate:number", "PUT doesrefraction DoesRefraction:bool",
        "PUT guideratedeclination GuideRateDeclination:number", "PUT guideraterightascension GuideRateRightAscension:number",
        "PUT rightascensionrate RightAscensionRate:number", "PUT sideofpier SideOfPier:int", "PUT siteelevation SiteElevation:number",
        "PUT sitelatitude SiteLatitude:number", "PUT sitelongitude SiteLongitude:number", "PUT slewsettletime SlewSettleTime:int",
        "PUT targetdeclination TargetDeclination:number", "PUT targetrightascension TargetRightAscension:number",
        "PUT tracking Tracking:bool", "PUT trackingrate TrackingRate:int", "PUT utcdate UTCDate:date",
        "PUT abortslew", "PUT findhome", "PUT moveaxis Axis:int Rate:number", "PUT park", "PUT pulseguide Direction:int Duration:int",
        "PUT setpark", "PUT slewtoaltaz Azimuth:number Altitude:number", "PUT slewtoaltazasync Azimuth:number Altitude:number",
        "PUT slewtocoordinates RightAscension:number Declination:number",
        "PUT slewtocoordinatesasync RightAscension:number Declination:number", "PUT slewtotarget", "PUT slewtotargetasync",
        "PUT synctoaltaz Azimuth:number Altitude:number", "PUT synctocoordinates RightAscension:number Declination:number",
        "PUT synctotarget", "PUT unpark");

    private readonly object _gate = new();
    private readonly Device[] _telescopes;
    private readonly Device[] _monitors;
    private readonly TimeProvider _time;
    private uint _serverTransactions;

    /// <param name="telescopes">How many mounts to serve (0 to 8), numbered from 0, each parked and connected.</param>
    /// <param name="safetyMonitors">How many safety monitors to serve (0 to 8), numbered from 0, each safe and connected.</param>
    /// <param name="time">The clock for the mount's sidereal time and UTC date and <c>DeviceState</c>'s TimeStamp; the system's by default.</param>
    public EmulatedAlpacaServer(int telescopes = 1, int safetyMonitors = 1, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(telescopes);
        ArgumentOutOfRangeException.ThrowIfNegative(safetyMonitors);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(telescopes, MaxDevicesPerType);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(safetyMonitors, MaxDevicesPerType);
        _time = time ?? TimeProvider.System;
        _telescopes = [.. Enumerable.Range(0, telescopes).Select(number => new Device(TelescopeType, number, $"Emulated mount {number}"))];
        _monitors = [.. Enumerable.Range(0, safetyMonitors).Select(number => new Device(SafetyMonitorType, number, $"Emulated safety monitor {number}"))];
    }

    private enum ParameterType
    {
        Boolean,
        Number,
        Integer,
        Text,
        Date
    }

    /// <summary>Every device's state now.</summary>
    public EmulatedAlpacaStatus GetStatus()
    {
        lock (_gate)
        {
            return new EmulatedAlpacaStatus([.. _telescopes.Select(Describe)], [.. _monitors.Select(Describe)]);
        }
    }

    /// <summary>
    /// Changes a mount: parked or not, connected or not, able to park or not, and how it answers. Null leaves a setting
    /// as it is. Throws <see cref="ArgumentException"/> for a device number it does not serve.
    /// </summary>
    public void SetTelescope(int deviceNumber, bool? parked = null, bool? connected = null, EmulatedAlpacaBehaviour? behaviour = null, bool? canPark = null)
        => Set(_telescopes, "telescope", deviceNumber, parked, connected, behaviour, canPark);

    /// <summary>
    /// Changes a safety monitor: safe or not, connected or not, and how it answers. Null leaves a setting as it is. Throws
    /// <see cref="ArgumentException"/> for a device number it does not serve.
    /// </summary>
    public void SetSafetyMonitor(int deviceNumber, bool? safe = null, bool? connected = null, EmulatedAlpacaBehaviour? behaviour = null)
        => Set(_monitors, "safety monitor", deviceNumber, safe, connected, behaviour, canPark: null);

    /// <summary>Every device parked or safe, able to park, connected and answering again; the request counts start again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var device in _telescopes.Concat(_monitors))
            {
                device.Value = true;
                device.CanPark = true;
                device.Connected = true;
                device.Behaviour = EmulatedAlpacaBehaviour.Answers;
                device.Requests = 0;
            }
        }
    }

    /// <summary>
    /// Answers a Device API request, <c>GET</c> or <c>PUT /api/v1/{deviceType}/{deviceNumber}/{member}</c>, with its
    /// parameters: the query string's for a GET, the form body's for a PUT.
    /// </summary>
    public EmulatedAlpacaAnswer Answer(string method, string deviceType, string deviceNumber, string member, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(deviceType);
        ArgumentNullException.ThrowIfNull(deviceNumber);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(parameters);
        var put = string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase);
        if (!put && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest($"The Alpaca Device API takes GET and PUT, not {method}.");
        }

        var (devices, members) = deviceType switch
        {
            TelescopeType => (_telescopes, TelescopeMembers),
            SafetyMonitorType => (_monitors, SafetyMonitorMembers),
            _ => (null, null)
        };
        if (devices is null || members is null)
        {
            // Alpaca URLs are lower case, and only these two types are served.
            return BadRequest($"The device type '{deviceType}' is not served here: only telescope and safetymonitor, in lower case.");
        }

        if (!uint.TryParse(deviceNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number >= devices.Length)
        {
            return BadRequest($"There is no {deviceType} device number {deviceNumber}.");
        }

        var values = parameters.ToList();
        var names = put ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!TryReadTransaction(values, "ClientID", names, out _, out var problem)
            || !TryReadTransaction(values, "ClientTransactionID", names, out var clientTransaction, out problem))
        {
            return BadRequest(problem!);
        }

        var key = $"{(put ? "PUT" : "GET")} {member}";
        if (!CommonMembers.TryGetValue(key, out var expected) && !members.TryGetValue(key, out expected))
        {
            return BadRequest($"The {deviceType} has no member '{member}' to {(put ? "PUT" : "GET")} (members are lower case).");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in expected)
        {
            var text = Find(values, parameter.Name, names);
            if (text is null || !IsOfType(text, parameter.Type))
            {
                return BadRequest($"{parameter.Name} is missing or is not {Describe(parameter.Type)}.");
            }

            arguments[parameter.Name] = text;
        }

        var device = devices[number];
        lock (_gate)
        {
            device.Requests++;
            switch (device.Behaviour)
            {
                case EmulatedAlpacaBehaviour.Hangs:
                    return new EmulatedAlpacaAnswer(200, null, null, Hang: true);
                case EmulatedAlpacaBehaviour.ServerError:
                    return new EmulatedAlpacaAnswer(500, null, "The emulated device's server failed (emulator fault injection).");
                case EmulatedAlpacaBehaviour.DriverError:
                    return Error(clientTransaction, DriverError, "The emulated driver could not reach its device (emulator fault injection).");
            }

            var outcome = put ? Call(device, member, arguments) : Read(device, member, arguments);
            return outcome.Error != 0 ? Error(clientTransaction, outcome.Error, outcome.Message)
                : outcome.HasValue ? new EmulatedAlpacaAnswer(200, Response(clientTransaction, outcome.Value, hasValue: true, 0, string.Empty), null)
                : new EmulatedAlpacaAnswer(200, Response(clientTransaction, null, hasValue: false, 0, string.Empty), null);
        }
    }

    /// <summary>
    /// The Management API's answers: <c>apiversions</c>, <c>v1/description</c> and <c>v1/configureddevices</c>; null for
    /// any other path.
    /// </summary>
    public EmulatedAlpacaAnswer? AnswerManagement(string path, IEnumerable<KeyValuePair<string, string?>> query)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);
        var values = query.ToList();
        if (!TryReadTransaction(values, "ClientID", StringComparison.OrdinalIgnoreCase, out _, out var problem)
            || !TryReadTransaction(values, "ClientTransactionID", StringComparison.OrdinalIgnoreCase, out var clientTransaction, out problem))
        {
            return BadRequest(problem!);
        }

        lock (_gate)
        {
            object? value = path switch
            {
                "apiversions" => new[] { 1 },
                "v1/description" => new Dictionary<string, object?>
                {
                    ["ServerName"] = "HVO HAT emulator",
                    ["Manufacturer"] = "Hualapai Valley Observatory",
                    ["ManufacturerVersion"] = "1.0",
                    ["Location"] = "Emulated (no hardware)"
                },
                "v1/configureddevices" => _telescopes.Concat(_monitors).Select(device => new Dictionary<string, object?>
                {
                    ["DeviceName"] = device.Name,
                    ["DeviceType"] = device.Type == TelescopeType ? "Telescope" : "SafetyMonitor",
                    ["DeviceNumber"] = device.Number,
                    ["UniqueID"] = device.UniqueId
                }).ToArray(),
                _ => null
            };
            if (value is null)
            {
                return null;
            }

            return new EmulatedAlpacaAnswer(200, new Dictionary<string, object?>
            {
                ["Value"] = value,
                ["ClientTransactionID"] = clientTransaction,
                ["ServerTransactionID"] = NextServerTransactionLocked(),
                ["ErrorNumber"] = 0,
                ["ErrorMessage"] = string.Empty
            }, null);
        }
    }

    // The caller holds the gate.
    private Outcome Read(Device device, string member, Dictionary<string, string> arguments)
    {
        if (!device.Connected && !ReadableWhileNotConnected.Contains(member))
        {
            return Outcome.Failed(NotConnectedError, $"{device.Name} is not connected.");
        }

        var telescope = device.Type == TelescopeType;
        switch (member)
        {
            case "connected":
                return Outcome.Of(device.Connected);
            case "connecting":
                // Connect completes at once here, so a connection is never in progress.
                return Outcome.Of(false);
            case "name":
                return Outcome.Of(device.Name);
            case "description":
                return Outcome.Of($"{device.Name}, emulated by the HVO HAT emulator");
            case "driverinfo":
                return Outcome.Of("HVO HAT emulator: an emulated ASCOM Alpaca device for development and tests");
            case "driverversion":
                return Outcome.Of("1.0");
            case "interfaceversion":
                // ITelescopeV4 and ISafetyMonitorV3: the interfaces of ASCOM Platform 7.
                return Outcome.Of(telescope ? 4 : 3);
            case "supportedactions":
                return Outcome.Of(Array.Empty<string>());
            case "devicestate":
                return Outcome.Of(telescope ? TelescopeState(device) : MonitorState(device));
            case "issafe":
                return Outcome.Of(device.Value);
        }

        return ReadTelescope(device, member, arguments);
    }

    // The caller holds the gate. The mount stands still on the meridian at the celestial equator, not tracking.
    private Outcome ReadTelescope(Device device, string member, Dictionary<string, string> arguments)
    {
        switch (member)
        {
            case "athome" or "slewing" or "tracking"
                or "canfindhome" or "canpulseguide" or "cansetdeclinationrate" or "cansetguiderates" or "cansetpark"
                or "cansetpierside" or "cansetrightascensionrate" or "cansettracking" or "canslew" or "canslewaltaz"
                or "canslewaltazasync" or "canslewasync" or "cansync" or "cansyncaltaz":
                return Outcome.Of(false);
            case "canpark" or "canunpark":
                return Outcome.Of(device.CanPark);
            case "atpark":
                // A mount that cannot park is never parked.
                return Outcome.Of(Parked(device));
            case "declination" or "declinationrate" or "rightascensionrate":
                return Outcome.Of(0.0);
            case "rightascension" or "siderealtime":
                return Outcome.Of(LocalSiderealHours());
            case "equatorialsystem":
                // equTopocentric.
                return Outcome.Of(1);
            case "sitelatitude":
                return Outcome.Of(SiteLatitude);
            case "sitelongitude":
                return Outcome.Of(SiteLongitude);
            case "siteelevation":
                return Outcome.Of(SiteElevation);
            case "trackingrate":
                // driveSidereal, the only rate.
                return Outcome.Of(0);
            case "trackingrates":
                return Outcome.Of(new[] { 0 });
            case "utcdate":
                return Outcome.Of(UtcDate());
            case "axisrates":
                return IsAxis(arguments["Axis"]) ? Outcome.Of(Array.Empty<object>()) : Outcome.Failed(InvalidValueError, "Axis must be 0, 1 or 2.");
            case "canmoveaxis":
                return IsAxis(arguments["Axis"]) ? Outcome.Of(false) : Outcome.Failed(InvalidValueError, "Axis must be 0, 1 or 2.");
            default:
                // AlignmentMode, Altitude, Azimuth, the aperture and focal length, DoesRefraction, the guide rates,
                // IsPulseGuiding, SideOfPier, DestinationSideOfPier, SlewSettleTime and the targets: optional, or gated
                // by a capability this mount does not have.
                return Outcome.Failed(NotImplementedError, $"{member} is not implemented by this mount.");
        }
    }

    // The caller holds the gate.
    private Outcome Call(Device device, string member, Dictionary<string, string> arguments)
    {
        switch (member)
        {
            case "connected":
                device.Connected = bool.Parse(arguments["Connected"]);
                return Outcome.Done;
            case "connect":
                device.Connected = true;
                return Outcome.Done;
            case "disconnect":
                device.Connected = false;
                return Outcome.Done;
        }

        if (!device.Connected)
        {
            return Outcome.Failed(NotConnectedError, $"{device.Name} is not connected.");
        }

        switch (member)
        {
            case "action":
                return Outcome.Failed(ActionNotImplementedError, "The emulated device supports no actions.");
            case "commandblind" or "commandbool" or "commandstring":
                // Deprecated in Platform 7.
                return Outcome.Failed(NotImplementedError, $"{member} is not implemented.");
            case "park" or "unpark" when !device.CanPark:
                return Outcome.Failed(NotImplementedError, $"{device.Name} cannot park.");
            case "park":
                device.Value = true;
                return Outcome.Done;
            case "unpark":
                device.Value = false;
                return Outcome.Done;
            case "abortslew" when Parked(device):
                return Outcome.Failed(ParkedError, $"{device.Name} is parked.");
            case "abortslew":
                // Nothing is slewing.
                return Outcome.Done;
            default:
                // Every write and every motion this mount cannot do.
                return Outcome.Failed(NotImplementedError, $"{member} is not implemented by this mount.");
        }
    }

    private object[] TelescopeState(Device device) =>
    [
        State("AtHome", false),
        State("AtPark", Parked(device)),
        State("Declination", 0.0),
        State("RightAscension", LocalSiderealHours()),
        State("SiderealTime", LocalSiderealHours()),
        State("Slewing", false),
        State("Tracking", false),
        State("UTCDate", UtcDate()),
        State("TimeStamp", TimeStamp())
    ];

    private object[] MonitorState(Device device) => [State("IsSafe", device.Value), State("TimeStamp", TimeStamp())];

    private static Dictionary<string, object?> State(string name, object value) => new() { ["Name"] = name, ["Value"] = value };

    private static bool Parked(Device device) => device.Value && device.CanPark;

    private static bool IsAxis(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) is >= 0 and <= 2;

    /// <summary>Local mean sidereal time at <see cref="SiteLongitude"/>, hours (0 to 24).</summary>
    private double LocalSiderealHours()
    {
        var days = (_time.GetUtcNow() - J2000).TotalDays;
        var hours = (18.697374558 + (24.06570982441908 * days) + (SiteLongitude / 15.0)) % 24.0;
        return hours < 0 ? hours + 24.0 : hours;
    }

    private string UtcDate() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private string TimeStamp() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private void Set(Device[] devices, string kind, int deviceNumber, bool? value, bool? connected, EmulatedAlpacaBehaviour? behaviour, bool? canPark)
    {
        if (deviceNumber < 0 || deviceNumber >= devices.Length)
        {
            throw new ArgumentException(devices.Length == 0
                ? $"The emulator serves no {kind}."
                : $"The emulator serves {kind} device numbers 0 to {devices.Length - 1}.");
        }

        if (behaviour is { } chosen && !Enum.IsDefined(chosen))
        {
            throw new ArgumentException("The behaviour must be Answers, DriverError, ServerError or Hangs.");
        }

        lock (_gate)
        {
            var device = devices[deviceNumber];
            device.Value = value ?? device.Value;
            device.Connected = connected ?? device.Connected;
            device.Behaviour = behaviour ?? device.Behaviour;
            device.CanPark = canPark ?? device.CanPark;
        }
    }

    // The caller holds the gate.
    private EmulatedAlpacaAnswer Error(uint clientTransaction, int error, string message)
        => new(200, Response(clientTransaction, null, hasValue: false, error, message), null);

    // The caller holds the gate.
    private Dictionary<string, object?> Response(uint clientTransaction, object? value, bool hasValue, int error, string message)
    {
        var json = new Dictionary<string, object?>();
        if (hasValue)
        {
            json["Value"] = value;
        }

        json["ClientTransactionID"] = clientTransaction;
        json["ServerTransactionID"] = NextServerTransactionLocked();
        json["ErrorNumber"] = error;
        json["ErrorMessage"] = message;
        return json;
    }

    private uint NextServerTransactionLocked()
    {
        _serverTransactions = _serverTransactions == uint.MaxValue ? 1 : _serverTransactions + 1;
        return _serverTransactions;
    }

    private static EmulatedAlpacaAnswer BadRequest(string message) => new(400, null, message);

    /// <summary>The members, from "METHOD name Parameter:type ..." lines.</summary>
    private static Dictionary<string, Parameter[]> Members(params string[] lines)
        => lines.Select(line => line.Split(' ')).ToDictionary(
            parts => $"{parts[0]} {parts[1]}",
            parts => parts.Skip(2).Select(spec =>
            {
                var (name, type) = (spec[..spec.IndexOf(':', StringComparison.Ordinal)], spec[(spec.IndexOf(':', StringComparison.Ordinal) + 1)..]);
                return new Parameter(name, type switch
                {
                    "bool" => ParameterType.Boolean,
                    "number" => ParameterType.Number,
                    "int" => ParameterType.Integer,
                    "date" => ParameterType.Date,
                    _ => ParameterType.Text
                });
            }).ToArray(),
            StringComparer.Ordinal);

    private static bool IsOfType(string text, ParameterType type) => type switch
    {
        ParameterType.Boolean => bool.TryParse(text, out _),
        ParameterType.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
        ParameterType.Integer => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        ParameterType.Date => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
        _ => true
    };

    private static string Describe(ParameterType type) => type switch
    {
        ParameterType.Boolean => "True or False",
        ParameterType.Number => "a number",
        ParameterType.Integer => "a whole number",
        ParameterType.Date => "an ISO 8601 date and time",
        _ => "given"
    };

    private static string? Find(IEnumerable<KeyValuePair<string, string?>> parameters, string name, StringComparison names)
        => parameters.FirstOrDefault(pair => string.Equals(pair.Key, name, names)).Value;

    // A missing number is 0, the Alpaca API's "not supplied".
    private static bool TryReadTransaction(IEnumerable<KeyValuePair<string, string?>> parameters, string name, StringComparison names, out uint value, out string? problem)
    {
        value = 0;
        problem = null;
        foreach (var (key, text) in parameters)
        {
            if (!string.Equals(key, name, names))
            {
                continue;
            }

            if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                problem = $"{name} must be a whole number from 0 to 4294967295.";
                return false;
            }

            return true;
        }

        return true;
    }

    private static EmulatedAlpacaDeviceStatus Describe(Device device) => new(
        device.Type,
        device.Number,
        device.Name,
        device.UniqueId,
        device.Connected,
        device.Type == TelescopeType ? Parked(device) : null,
        device.Type == TelescopeType ? device.CanPark : null,
        device.Type == SafetyMonitorType ? device.Value : null,
        device.Behaviour,
        device.Requests);

    /// <summary>A member's parameter: its name, as the Alpaca API writes it, and its type.</summary>
    private sealed record Parameter(string Name, ParameterType Type);

    /// <summary>What a member answers: a value, nothing (a method), or an ASCOM error.</summary>
    private readonly record struct Outcome(bool HasValue, object? Value, int Error, string Message)
    {
        public static Outcome Done => new(false, null, 0, string.Empty);

        public static Outcome Of(object value) => new(true, value, 0, string.Empty);

        public static Outcome Failed(int error, string message) => new(false, null, error, message);
    }

    /// <summary>One device; its mutable fields are guarded by the server's gate.</summary>
    private sealed class Device(string type, int number, string name)
    {
        public string Type { get; } = type;

        public int Number { get; } = number;

        public string Name { get; } = name;

        /// <summary>Stable for the type and number, so a client's configuration survives the emulator's restart.</summary>
        public string UniqueId { get; } = string.Create(CultureInfo.InvariantCulture, $"hvo-hat-emulator-{type}-{number}");

        /// <summary>A telescope's AtPark (while it can park), a safety monitor's IsSafe.</summary>
        public bool Value { get; set; } = true;

        /// <summary>A telescope's CanPark and CanUnpark.</summary>
        public bool CanPark { get; set; } = true;

        public bool Connected { get; set; } = true;

        public EmulatedAlpacaBehaviour Behaviour { get; set; } = EmulatedAlpacaBehaviour.Answers;

        public long Requests { get; set; }
    }
}
