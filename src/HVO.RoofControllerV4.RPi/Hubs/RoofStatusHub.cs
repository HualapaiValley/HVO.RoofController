using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.SignalR;

namespace HVO.RoofControllerV4.RPi.Hubs;

/// <summary>
/// Live status for the clients at <see cref="RoofStatusHubContract.Path"/>: each connection receives
/// <see cref="RoofStatusHubMessage"/>s on <see cref="RoofStatusHubContract.StatusMethod"/> from
/// <see cref="RoofStatusBroadcaster"/>. The hub has no methods a client can call; every command stays on REST.
/// </summary>
/// <remarks>
/// Mapped in <c>Program</c> with the Viewer policy on the API scheme (an API key or a session), as <c>api/*</c> is: no
/// cookie is accepted, so a browser page on another site cannot open a connection.
/// </remarks>
public sealed class RoofStatusHub(RoofStatusBroadcaster broadcaster) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!broadcaster.TryRegister(Context.ConnectionId, Context.User, Context.Abort, out var refusal))
        {
            // SignalR closes the connection with this reason and tells the client not to reconnect, so the client's
            // automatic reconnect stops: a client retries on its own, with a growing delay (RoofStatusHubContract).
            // SignalR also logs each such close at Error (category HubConnectionHandler); that category is not
            // filtered, as it carries real transport errors too. The broadcaster's own Warning is rate-limited.
            throw new HubException(refusal);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
