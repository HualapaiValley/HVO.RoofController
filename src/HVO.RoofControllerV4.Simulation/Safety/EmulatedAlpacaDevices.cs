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
/// <item>URLs are lower case (<c>/api/v1/{device_type}/{device_number}/{member}</c>); parameter names, in a GET's query
/// or a PUT's form body, are not case sensitive.</item>
/// <item>A request the device cannot interpret (an unknown device type, device number or member, a GET of a member only
/// PUT, a <c>ClientID</c> or <c>ClientTransactionID</c> that is not a number from 0 to 4294967295, a PUT
/// <c>Connected</c> that is not True or False) is HTTP 400 with a text message.</item>
/// <item>An answer is HTTP 200 with the JSON <c>Value</c> (a property read), <c>ClientTransactionID</c> (echoed, 0 when
/// not given), <c>ServerTransactionID</c> (from 1, one more each answer), <c>ErrorNumber</c> and <c>ErrorMessage</c>.
/// An error carries no <c>Value</c>, so a client cannot take it for a reading.</item>
/// <item>Only <c>Connected</c>, <c>Connecting</c>, <c>Name</c>, <c>Description</c>, <c>DriverInfo</c>,
/// <c>DriverVersion</c>, <c>InterfaceVersion</c> and <c>SupportedActions</c> answer while not connected; every other
/// member answers <c>ErrorNumber</c> 0x407 (NotConnectedException).</item>
/// <item>A mount that cannot park answers <c>CanPark</c> false, <c>AtPark</c> false, and 0x400 (NotImplementedException)
/// to <c>Park</c> and <c>Unpark</c>, as ITelescopeV4 says.</item>
/// </list>
/// The safety monitor serves every ISafetyMonitorV3 member. The mount serves the common members and its park members
/// only (<c>AtPark</c>, <c>CanPark</c>, <c>CanUnpark</c>, <c>Park</c>, <c>Unpark</c>); any other telescope member is
/// HTTP 400, so it is not a whole ITelescopeV4 (ASCOM's OmniSim is one). Nothing here moves or reaches hardware, and the
/// devices answer no Alpaca discovery. Safe to use from any thread.
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

    /// <summary>ASCOM NotConnectedException.</summary>
    public const int NotConnectedError = 0x407;

    /// <summary>ASCOM ActionNotImplementedException.</summary>
    public const int ActionNotImplementedError = 0x40C;

    /// <summary>The first driver-specific error number, which <see cref="EmulatedAlpacaBehaviour.DriverError"/> answers.</summary>
    public const int DriverError = 0x500;

    // The members every device has (IAscomDeviceV2), read with GET.
    private static readonly HashSet<string> CommonReads =
        ["connected", "connecting", "description", "driverinfo", "driverversion", "interfaceversion", "name", "supportedactions", "devicestate"];

    // The members every device has, called with PUT.
    private static readonly HashSet<string> CommonCalls = ["connected", "connect", "disconnect", "action", "commandblind", "commandbool", "commandstring"];

    // What may be read while not connected (ASCOM Platform 7, "Members available when not connected").
    private static readonly HashSet<string> ReadableWhileNotConnected =
        ["connected", "connecting", "description", "driverinfo", "driverversion", "interfaceversion", "name", "supportedactions"];

    private readonly object _gate = new();
    private readonly Device[] _telescopes;
    private readonly Device[] _monitors;
    private readonly TimeProvider _time;
    private uint _serverTransactions;

    /// <param name="telescopes">How many mounts to serve (0 to 8), numbered from 0, each parked and connected.</param>
    /// <param name="safetyMonitors">How many safety monitors to serve (0 to 8), numbered from 0, each safe and connected.</param>
    /// <param name="time">The clock for <c>DeviceState</c>'s TimeStamp; the system's by default.</param>
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

        var devices = deviceType switch
        {
            TelescopeType => _telescopes,
            SafetyMonitorType => _monitors,
            _ => null
        };
        if (devices is null)
        {
            // Alpaca URLs are lower case, and only these two types are served.
            return BadRequest($"The device type '{deviceType}' is not served here: only telescope and safetymonitor, in lower case.");
        }

        if (!uint.TryParse(deviceNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number >= devices.Length)
        {
            return BadRequest($"There is no {deviceType} device number {deviceNumber}.");
        }

        var values = parameters.ToList();
        if (!TryReadTransaction(values, "ClientID", out _, out var problem) || !TryReadTransaction(values, "ClientTransactionID", out var clientTransaction, out problem))
        {
            return BadRequest(problem!);
        }

        var known = put
            ? CommonCalls.Contains(member) || (deviceType == TelescopeType && member is "park" or "unpark")
            : CommonReads.Contains(member) || member == (deviceType == TelescopeType ? "atpark" : "issafe")
                || (deviceType == TelescopeType && member is "canpark" or "canunpark");
        if (!known)
        {
            return BadRequest($"The {deviceType} has no member '{member}' to {(put ? "PUT" : "GET")} here (members are lower case).");
        }

        bool? connect = null;
        if (put && member == "connected")
        {
            var text = Find(values, "Connected");
            if (!bool.TryParse(text, out var wanted))
            {
                return BadRequest("Connected must be True or False.");
            }

            connect = wanted;
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

            return put ? Call(device, member, connect, clientTransaction) : Read(device, member, clientTransaction);
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
        if (!TryReadTransaction(values, "ClientID", out _, out var problem) || !TryReadTransaction(values, "ClientTransactionID", out var clientTransaction, out problem))
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
    private EmulatedAlpacaAnswer Read(Device device, string member, uint clientTransaction)
    {
        if (!device.Connected && !ReadableWhileNotConnected.Contains(member))
        {
            return Error(clientTransaction, NotConnectedError, $"{device.Name} is not connected.");
        }

        var telescope = device.Type == TelescopeType;
        object value = member switch
        {
            "connected" => device.Connected,
            // Connect completes at once here, so a connection is never in progress.
            "connecting" => false,
            "name" => device.Name,
            "description" => $"{device.Name}, emulated by the HVO HAT emulator",
            "driverinfo" => "HVO HAT emulator: an emulated ASCOM Alpaca device for development and tests",
            "driverversion" => "1.0",
            // ITelescopeV4 and ISafetyMonitorV3: the interfaces of ASCOM Platform 7.
            "interfaceversion" => telescope ? 4 : 3,
            "supportedactions" => Array.Empty<string>(),
            "devicestate" => new[]
            {
                new Dictionary<string, object?> { ["Name"] = telescope ? "AtPark" : "IsSafe", ["Value"] = telescope ? device.Value && device.CanPark : device.Value },
                new Dictionary<string, object?> { ["Name"] = "TimeStamp", ["Value"] = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) }
            },
            "canpark" or "canunpark" => device.CanPark,
            // A mount that cannot park is never parked.
            "atpark" => device.Value && device.CanPark,
            _ => device.Value
        };
        return new EmulatedAlpacaAnswer(200, Response(clientTransaction, value, hasValue: true, 0, string.Empty), null);
    }

    // The caller holds the gate.
    private EmulatedAlpacaAnswer Call(Device device, string member, bool? connect, uint clientTransaction)
    {
        switch (member)
        {
            case "connected":
                device.Connected = connect!.Value;
                return Done(clientTransaction);
            case "connect":
                device.Connected = true;
                return Done(clientTransaction);
            case "disconnect":
                device.Connected = false;
                return Done(clientTransaction);
        }

        if (!device.Connected)
        {
            return Error(clientTransaction, NotConnectedError, $"{device.Name} is not connected.");
        }

        switch (member)
        {
            case "action":
                return Error(clientTransaction, ActionNotImplementedError, "The emulated device supports no actions.");
            case "park" or "unpark" when !device.CanPark:
                return Error(clientTransaction, NotImplementedError, $"{device.Name} cannot park.");
            case "park":
                device.Value = true;
                return Done(clientTransaction);
            case "unpark":
                device.Value = false;
                return Done(clientTransaction);
            default:
                // CommandBlind, CommandBool and CommandString, deprecated in Platform 7.
                return Error(clientTransaction, NotImplementedError, $"{member} is not implemented.");
        }
    }

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
    private EmulatedAlpacaAnswer Done(uint clientTransaction)
        => new(200, Response(clientTransaction, null, hasValue: false, 0, string.Empty), null);

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

    private static string? Find(IEnumerable<KeyValuePair<string, string?>> parameters, string name)
        => parameters.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    // Parameter names are matched whatever their case; a missing one is 0, the Alpaca API's "not supplied".
    private static bool TryReadTransaction(IEnumerable<KeyValuePair<string, string?>> parameters, string name, out uint value, out string? problem)
    {
        value = 0;
        problem = null;
        foreach (var (key, text) in parameters)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
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
        device.Type == TelescopeType ? device.Value && device.CanPark : null,
        device.Type == TelescopeType ? device.CanPark : null,
        device.Type == SafetyMonitorType ? device.Value : null,
        device.Behaviour,
        device.Requests);

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
