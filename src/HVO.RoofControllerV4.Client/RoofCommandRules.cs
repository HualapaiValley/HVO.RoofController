using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>What a client knows when it decides whether to offer Open, Close or Clear fault.</summary>
/// <param name="Status">The newest status, or null before the first.</param>
/// <param name="IsStale">True when the status may be out of date (the status hub is not delivering it).</param>
/// <param name="CanOperate">
/// True when the caller holds the Operator role; null while the client does not know the caller's role yet (the
/// controller decides).
/// </param>
/// <param name="CommandInFlight">True while a command sent from this client has not been answered.</param>
/// <param name="IsAdmin">
/// True when the caller holds the Admin role; null while the client does not know (the controller decides). Only a
/// controller that narrows the safety override to admins asks.
/// </param>
public readonly record struct RoofCommandState(RoofStatusResponse? Status, bool IsStale, bool? CanOperate, bool CommandInFlight, bool? IsAdmin = null);

/// <summary>
/// Why a client does not offer Open, Close or Clear fault: the same reasons, in the same order and words, in every
/// client. Null means the command may be offered. The controller still checks every request; these rules keep a client
/// from offering what the controller would refuse, or motion on a roof it cannot watch. Each reason reads after "Not
/// sent: " or "Open: "; a client that shows one on its own starts it with a capital. Stop is never blocked.
/// </summary>
public static class RoofCommandRules
{
    public const string MotionRoleNeeded = "the Operator role is needed to open or close the roof";

    public const string ClearFaultRoleNeeded = "the Operator role is needed to clear a fault";

    public const string NoStatus = "there is no status yet";

    public const string Stale = "the status is stale, so the roof cannot be watched";

    public const string Initializing = "the controller is initializing";

    public const string ShuttingDown = "the controller is shutting down";

    public const string CommandInFlight = "a command is on its way";

    public const string ClearFaultInProgress = "a clear-fault pulse is in progress";

    public const string FaultLatched = "a fault is latched: clear it first";

    public const string DriveFault = "the drive reports a fault: clear it first";

    public const string Moving = "the roof is moving: stop it first";

    public const string AlreadyOpen = "the roof is already open";

    public const string AlreadyClosed = "the roof is already closed";

    /// <summary>Open, while the controller's safety interlock blocks it (Block mode) and the conditions are not Safe.</summary>
    public const string SafetyInterlock = "the safety interlock blocks it: the safety conditions are not confirmed safe";

    /// <summary>
    /// Open, while the conditions are not Safe, the interlock asks for an override (Warn mode), the controller lets only
    /// admins give it, and the caller is not one.
    /// </summary>
    public const string SafetyOverrideNeedsAdmin = "the safety conditions are not confirmed safe, and only an admin may override them";

    public const string NoFault = "no fault is active";

    /// <summary>Why Open or Close got no answer, when the connection ended after the command was sent.</summary>
    public const string UnansweredConnection = "The connection to the controller ended before its answer arrived.";

    /// <summary>
    /// True when the roof has a fault a clear-fault pulse is for: a latched fault, the drive's fault input, or the Error
    /// status.
    /// </summary>
    public static bool HasFault(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsFaultLatched || status.IsDriveFaultActive == true || status.Status == RoofControllerStatus.Error;
    }

    /// <summary>
    /// Why no command may be offered at all: there is no status, it is stale, or the controller is not ready. Null when
    /// the controller can take commands.
    /// </summary>
    public static string? GetAvailabilityBlockReason(RoofStatusResponse? status, bool isStale)
        => status is null ? NoStatus
            : isStale ? Stale
            : !status.IsInitialized ? Initializing
            : status.IsShuttingDown ? ShuttingDown
            : null;

    /// <summary>Why Open (<see cref="RoofMotionDirection.Opening"/>) or Close is not offered; null when it is.</summary>
    public static string? GetMotionBlockReason(RoofMotionDirection direction, RoofCommandState state)
    {
        if (direction is not (RoofMotionDirection.Opening or RoofMotionDirection.Closing))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Only Opening or Closing is a motion to offer.");
        }

        if (state.CanOperate == false)
        {
            return MotionRoleNeeded;
        }

        if (GetAvailabilityBlockReason(state.Status, state.IsStale) is { } unavailable)
        {
            return unavailable;
        }

        var status = state.Status!;
        return state.CommandInFlight ? CommandInFlight
            : status.IsClearFaultInProgress ? ClearFaultInProgress
            : status.IsFaultLatched ? FaultLatched
            : status.IsDriveFaultActive == true ? DriveFault
            : status.IsMoving ? Moving
            : direction == RoofMotionDirection.Opening && status.Status is RoofControllerStatus.Open or RoofControllerStatus.Opening ? AlreadyOpen
            : direction == RoofMotionDirection.Closing && status.Status is RoofControllerStatus.Closed or RoofControllerStatus.Closing ? AlreadyClosed
            : direction == RoofMotionDirection.Opening && IsOpenBlockedBySafety(status) ? SafetyInterlock
            : direction == RoofMotionDirection.Opening && state.IsAdmin == false && NeedsSafetyOverride(status)
                && status.Safety!.OverrideRole == RoofControllerApiContract.AdminRole ? SafetyOverrideNeedsAdmin
            : null;
    }

    /// <summary>
    /// True when Open needs the safety override: the controller's interlock is in Warn mode and the conditions are not
    /// Safe (Unknown counts as unsafe). Each client then asks before it sends Open, and sends it with the override only
    /// once the person confirms (<see cref="RoofSafetyText"/>). Open with the interlock Off or Block never asks.
    /// </summary>
    public static bool NeedsSafetyOverride(RoofStatusResponse? status)
        => status?.Safety is { InterlockMode: RoofSafetyInterlockMode.Warn, State: not RoofSafetyState.Safe };

    /// <summary>
    /// True when the controller refused Open for want of the safety override (SafetyOverrideRequired): the conditions
    /// were not Safe when it checked, though this client may not have known. The client asks, as it does before Open.
    /// </summary>
    public static bool IsSafetyOverrideRequest(Exception error)
        => error is RoofApiException { Code: RoofControllerErrorCode.SafetyOverrideRequired };

    /// <summary>
    /// True when the controller's safety interlock refuses Open whatever the caller sends: Block mode, and conditions
    /// that are not Safe. Warn mode is not a block: Open is offered, and the controller asks for an override.
    /// </summary>
    public static bool IsOpenBlockedBySafety(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Safety is { InterlockMode: RoofSafetyInterlockMode.Block, State: not RoofSafetyState.Safe };
    }

    /// <summary>Why Clear fault is not offered; null when it is.</summary>
    public static string? GetClearFaultBlockReason(RoofCommandState state)
    {
        if (state.CanOperate == false)
        {
            return ClearFaultRoleNeeded;
        }

        if (GetAvailabilityBlockReason(state.Status, state.IsStale) is { } unavailable)
        {
            return unavailable;
        }

        var status = state.Status!;
        return state.CommandInFlight ? CommandInFlight
            : status.IsClearFaultInProgress ? ClearFaultInProgress
            : status.IsMoving ? Moving
            : HasFault(status) ? null
            : NoFault;
    }

    /// <summary>
    /// True when Open or Close failed with no answer from the controller after it may have been sent: a timeout, the
    /// connection dropping, an answer that could not be read, or a proxy's 502 or 504 (the controller answers Open and
    /// Close with neither). The controller may have acted on it, so the roof may be moving. A connection that could not
    /// be made sent nothing.
    /// </summary>
    public static bool MayHaveReachedController(Exception error) => error switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } => false,
        HttpRequestException or TimeoutException or TaskCanceledException { InnerException: TimeoutException } or RoofProtocolException => true,
        RoofApiException { StatusCode: System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout } => true,
        _ => false
    };

    /// <summary>
    /// Said after Open or Close got no answer (<see cref="MayHaveReachedController"/>): why, and that the roof may be
    /// moving; <paramref name="how"/> says how to stop it ("press F9"). A connection that failed after the command was
    /// sent is not "could not be reached".
    /// </summary>
    public static string DescribeUnanswered(Exception error, string verb, string how)
    {
        ArgumentNullException.ThrowIfNull(error);
        var why = error is HttpRequestException ? UnansweredConnection : RoofText.DescribeFailure(error);
        return $"{why} The {verb} may have reached the controller, and the roof may be moving. To stop it, {how}.";
    }

    /// <summary>
    /// One line for Open and Close together: "Open and Close: reason." when both are blocked, "Open: reason." or
    /// "Close: reason." when one is, and null when neither is.
    /// </summary>
    public static string? DescribeMotionBlocks(string? open, string? close)
        => open is not null && close is not null ? $"Open and Close: {(open == close ? open : $"{open}; {close}")}."
            : open is not null ? $"Open: {open}."
            : close is not null ? $"Close: {close}."
            : null;

    /// <summary><paramref name="reason"/> starting with a capital, for showing it on its own.</summary>
    public static string Capitalize(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return reason.Length == 0 ? reason : string.Concat(char.ToUpperInvariant(reason[0]).ToString(), reason.AsSpan(1));
    }
}
