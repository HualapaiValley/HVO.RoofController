using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.SignalR;

namespace HVO.RoofControllerV4.RPi.Hubs;

/// <summary>
/// Live status for the clients at <see cref="RoofStatusHubContract.Path"/>: each connection receives
/// <see cref="RoofStatusHubMessage"/>s on <see cref="RoofStatusHubContract.StatusMethod"/> from
/// <see cref="RoofStatusBroadcaster"/>. The hub has no methods a client can call; every command stays on REST.
/// </summary>
/// <remarks>
/// Mapped in <c>Program</c> with the Viewer policy on the API key scheme only, as <c>api/*</c> is: the console cookie is
/// not accepted, so a browser page on another site cannot open a connection with it.
/// </remarks>
public sealed class RoofStatusHub(RoofStatusBroadcaster broadcaster) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!broadcaster.TryRegister(Context.ConnectionId, Context.User, Context.Abort))
        {
            // SignalR closes the connection and tells the client not to reconnect.
            throw new HubException("The controller is not accepting more status connections.");
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
