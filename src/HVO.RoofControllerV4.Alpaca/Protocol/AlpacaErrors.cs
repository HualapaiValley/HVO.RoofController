using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Protocol;

/// <summary>
/// The Alpaca error numbers the server returns (<c>ErrorNumber</c>, with HTTP 200), a fixed table that docs/alpaca.md
/// lists. The ASCOM numbers (0x400 to 0x40C) mean what the Alpaca API says; 0x500 to 0x50F are this server's own, one
/// per cause, so a client can tell an unreachable controller from a refused key or unverified relays.
/// </summary>
public static class AlpacaErrors
{
    /// <summary>The member is not implemented: a roll-off roof has no azimuth, altitude, park or home.</summary>
    public const int NotImplemented = 0x400;

    /// <summary>A parameter's value is out of range.</summary>
    public const int InvalidValue = 0x401;

    /// <summary>The client has not connected (<c>Connected</c>, <c>Connect</c>). AbortSlew never needs it.</summary>
    public const int NotConnected = 0x407;

    /// <summary>
    /// Not allowed in the current state: Open and Close while commands are disabled, and the controller's refusals of a
    /// command (a latched fault, an active interlock, another operation in progress, the safety conditions).
    /// </summary>
    public const int InvalidOperation = 0x40B;

    /// <summary>The server has no such action (<c>SupportedActions</c> lists the ones it has).</summary>
    public const int ActionNotImplemented = 0x40C;

    /// <summary>
    /// The roof's status is not known: the controller cannot be reached, has sent no status yet, or its status hub has
    /// been silent for more than 3 s. A read never returns a status older than that.
    /// </summary>
    public const int ControllerUnavailable = 0x500;

    /// <summary>The controller has not finished starting (its <c>NotInitialized</c>).</summary>
    public const int ControllerNotInitialized = 0x501;

    /// <summary>The controller is shutting down (its <c>ShuttingDown</c>).</summary>
    public const int ControllerShuttingDown = 0x502;

    /// <summary>The controller cannot read its safety inputs (its <c>HardwareUnavailable</c>).</summary>
    public const int HardwareUnavailable = 0x503;

    /// <summary>
    /// The controller could not verify the relay register after the command (its <c>RelayStateUnverified</c>): confirm at
    /// the roof that the motor has stopped.
    /// </summary>
    public const int RelayStateUnverified = 0x504;

    /// <summary>The controller did not accept the Alpaca server's API key (revoked, or the wrong role).</summary>
    public const int ControllerRefusedKey = 0x505;

    /// <summary>Any other failure of the controller.</summary>
    public const int ControllerError = 0x50F;

    /// <summary>The Alpaca error number for a controller's refusal code.</summary>
    public static int For(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.NotInitialized => ControllerNotInitialized,
        RoofControllerErrorCode.ShuttingDown => ControllerShuttingDown,
        RoofControllerErrorCode.HardwareUnavailable => HardwareUnavailable,
        RoofControllerErrorCode.RelayStateUnverified => RelayStateUnverified,
        RoofControllerErrorCode.FaultLatched
            or RoofControllerErrorCode.InterlockActive
            or RoofControllerErrorCode.OperationInProgress
            or RoofControllerErrorCode.LeaseNotActive
            or RoofControllerErrorCode.SafetyInterlockActive
            or RoofControllerErrorCode.SafetyOverrideRequired => InvalidOperation,
        RoofControllerErrorCode.SignInFailed
            or RoofControllerErrorCode.SignInLockedOut
            or RoofControllerErrorCode.SignInBusy
            or RoofControllerErrorCode.KioskKeyRequired
            or RoofControllerErrorCode.CredentialNotAllowed
            or RoofControllerErrorCode.IdentityStoreUnavailable => ControllerRefusedKey,
        _ => ControllerError,
    };

    /// <summary>Every number in the table, for the tests and the docs.</summary>
    public static IReadOnlyList<int> All { get; } =
    [
        NotImplemented, InvalidValue, NotConnected, InvalidOperation, ActionNotImplemented,
        ControllerUnavailable, ControllerNotInitialized, ControllerShuttingDown, HardwareUnavailable,
        RelayStateUnverified, ControllerRefusedKey, ControllerError,
    ];
}

/// <summary>An Alpaca error, returned to the client as <c>ErrorNumber</c> and <c>ErrorMessage</c> with HTTP 200.</summary>
public sealed class AlpacaException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

/// <summary>A malformed request: answered with HTTP 400 and the message as plain text.</summary>
public sealed class AlpacaBadRequestException(string message) : Exception(message);
