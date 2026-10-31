using System.Text;
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
/// not (<see cref="AlpacaDevice"/>).
/// </summary>
public sealed class AlpacaDome : AlpacaDevice
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

    /// <summary>The member that stops the roof, as it appears in URLs.</summary>
    public const string AbortSlewMember = "abortslew";

    /// <summary>The refusal of OpenShutter and CloseShutter in this phase.</summary>
    public const string CommandsDisabled =
        "Commands are disabled on this Alpaca server: it reads the roof and stops it (AbortSlew), but does not open or close it. Use the roof controller's own interfaces.";

    private readonly AlpacaRoof _roof;

    public AlpacaDome(AlpacaRoof roof, AlpacaConnections connections, TimeProvider time)
        : base(connections, time, "the roll-off roof through the roof controller's API")
    {
        _roof = roof;
    }

    public override string UrlType => DeviceType;

    public override string TypeName => DeviceTypeName;

    public override string DisplayName => DeviceName;

    public override string DescriptionText => DeviceDescription;

    public override int AlpacaInterfaceVersion => InterfaceVersion;

    protected override void AddMembers(IDictionary<string, Member> members)
    {
        // The roof.
        members["shutterstatus"] = Connected(_ => AlpacaResult.Of((int)DomeShutter.StateOf(_roof.GetCurrent())), ShutterError);
        members["slewing"] = Connected(_ => AlpacaResult.Of(DomeShutter.IsSlewing(_roof.GetCurrent())), False);
        members["cansetshutter"] = Connected(_ => AlpacaResult.Of(true), False);
        members["canfindhome"] = Connected(_ => AlpacaResult.Of(false), False);
        members["canpark"] = Connected(_ => AlpacaResult.Of(false), False);
        members["cansetaltitude"] = Connected(_ => AlpacaResult.Of(false), False);
        members["cansetazimuth"] = Connected(_ => AlpacaResult.Of(false), False);
        members["cansetpark"] = Connected(_ => AlpacaResult.Of(false), False);
        members["canslave"] = Connected(_ => AlpacaResult.Of(false), False);
        members["cansyncazimuth"] = Connected(_ => AlpacaResult.Of(false), False);
        members["slaved"] = new(Sync(call => RequireConnected(call, () => AlpacaResult.Of(false))), Sync(SetSlaved), False);
        members["altitude"] = NotImplementedProperty("Altitude", "a roll-off roof has no altitude", Zero);
        members["athome"] = NotImplementedProperty("AtHome", "a roll-off roof has no home position (CanFindHome is false)", False);
        members["atpark"] = NotImplementedProperty("AtPark", "a roll-off roof has no park position (CanPark is false)", False);
        members["azimuth"] = NotImplementedProperty("Azimuth", "a roll-off roof has no azimuth", Zero);

        // Stop: always allowed.
        members[AbortSlewMember] = new(null, AbortSlewAsync);
        members["openshutter"] = new(null, Sync(call => RequireConnected(call, () => AlpacaResult.Error(AlpacaErrors.InvalidOperation, CommandsDisabled))));
        members["closeshutter"] = new(null, Sync(call => RequireConnected(call, () => AlpacaResult.Error(AlpacaErrors.InvalidOperation, CommandsDisabled))));
        members["findhome"] = NotImplementedMethod("FindHome", "a roll-off roof has no home position");
        members["park"] = NotImplementedMethod("Park", "a roll-off roof has no park position");
        members["setpark"] = NotImplementedMethod("SetPark", "a roll-off roof has no park position");
        members["slewtoaltitude"] = NotImplementedMethod("SlewToAltitude", "a roll-off roof has no altitude", call => call.Request.RequireDouble("Altitude"));
        members["slewtoazimuth"] = NotImplementedMethod("SlewToAzimuth", "a roll-off roof has no azimuth", call => call.Request.RequireDouble("Azimuth"));
        members["synctoazimuth"] = NotImplementedMethod("SyncToAzimuth", "a roll-off roof has no azimuth", call => call.Request.RequireDouble("Azimuth"));
    }

    protected override AlpacaResult DeviceState()
    {
        var status = _roof.GetCurrent();
        var shutter = (int)DomeShutter.StateOf(status);
        var slewing = DomeShutter.IsSlewing(status);
        return State(("ShutterStatus", w => w.WriteNumberValue(shutter)), ("Slewing", w => w.WriteBooleanValue(slewing)));
    }

    protected override string DescribeNow() => DescribeStatus(_roof.GetCurrent());

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

    private static readonly AlpacaValueWriter ShutterError = writer => writer.WriteNumberValue((int)DomeShutterState.Error);
}
