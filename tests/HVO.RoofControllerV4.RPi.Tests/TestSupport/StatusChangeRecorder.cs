using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Records every <see cref="IRoofControllerServiceV4.StatusChanged"/> snapshot in delivery order and can wait until
/// the dispatcher has delivered everything published so far.
/// </summary>
internal sealed class StatusChangeRecorder : IDisposable
{
    private readonly IRoofControllerServiceV4 _service;
    private readonly object _gate = new();
    private readonly List<RoofStatusResponse> _snapshots = new();

    public StatusChangeRecorder(IRoofControllerServiceV4 service)
    {
        _service = service;
        _service.StatusChanged += OnStatusChanged;
    }

    public IReadOnlyList<RoofStatusResponse> Snapshots
    {
        get { lock (_gate) { return _snapshots.ToArray(); } }
    }

    public long LastDeliveredVersion
    {
        get { lock (_gate) { return _snapshots.Count == 0 ? 0 : _snapshots[^1].StatusVersion; } }
    }

    /// <summary>Number of transitions into <paramref name="status"/> among the recorded snapshots.</summary>
    public int TransitionsInto(RoofControllerStatus status, RoofControllerStatus initial)
    {
        var count = 0;
        var previous = initial;
        foreach (var snapshot in Snapshots)
        {
            if (snapshot.Status == status && previous != status)
            {
                count++;
            }

            previous = snapshot.Status;
        }

        return count;
    }

    /// <summary>
    /// Waits until the dispatcher has delivered the service's current <see cref="RoofStatusResponse.StatusVersion"/>
    /// (or a later one). Returns false on timeout.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan? timeout = null)
    {
        var target = _service.GetCurrentStatusSnapshot().StatusVersion;
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (stopwatch.Elapsed < limit)
        {
            if (LastDeliveredVersion >= target)
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return LastDeliveredVersion >= target;
    }

    public void Dispose() => _service.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, RoofStatusChangedEventArgs args)
    {
        lock (_gate)
        {
            _snapshots.Add(args.Status);
        }
    }
}
