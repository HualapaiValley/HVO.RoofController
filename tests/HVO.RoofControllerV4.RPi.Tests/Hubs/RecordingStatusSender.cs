using System.Collections.Concurrent;
using System.Diagnostics;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Hubs;

namespace HVO.RoofControllerV4.RPi.Tests.Hubs;

/// <summary>How a <see cref="RecordingStatusSender"/> treats sends to one connection.</summary>
internal enum SendBehavior
{
    /// <summary>Delivered at once.</summary>
    Deliver,

    /// <summary>
    /// The send never completes until it is cancelled: a client that stopped reading, whose transport buffer is full.
    /// </summary>
    NeverComplete,

    /// <summary>The send blocks its calling thread until <see cref="RecordingStatusSender.Release"/>.</summary>
    BlockThread,

    /// <summary>The first send waits (asynchronously) for <see cref="RecordingStatusSender.Release"/>; later ones are delivered at once.</summary>
    HoldFirst
}

/// <summary>Records what the broadcaster sends to each connection, with a scripted behavior per connection.</summary>
internal sealed class RecordingStatusSender : IRoofStatusSender, IDisposable
{
    private readonly ConcurrentDictionary<string, SendBehavior> _behaviors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoofStatusHubMessage>> _delivered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cancelled = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _released = new(false);
    private readonly TaskCompletionSource _releasedAsync = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Script(string connectionId, SendBehavior behavior) => _behaviors[connectionId] = behavior;

    public void Release()
    {
        _released.Set();
        _releasedAsync.TrySetResult();
    }

    public IReadOnlyList<RoofStatusHubMessage> Delivered(string connectionId)
        => _delivered.TryGetValue(connectionId, out var queue) ? queue.ToArray() : [];

    public int Attempts(string connectionId) => _attempts.GetValueOrDefault(connectionId);

    public int Cancelled(string connectionId) => _cancelled.GetValueOrDefault(connectionId);

    public async Task SendAsync(string connectionId, RoofStatusHubMessage message, CancellationToken cancellationToken)
    {
        var attempt = _attempts.AddOrUpdate(connectionId, 1, (_, n) => n + 1);
        try
        {
            switch (_behaviors.GetValueOrDefault(connectionId))
            {
                case SendBehavior.NeverComplete:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case SendBehavior.BlockThread:
                    _released.Wait(cancellationToken);
                    break;
                case SendBehavior.HoldFirst when attempt == 1:
                    await _releasedAsync.Task.WaitAsync(cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _cancelled.AddOrUpdate(connectionId, 1, (_, n) => n + 1);
            throw;
        }

        _delivered.GetOrAdd(connectionId, _ => new ConcurrentQueue<RoofStatusHubMessage>()).Enqueue(message);
    }

    /// <summary>Waits until what was delivered to <paramref name="connectionId"/> satisfies <paramref name="condition"/>.</summary>
    public async Task<IReadOnlyList<RoofStatusHubMessage>> WaitForAsync(
        string connectionId,
        Func<IReadOnlyList<RoofStatusHubMessage>, bool> condition,
        string what,
        TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var delivered = Delivered(connectionId);
            if (condition(delivered))
            {
                return delivered;
            }

            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new AssertFailedException(
                    $"Timed out waiting for {what} on '{connectionId}'. Delivered: {string.Join(", ", delivered.Select(Describe))}.");
            }

            await Task.Delay(10);
        }
    }

    public async Task WaitForAttemptsAsync(string connectionId, int attempts, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (Attempts(connectionId) < attempts)
        {
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new AssertFailedException($"Timed out waiting for {attempts} send(s) to '{connectionId}'; saw {Attempts(connectionId)}.");
            }

            await Task.Delay(10);
        }
    }

    public static string Describe(RoofStatusHubMessage message) => $"#{message.Sequence} v{message.Status.StatusVersion} {message.Status.Status}";

    /// <summary>Frees any thread still blocked in a send (the event is left undisposed: that thread may still be waking).</summary>
    public void Dispose() => Release();
}
