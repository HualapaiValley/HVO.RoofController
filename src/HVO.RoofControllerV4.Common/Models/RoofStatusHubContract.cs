namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level constants of the live status hub (SignalR, JSON protocol). The hub only sends: every command, Stop
/// included, stays on the REST API.
/// </summary>
public static class RoofStatusHubContract
{
    /// <summary>Path of the hub on the controller.</summary>
    public const string Path = "/hubs/roof";

    /// <summary>Client method that receives each <see cref="RoofStatusHubMessage"/>.</summary>
    public const string StatusMethod = "Status";

    /// <summary>The only hub protocol the controller offers.</summary>
    public const string Protocol = "json";

    /// <summary>
    /// While nothing changes, the hub still sends the current snapshot at least this often, so a client can tell a
    /// quiet controller from a lost connection.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>Most connections open at once; a connection past the limit is closed with a reason.</summary>
    public const int MaxConnections = 32;

    /// <summary>
    /// Most connections open at once with one API key. A connection past a limit is closed with a reason and a close
    /// message that tells SignalR's automatic reconnect not to try again, so a client that wants status reconnects on its
    /// own, with a growing delay.
    /// </summary>
    public const int MaxConnectionsPerKey = 8;
}

/// <summary>
/// One message from the status hub: sent on connect, on every status change, and as a heartbeat while nothing changes.
/// </summary>
/// <param name="Status">The full status snapshot, as <c>GET RoofControl/Status</c> returns it.</param>
/// <param name="Sequence">
/// Goes up by one for every message the hub publishes, from 1 when the controller process starts, whether or not
/// anyone is connected, so a connection's first message can have any number. Each connection keeps only the newest
/// message it has not yet received, so a slow client can see gaps, but never a message older than one it already has.
/// </param>
/// <param name="ServerTimeUtc">The controller's clock when the message was published.</param>
/// <param name="InstanceId">
/// Random identifier of the publishing process. It changes when the controller restarts, and <paramref name="Sequence"/>
/// starts again, so a client compares sequence numbers only within one instance and can tell a restart from a lost
/// message.
/// </param>
public sealed record RoofStatusHubMessage(
    RoofStatusResponse Status,
    long Sequence,
    DateTimeOffset ServerTimeUtc,
    string InstanceId);
