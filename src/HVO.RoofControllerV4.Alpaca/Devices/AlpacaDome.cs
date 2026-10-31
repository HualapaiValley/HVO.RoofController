using System.Text;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Alpaca.Security;
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
/// <item>CloseShutter (#220, phase 3) carries the Alpaca client's own key (<c>X-HVO-Api-Key</c>, or HTTP Basic with the
/// key as both parts) to the controller, which alone decides it: the server refuses on its own only for a client not
/// connected, a missing or malformed credential, or an AbortSlew that overtook it before its ticket. It asks the
/// controller for a ticket, then sends the Close with it; it never retries.</item>
/// <item>OpenShutter is refused (<see cref="AlpacaErrors.InvalidOperation"/>) before anything reaches the controller,
/// whatever any setting or grant says: Alpaca Open is not available in this version.</item>
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
    public const string DeviceDescription = "Hualapai Valley Observatory roll-off roof";

    /// <summary>The member that stops the roof, as it appears in URLs.</summary>
    public const string AbortSlewMember = "abortslew";

    /// <summary>The refusal of OpenShutter in this phase, whatever any setting or grant says.</summary>
    public const string OpenNotAvailable =
        "OpenShutter is not available on this Alpaca server: it reads the roof, closes it when the controller allows, and stops it (AbortSlew). Open the roof from the roof controller's own interfaces.";

    /// <summary>The refusal of a Close whose credential cannot be used, or that the controller did not accept.</summary>
    public const string NotAuthorised =
        "Not authorised: CloseShutter needs the External Alpaca account's key, in the X-HVO-Api-Key header or as HTTP Basic with the key as both the user name and the password.";

    /// <summary>The refusal of a Close an AbortSlew overtook before its ticket.</summary>
    public const string Aborted = "Aborted: an AbortSlew came after this CloseShutter and before it reached the controller; nothing was sent. Send it again if it is still wanted.";

    private readonly AlpacaRoof _roof;
    private readonly AlpacaAuthFailures _failures;
    private readonly ILogger<AlpacaDome> _logger;

    public AlpacaDome(AlpacaRoof roof, AlpacaConnections connections, TimeProvider time, AlpacaAuthFailures failures, ILogger<AlpacaDome> logger)
        : base(connections, time, "the roll-off roof through the roof controller's API")
    {
        _roof = roof;
        _failures = failures;
        _logger = logger;
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
        members["openshutter"] = new(null, Sync(call => RequireConnected(call, () => AlpacaResult.Error(AlpacaErrors.InvalidOperation, OpenNotAvailable))));
        members["closeshutter"] = new(null, CloseShutterAsync);
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

    /// <summary>
    /// CloseShutter (#220): local refusals first (not connected, no usable credential), then the controller's ticket, then
    /// the Close with it. The request's cancellation is not passed on: a client that hangs up does not cancel a command
    /// already on its way, and nothing is retried.
    /// </summary>
    private async Task<AlpacaResult> CloseShutterAsync(Call call, CancellationToken cancellationToken)
    {
        if (!Connections.IsConnected(call.Client))
        {
            return AlpacaResult.Error(AlpacaErrors.NotConnected, "Not connected: set Connected to true (or call Connect) first.");
        }

        var request = call.Request;
        var aborts = _roof.AbortCount;
        var credential = request.Credential;
        if (!credential.IsUsable)
        {
            _logger.LogInformation("CloseShutter from {RemoteAddress} (ClientID {ClientId}) refused: {Credential}", request.RemoteAddress, request.ClientId, credential);
            _ = _roof.ReportRefusalAsync(new RoofAlpacaEvent(nameof(RoofAlpacaCommandKind.Close), "NotAuthorised", request.RemoteAddress.ToString(), request.ClientId, request.ClientTransactionId));
            await _failures.DelayAsync(request.RemoteAddress).ConfigureAwait(false);
            return AlpacaResult.Error(AlpacaErrors.InvalidOperation, NotAuthorised);
        }

        var forwarding = new RoofAlpacaForwarding(request.ClientId, request.ClientTransactionId, request.RemoteAddress.ToString(), credential.Method!);
        RoofAlpacaTicket ticket;
        try
        {
            ticket = await _roof.RequestTicketAsync(RoofAlpacaCommandKind.Close, credential.Key!, forwarding).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or OperationCanceledException or RoofProtocolException)
        {
            return await RefusalAsync(request, "ticket", ex).ConfigureAwait(false);
        }

        _failures.Accepted(request.RemoteAddress);
        if (_roof.AbortCount != aborts)
        {
            _logger.LogInformation("CloseShutter from {RemoteAddress} (ClientID {ClientId}) refused: an AbortSlew came first", request.RemoteAddress, request.ClientId);
            return AlpacaResult.Error(AlpacaErrors.InvalidOperation, Aborted);
        }

        try
        {
            await _roof.CloseAsync(ticket, credential.Key!, forwarding).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RoofApiException or HttpRequestException or TimeoutException or OperationCanceledException or RoofProtocolException)
        {
            return await RefusalAsync(request, "close", ex).ConfigureAwait(false);
        }

        _logger.LogInformation("CloseShutter from {RemoteAddress} (ClientID {ClientId}, ClientTransactionID {ClientTransactionId}) accepted by the controller",
            request.RemoteAddress, request.ClientId, request.ClientTransactionId);
        return AlpacaResult.Done;
    }

    /// <summary>The Alpaca answer to a ticket or Close the controller refused or did not answer; never retried.</summary>
    private async Task<AlpacaResult> RefusalAsync(AlpacaRequest request, string step, Exception error)
    {
        if (error is RoofApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } && (error as RoofApiException)?.AlpacaRefusal is not "OpenNotAvailable")
        {
            // The controller raised the alert itself; the answer is slowed down for this address.
            _logger.LogInformation("CloseShutter from {RemoteAddress} (ClientID {ClientId}) not authorised by the controller at the {Step}", request.RemoteAddress, request.ClientId, step);
            await _failures.DelayAsync(request.RemoteAddress).ConfigureAwait(false);
            return AlpacaResult.Error(AlpacaErrors.InvalidOperation, NotAuthorised);
        }

        if (error is RoofApiException { Code: { } code } refusal)
        {
            var message = refusal.Detail is { Length: > 0 } detail ? detail : refusal.Message;
            _logger.LogInformation("CloseShutter from {RemoteAddress} (ClientID {ClientId}) refused by the controller at the {Step}: {Code} {Rule}",
                request.RemoteAddress, request.ClientId, step, code, refusal.AlpacaRefusal);
            return AlpacaResult.Error(AlpacaErrors.For(code), message);
        }

        _logger.LogWarning("CloseShutter from {RemoteAddress} (ClientID {ClientId}): the controller did not answer the {Step} ({Reason}); nothing is retried",
            request.RemoteAddress, request.ClientId, step, RoofText.DescribeFailure(error));
        return AlpacaResult.Error(AlpacaRoof.ErrorNumberFor(error),
            $"The roof controller did not answer the Close ({RoofText.DescribeFailure(error)}). Nothing is retried: check the roof's state, and send CloseShutter again if it is still wanted.");
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
