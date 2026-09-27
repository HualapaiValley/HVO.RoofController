using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// Tracks whether the browser connection of one Blazor circuit is up (registered per circuit as a
/// <see cref="CircuitHandler"/>). The console renews the operator lease from a server-side timer, so it uses this to stop
/// renewing as soon as SignalR reports the connection down. Otherwise the lease would stay alive for the whole circuit
/// retention period after the browser has gone.
/// </summary>
public sealed class ConsoleCircuitMonitor : CircuitHandler
{
    private volatile bool _isConnected = true;

    /// <summary>False from the moment the connection goes down until it comes back.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Raised when the connection goes down (false) or comes back (true).</summary>
    public event Action<bool>? ConnectionChanged;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        SetConnected(false);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        SetConnected(true);
        return Task.CompletedTask;
    }

    internal void SetConnected(bool connected)
    {
        _isConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }
}
