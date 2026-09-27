using System;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Controllers.Camera;

/// <summary>
/// Caps the number of concurrently proxied camera streams (<see cref="BlueIrisOptions.MaxConcurrentStreams"/>, read
/// once at startup) so viewers cannot exhaust Pi sockets, threads or Blue Iris connections.
/// </summary>
public sealed class CameraStreamLimiter : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public CameraStreamLimiter(IOptions<BlueIrisOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        MaxStreams = Math.Clamp(options.Value.MaxConcurrentStreams, 1, 32);
        _slots = new SemaphoreSlim(MaxStreams, MaxStreams);
    }

    /// <summary>Effective cap.</summary>
    public int MaxStreams { get; }

    /// <summary>Streams currently open.</summary>
    public int ActiveStreams => MaxStreams - _slots.CurrentCount;

    /// <summary>Takes a slot without waiting. Dispose the returned lease to release it.</summary>
    public IDisposable? TryAcquire() => _slots.Wait(0) ? new Lease(_slots) : null;

    public void Dispose() => _slots.Dispose();

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _slots;

        public Lease(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
