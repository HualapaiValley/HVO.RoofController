using System.Collections.Frozen;
using System.Text.Json;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Alpaca;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// What every device of this server shares: its members by name (lower case, as in URLs) with their GET and PUT, the
/// common members (IDeviceV2: Connected, Connect, Disconnect, Connecting, Name, Description, DriverInfo, DriverVersion,
/// InterfaceVersion, SupportedActions, DeviceState, Action and the Command members), the client's connection
/// (<see cref="AlpacaConnections"/>, one per device), and a property's error carrying its type's default as its Value.
/// A device adds its own members (<see cref="AddMembers"/>). The common members are readable before connecting; Action
/// needs the client connected, and its one action, <see cref="StatusAction"/>, only describes.
/// </summary>
public abstract class AlpacaDevice
{
    /// <summary>The one action every device has: what it reports, and why. It never moves the roof.</summary>
    public const string StatusAction = "status";

    private static readonly IReadOnlyList<string> Actions = [StatusAction];

    private readonly Lazy<FrozenDictionary<string, Member>> _members;

    /// <param name="connections">This device's own connections.</param>
    /// <param name="time">The clock for DeviceState's TimeStamp.</param>
    /// <param name="purpose">What DriverInfo says the device is, after the server's name and version.</param>
    protected AlpacaDevice(AlpacaConnections connections, TimeProvider time, string purpose)
    {
        Connections = connections;
        Time = time;
        var version = RoofProductVersion.Of(typeof(AlpacaDevice).Assembly);
        // The server's name first: the controller's safety sources recognise this server's own devices by it.
        DriverInfo = $"{AlpacaServerIdentity.ServerName} {RoofProductVersion.Describe(version)}: {purpose}";
        DriverVersion = MajorMinor(version);
        _members = new Lazy<FrozenDictionary<string, Member>>(BuildMembers);
    }

    /// <summary>The device type in URLs (lower case).</summary>
    public abstract string UrlType { get; }

    /// <summary>The Alpaca device type name, as the management API lists it.</summary>
    public abstract string TypeName { get; }

    /// <summary>The device's Name.</summary>
    public abstract string DisplayName { get; }

    /// <summary>The device's Description; ASCOM allows at most 64 characters.</summary>
    public abstract string DescriptionText { get; }

    /// <summary>The ASCOM interface's version (InterfaceVersion).</summary>
    public abstract int AlpacaInterfaceVersion { get; }

    /// <summary>The server's full version and what the device is: it begins with <see cref="AlpacaServerIdentity.ServerName"/>.</summary>
    public string DriverInfo { get; }

    /// <summary>The server's version as major.minor, as ASCOM asks.</summary>
    public string DriverVersion { get; }

    /// <summary>Which clients have connected to this device.</summary>
    public AlpacaConnections Connections { get; }

    /// <summary>Every member's name, as it appears in URLs.</summary>
    public IEnumerable<string> MemberNames => _members.Value.Keys;

    protected TimeProvider Time { get; }

    /// <summary>
    /// Answers the member <paramref name="name"/> (lower case, as in the URL) for <paramref name="request"/>. Null: no such
    /// member, or not with that HTTP method (a malformed request).
    /// </summary>
    /// <exception cref="AlpacaBadRequestException">A parameter is missing or malformed.</exception>
    public async Task<AlpacaResult?> InvokeAsync(string name, AlpacaRequest request, CancellationToken cancellationToken)
    {
        if (!_members.Value.TryGetValue(name, out var member))
        {
            return null;
        }

        var handler = request.IsPut ? member.Put : member.Get;
        if (handler is null)
        {
            return null;
        }

        AlpacaResult result;
        try
        {
            result = await handler(new Call(request, new AlpacaClientKey(request.RemoteAddress, request.ClientId)), cancellationToken).ConfigureAwait(false);
        }
        catch (AlpacaException ex)
        {
            result = AlpacaResult.Error(ex);
        }

        // A property's error still carries a Value, as the Alpaca API's responses define it: its type's default, or a
        // device's own fallback (the Dome's ShutterStatus Error), so a client that overlooks ErrorNumber is not misled.
        return result.ErrorNumber != 0 && !request.IsPut ? result with { Value = member.Fallback } : result;
    }

    /// <summary>Adds the device's own members to the common ones.</summary>
    protected abstract void AddMembers(IDictionary<string, Member> members);

    /// <summary>The device's DeviceState: its operational properties and a TimeStamp.</summary>
    protected abstract AlpacaResult DeviceState();

    /// <summary>The <see cref="StatusAction"/>'s text.</summary>
    protected abstract string DescribeNow();

    /// <summary>A member that needs the client connected (<see cref="AlpacaErrors.NotConnected"/> otherwise).</summary>
    protected Member Connected(Func<Call, AlpacaResult> get, AlpacaValueWriter fallback)
        => new(Sync(call => RequireConnected(call, () => get(call))), null, fallback);

    /// <summary><paramref name="member"/>'s answer when the client is connected; <see cref="AlpacaErrors.NotConnected"/> otherwise.</summary>
    protected AlpacaResult RequireConnected(Call call, Func<AlpacaResult> member)
        => Connections.IsConnected(call.Client)
            ? member()
            : AlpacaResult.Error(AlpacaErrors.NotConnected, "Not connected: set Connected to true (or call Connect) first.");

    /// <summary>A DeviceState answer: each name with its value, then TimeStamp (UTC).</summary>
    protected AlpacaResult State(params (string Name, Action<Utf8JsonWriter> Value)[] values)
    {
        var timestamp = Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        return new AlpacaResult(writer =>
        {
            writer.WriteStartArray();
            foreach (var (name, value) in values)
            {
                WriteState(writer, name, value);
            }

            WriteState(writer, "TimeStamp", w => w.WriteStringValue(timestamp));
            writer.WriteEndArray();
        }, 0, string.Empty);
    }

    protected static Member Property(Func<Call, AlpacaResult> get, AlpacaValueWriter fallback) => new(Sync(get), null, fallback);

    protected static Member NotImplementedProperty(string name, string why, AlpacaValueWriter fallback)
        => Property(_ => AlpacaResult.Error(AlpacaErrors.NotImplemented, $"{name} is not implemented: {why}."), fallback);

    protected static Member NotImplementedMethod(string name, string why, Action<Call>? parameters = null) => new(null, Sync(call =>
    {
        parameters?.Invoke(call);
        return AlpacaResult.Error(AlpacaErrors.NotImplemented, $"{name} is not implemented: {why}.");
    }));

    protected static Handler Sync(Func<Call, AlpacaResult> member) => (call, _) => Task.FromResult(member(call));

    protected static string Shorten(string text) => text.Length <= 32 ? text : text[..32] + "…";

    private FrozenDictionary<string, Member> BuildMembers()
    {
        var members = new Dictionary<string, Member>(StringComparer.Ordinal)
        {
            // The common members (IDeviceV2), readable before Connect.
            ["connected"] = new(Sync(call => AlpacaResult.Of(Connections.IsConnected(call.Client))), Sync(SetConnected), False),
            ["connect"] = Method(call => Connections.Connect(call.Client)),
            ["disconnect"] = Method(call => Connections.Disconnect(call.Client)),
            ["connecting"] = Property(_ => AlpacaResult.Of(false), False),
            ["description"] = Property(_ => AlpacaResult.Of(DescriptionText), EmptyText),
            ["driverinfo"] = Property(_ => AlpacaResult.Of(DriverInfo), EmptyText),
            ["driverversion"] = Property(_ => AlpacaResult.Of(DriverVersion), EmptyText),
            ["interfaceversion"] = Property(_ => AlpacaResult.Of(AlpacaInterfaceVersion), Zero),
            ["name"] = Property(_ => AlpacaResult.Of(DisplayName), EmptyText),
            ["supportedactions"] = Property(_ => AlpacaResult.Of(Actions), EmptyList),
            ["devicestate"] = Property(_ => DeviceState(), EmptyList),
            ["action"] = new(null, Sync(Action)),
            ["commandblind"] = new(null, Sync(Command)),
            ["commandbool"] = new(null, Sync(Command)),
            ["commandstring"] = new(null, Sync(Command)),
        };
        AddMembers(members);
        return members.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private AlpacaResult SetConnected(Call call)
    {
        if (call.Request.RequireBoolean("Connected"))
        {
            Connections.Connect(call.Client);
        }
        else
        {
            Connections.Disconnect(call.Client);
        }

        return AlpacaResult.Done;
    }

    private AlpacaResult Action(Call call)
    {
        var action = call.Request.Require("Action");
        _ = call.Request.Require("Parameters");
        return RequireConnected(call, () => string.Equals(action.Trim(), StatusAction, StringComparison.OrdinalIgnoreCase)
            ? AlpacaResult.Of(DescribeNow())
            : AlpacaResult.Error(AlpacaErrors.ActionNotImplemented, $"This server has no action '{Shorten(action)}'. SupportedActions lists the ones it has."));
    }

    private static AlpacaResult Command(Call call)
    {
        _ = call.Request.Require("Command");
        _ = call.Request.RequireBoolean("Raw");
        return AlpacaResult.Error(AlpacaErrors.NotImplemented, "The Command members are not implemented.");
    }

    private static Member Method(Action<Call> put) => new(null, Sync(call =>
    {
        put(call);
        return AlpacaResult.Done;
    }));

    private static void WriteState(Utf8JsonWriter writer, string name, Action<Utf8JsonWriter> value)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", name);
        writer.WritePropertyName("Value");
        value(writer);
        writer.WriteEndObject();
    }

    private static string MajorMinor(string version)
    {
        var parts = RoofProductVersion.WithoutCommit(version).Split('-')[0].Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    /// <summary>One request to a member: the request, and the client it comes from.</summary>
    protected sealed record Call(AlpacaRequest Request, AlpacaClientKey Client);

    /// <summary>A member's GET or PUT.</summary>
    protected delegate Task<AlpacaResult> Handler(Call call, CancellationToken cancellationToken);

    /// <summary>A member: its GET (a property), its PUT (a method or a property's setter), and its Value on a GET's error.</summary>
    protected sealed record Member(Handler? Get, Handler? Put, AlpacaValueWriter? Fallback = null);

    protected static readonly AlpacaValueWriter False = writer => writer.WriteBooleanValue(false);
    protected static readonly AlpacaValueWriter Zero = writer => writer.WriteNumberValue(0);
    protected static readonly AlpacaValueWriter EmptyText = writer => writer.WriteStringValue(string.Empty);
    protected static readonly AlpacaValueWriter EmptyList = writer =>
    {
        writer.WriteStartArray();
        writer.WriteEndArray();
    };
}
