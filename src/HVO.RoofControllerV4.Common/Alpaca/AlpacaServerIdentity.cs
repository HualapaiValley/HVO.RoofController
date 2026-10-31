namespace HVO.RoofControllerV4.Common.Alpaca;

/// <summary>
/// How the roof's own ASCOM Alpaca server (<c>HVO.RoofControllerV4.Alpaca</c>, docs/alpaca.md) names itself: its
/// management API's <c>ServerName</c>, and the start of every one of its devices' <c>DriverInfo</c>. The controller's
/// Alpaca safety sources recognise the server's own devices by it, so the controller never reads its own weather back
/// (#220): a loop.
/// </summary>
public static class AlpacaServerIdentity
{
    /// <summary>The server's name; each of its devices' DriverInfo begins with it and a space.</summary>
    public const string ServerName = "HVO Roof Controller Alpaca server";

    /// <summary>True when <paramref name="driverInfo"/> is one of this server's devices'.</summary>
    public static bool IsOwnDriverInfo(string? driverInfo)
        => driverInfo is not null && driverInfo.StartsWith(ServerName + " ", StringComparison.Ordinal);
}
