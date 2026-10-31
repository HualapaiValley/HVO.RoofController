using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// The roof as Alpaca Dome 0 (IDomeV3), a roll-off roof: a shutter and nothing else. This phase reads only, apart from
/// AbortSlew, which stops the roof:
/// <list type="bullet">
/// <item>The shutter, Slewing, the capabilities (CanSetShutter true, every other false), DeviceState and the common
/// members read from the last status the controller published (<see cref="AlpacaRoof"/>).</item>
/// <item>AbortSlew is the controller's Stop, always allowed: also before Connect, after a restart forgot the client,
/// and with a fault latched.</item>
/// <item>OpenShutter and CloseShutter are refused (<see cref="AlpacaErrors.InvalidOperation"/>): commands are
/// disabled.</item>
/// <item>Azimuth, altitude, park and home are not implemented (<see cref="AlpacaErrors.NotImplemented"/>), nor are the
/// Command members.</item>
/// <item>The <c>status</c> action describes the roof (and why it reads as Error); no action moves it or clears a
/// fault.</item>
/// </list>
/// Every device member but AbortSlew needs the client to have connected first (<see cref="AlpacaErrors.NotConnected"/>);
/// the common members (Name, Description, the versions, SupportedActions, DeviceState) and Connected and Connecting do
/// not.
/// </summary>
public sealed class AlpacaDome
{
    /// <summary>The device type in URLs and the management API.</summary>
    public const string DeviceType = "dome";

    /// <summary>The Alpaca device type name.</summary>
    public const string DeviceTypeName = "Dome";

    /// <summary>IDomeV3.</summary>
    public const int InterfaceVersion = 3;

    /// <summary>The device's name.</summary>
    public const string DeviceName = "HVO roll-off roof";

    /// <summary>The device's description; ASCOM allows at most 64 characters.</summary>
    public const string DeviceDescription = "Hualapai Valley Observatory roll-off roof (read-only)";

    /// <summary>The one action: what the roof is doing, and why it reads as it does. It never moves the roof.</summary>
    public const string StatusAction = "status";

    /// <summary>The refusal of OpenShutter and CloseShutter in this phase.</summary>
    public const string CommandsDisabled =
        "Commands are disabled on this Alpaca server: it reads the roof and stops it (AbortSlew), but does not open or close it. Use the roof controller's own interfaces.";

    private static readonly IReadOnlyList<string> SupportedActions = [StatusAction];

    private readonly AlpacaRoof _roof;
    private readonly AlpacaConnections _connections;
    private readonly TimeProvider _time;
    private readonly FrozenDictionary<string, Member> _members;

    public AlpacaDome(AlpacaRoof roof, AlpacaConnections connections, TimeProvider time)
    {
        _roof = roof;
        _connections = connections;
        _time = time;
        var version = RoofProductVersion.Of(typeof(AlpacaDome).Assembly);
        DriverInfo = $"HVO Roof Controller Alpaca server {RoofProductVersion.Describe(version)}: the roll-off roof through the roof controller's API";
        DriverVersion = MajorMinor(version);
        _members = BuildMembers();
    }

    /// <summary>The server's full version and what it is.</summary>
    public string DriverInfo { get; }

    /// <summary>The server's version as major.minor, as ASCOM asks.</summary>
    public string DriverVersion { get; }

    /// <summary>Every member's name, as it appears in URLs.</summary>
    public IEnumerable<string> MemberNames => _members.Keys;

    /// <summary>
    /// Answers the member <paramref name="name"/> (lower case, as in the URL) for <paramref name="request"/>. Null: no such
    /// member, or not with that HTTP method (a malformed request).
    /// </summary>
    /// <exception cref="AlpacaBadRequestException">A parameter is missing or malformed.</exception>
    public async Task<AlpacaResult?> InvokeAsync(string name, AlpacaRequest request, CancellationToken cancellationToken)
    {
        if (!_members.TryGetValue(name, out var member))
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

        // A property's error still carries a Value, as the Alpaca API's responses define it: its type's default, or for
        // ShutterStatus Error, so a client that overlooks ErrorNumber still does not see a roof that is fine.
        return result.ErrorNumber != 0 && !request.IsPut ? result with { Value = member.Fallback } : result;
    }

    private FrozenDictionary<string, Member> BuildMembers()
    {
        var members = new Dictionary<string, Member>(StringComparer.Ordinal)
        {
            // The common members (IDeviceV2), readable before Connect.
            ["connected"] = new(Sync(call => AlpacaResult.Of(_connections.IsConnected(call.Client))), Sync(SetConnected), False),
            ["connect"] = Method(call => _connections.Connect(call.Client)),
            ["disconnect"] = Method(call => _connections.Disconnect(call.Client)),
            ["connecting"] = Property(_ => AlpacaResult.Of(false), False),
            ["description"] = Property(_ => AlpacaResult.Of(DeviceDescription), EmptyText),
            ["driverinfo"] = Property(_ => AlpacaResult.Of(DriverInfo), EmptyText),
            ["driverversion"] = Property(_ => AlpacaResult.Of(DriverVersion), EmptyText),
            ["interfaceversion"] = Property(_ => AlpacaResult.Of(InterfaceVersion), Zero),
            ["name"] = Property(_ => AlpacaResult.Of(DeviceName), EmptyText),
            ["supportedactions"] = Property(_ => AlpacaResult.Of(SupportedActions), EmptyList),
            ["devicestate"] = Property(_ => DeviceState(), EmptyList),
            ["action"] = new(null, Sync(Action)),
            ["commandblind"] = new(null, Sync(Command)),
            ["commandbool"] = new(null, Sync(Command)),
            ["commandstring"] = new(null, Sync(Command)),

            // The roof.
            ["shutterstatus"] = Connected(_ => AlpacaResult.Of((int)DomeShutter.StateOf(_roof.GetCurrent())), ShutterError),
            ["slewing"] = Connected(_ => AlpacaResult.Of(DomeShutter.IsSlewing(_roof.GetCurrent())), False),
            ["cansetshutter"] = Connected(_ => AlpacaResult.Of(true), False),
            ["canfindhome"] = Connected(_ => AlpacaResult.Of(false), False),
            ["canpark"] = Connected(_ => AlpacaResult.Of(false), False),
            ["cansetaltitude"] = Connected(_ => AlpacaResult.Of(false), False),
            ["cansetazimuth"] = Connected(_ => AlpacaResult.Of(false), False),
            ["cansetpark"] = Connected(_ => AlpacaResult.Of(false), False),
            ["canslave"] = Connected(_ => AlpacaResult.Of(false), False),
            ["cansyncazimuth"] = Connected(_ => AlpacaResult.Of(false), False),
            ["slaved"] = new(Sync(call => RequireConnected(call, () => AlpacaResult.Of(false))), Sync(SetSlaved), False),
            ["altitude"] = NotImplementedProperty("Altitude", "a roll-off roof has no altitude", Zero),
            ["athome"] = NotImplementedProperty("AtHome", "a roll-off roof has no home position (CanFindHome is false)", False),
            ["atpark"] = NotImplementedProperty("AtPark", "a roll-off roof has no park position (CanPark is false)", False),
            ["azimuth"] = NotImplementedProperty("Azimuth", "a roll-off roof has no azimuth", Zero),

            // Stop: always allowed.
            ["abortslew"] = new(null, AbortSlewAsync),
            ["openshutter"] = new(null, Sync(call => RequireConnected(call, () => AlpacaResult.Error(AlpacaErrors.InvalidOperation, CommandsDisabled)))),
            ["closeshutter"] = new(null, Sync(call => RequireConnected(call, () => AlpacaResult.Error(AlpacaErrors.InvalidOperation, CommandsDisabled)))),
            ["findhome"] = NotImplementedMethod("FindHome", "a roll-off roof has no home position"),
            ["park"] = NotImplementedMethod("Park", "a roll-off roof has no park position"),
            ["setpark"] = NotImplementedMethod("SetPark", "a roll-off roof has no park position"),
            ["slewtoaltitude"] = NotImplementedMethod("SlewToAltitude", "a roll-off roof has no altitude", call => call.Request.RequireDouble("Altitude")),
            ["slewtoazimuth"] = NotImplementedMethod("SlewToAzimuth", "a roll-off roof has no azimuth", call => call.Request.RequireDouble("Azimuth")),
            ["synctoazimuth"] = NotImplementedMethod("SyncToAzimuth", "a roll-off roof has no azimuth", call => call.Request.RequireDouble("Azimuth")),
        };
        return members.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private AlpacaResult SetConnected(Call call)
    {
        if (call.Request.RequireBoolean("Connected"))
        {
            _connections.Connect(call.Client);
        }
        else
        {
            _connections.Disconnect(call.Client);
        }

        return AlpacaResult.Done;
    }

    private AlpacaResult SetSlaved(Call call)
    {
        var slaved = call.Request.RequireBoolean("Slaved");
        return RequireConnected(call, () => slaved
            ? AlpacaResult.Error(AlpacaErrors.NotImplemented, "Slaved cannot be set true: a roll-off roof is not slaved to a telescope (CanSlave is false).")
            : AlpacaResult.Done);
    }

    // The request's cancellation is not passed on: a client that hangs up never cancels a Stop.
    private async Task<AlpacaResult> AbortSlewAsync(Call call, CancellationToken cancellationToken)
    {
        await _roof.StopRoofAsync(call.Request.RemoteAddress, call.Request.ClientId, call.Request.ClientTransactionId).ConfigureAwait(false);
        return AlpacaResult.Done;
    }

    private AlpacaResult Action(Call call)
    {
        var action = call.Request.Require("Action");
        _ = call.Request.Require("Parameters");
        return RequireConnected(call, () => string.Equals(action.Trim(), StatusAction, StringComparison.OrdinalIgnoreCase)
            ? AlpacaResult.Of(DescribeStatus(_roof.GetCurrent()))
            : AlpacaResult.Error(AlpacaErrors.ActionNotImplemented, $"This server has no action '{Shorten(action)}'. SupportedActions lists the ones it has."));
    }

    private static AlpacaResult Command(Call call)
    {
        _ = call.Request.Require("Command");
        _ = call.Request.RequireBoolean("Raw");
        return AlpacaResult.Error(AlpacaErrors.NotImplemented, "The Command members are not implemented.");
    }

    private AlpacaResult DeviceState()
    {
        var status = _roof.GetCurrent();
        var shutter = (int)DomeShutter.StateOf(status);
        var slewing = DomeShutter.IsSlewing(status);
        var timestamp = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        return new AlpacaResult(writer =>
        {
            writer.WriteStartArray();
            WriteState(writer, "ShutterStatus", w => w.WriteNumberValue(shutter));
            WriteState(writer, "Slewing", w => w.WriteBooleanValue(slewing));
            WriteState(writer, "TimeStamp", w => w.WriteStringValue(timestamp));
            writer.WriteEndArray();
        }, 0, string.Empty);
    }

    private static void WriteState(Utf8JsonWriter writer, string name, Action<Utf8JsonWriter> value)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", name);
        writer.WritePropertyName("Value");
        value(writer);
        writer.WriteEndObject();
    }

    /// <summary>The <c>status</c> action's text: the shutter as Alpaca reads it, and the roof as the controller describes it.</summary>
    public static string DescribeStatus(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var text = new StringBuilder();
        text.Append("Shutter: ").Append(DomeShutter.StateOf(status)).Append(DomeShutter.IsSlewing(status) ? " (moving)" : string.Empty).Append(". ");
        text.Append("Roof: ").Append(RoofStatusText.DescribeRoof(status)).Append(". ");
        text.Append("Last stop: ").Append(RoofStatusText.DescribeLastStop(status)).Append(". ");
        text.Append("Fault: ").Append(RoofStatusText.DescribeFault(status)).Append('.');
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            text.Append(" Last error: ").Append(status.LastError.Trim()).Append('.');
        }

        if (status.Safety is { } safety)
        {
            text.Append(" Safety: ").Append(RoofStatusText.DescribeSafety(safety)).Append('.');
        }

        return text.ToString();
    }

    private AlpacaResult RequireConnected(Call call, Func<AlpacaResult> member)
        => _connections.IsConnected(call.Client)
            ? member()
            : AlpacaResult.Error(AlpacaErrors.NotConnected, "Not connected: set Connected to true (or call Connect) first.");

    private Member Connected(Func<Call, AlpacaResult> get, AlpacaValueWriter fallback)
        => new(Sync(call => RequireConnected(call, () => get(call))), null, fallback);

    private static Member Property(Func<Call, AlpacaResult> get, AlpacaValueWriter fallback) => new(Sync(get), null, fallback);

    private static Member Method(Action<Call> put) => new(null, Sync(call =>
    {
        put(call);
        return AlpacaResult.Done;
    }));

    private static Member NotImplementedProperty(string name, string why, AlpacaValueWriter fallback)
        => Property(_ => AlpacaResult.Error(AlpacaErrors.NotImplemented, $"{name} is not implemented: {why}."), fallback);

    private static Member NotImplementedMethod(string name, string why, Action<Call>? parameters = null) => new(null, Sync(call =>
    {
        parameters?.Invoke(call);
        return AlpacaResult.Error(AlpacaErrors.NotImplemented, $"{name} is not implemented: {why}.");
    }));

    private static Handler Sync(Func<Call, AlpacaResult> member) => (call, _) => Task.FromResult(member(call));

    private static string MajorMinor(string version)
    {
        var parts = RoofProductVersion.WithoutCommit(version).Split('-')[0].Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    private static string Shorten(string text) => text.Length <= 32 ? text : text[..32] + "…";

    private sealed record Call(AlpacaRequest Request, AlpacaClientKey Client);

    private delegate Task<AlpacaResult> Handler(Call call, CancellationToken cancellationToken);

    /// <summary>A member: its GET (a property), its PUT (a method or a property's setter), and its Value on a GET's error.</summary>
    private sealed record Member(Handler? Get, Handler? Put, AlpacaValueWriter? Fallback = null);

    private static readonly AlpacaValueWriter False = writer => writer.WriteBooleanValue(false);
    private static readonly AlpacaValueWriter Zero = writer => writer.WriteNumberValue(0);
    private static readonly AlpacaValueWriter EmptyText = writer => writer.WriteStringValue(string.Empty);
    private static readonly AlpacaValueWriter ShutterError = writer => writer.WriteNumberValue((int)DomeShutterState.Error);
    private static readonly AlpacaValueWriter EmptyList = writer =>
    {
        writer.WriteStartArray();
        writer.WriteEndArray();
    };
}
