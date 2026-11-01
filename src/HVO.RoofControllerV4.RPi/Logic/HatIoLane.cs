using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace HVO.RoofControllerV4.RPi.Logic;

/// <summary>
/// The HAT's register accesses, one at a time on a thread of their own, each bounded (#220, §17 AA5, §19 M1). A caller
/// waits at most the limit it gives. Past it the access counts as failed (<see cref="HatIoOverrunException"/>): the caller
/// goes on (and lets the controller's lock go), and every later access fails at once, without reaching the HAT, until the
/// overrun one has returned. A synchronous I2C transfer cannot be abandoned, so this is what keeps a hung bus from holding
/// the controller's lock: the overrun access may still complete late, so the relays stay unverified until it has, and
/// then <see cref="Recovered"/> is raised so the controller writes them off and verifies them.
/// </summary>
internal sealed class HatIoLane
{
    // One lane per HAT object, shared by every controller that drives it (the HAT is one bus device).
    private static readonly ConditionalWeakTable<object, HatIoLane> Lanes = new();

    // An idle worker thread ends after this long; the next access starts another.
    private static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private Action? _work;
    private bool _workerRunning;
    private volatile bool _overrun;

    /// <summary>The lane for <paramref name="hat"/>.</summary>
    public static HatIoLane For(object hat) => Lanes.GetValue(hat, static _ => new HatIoLane());

    /// <summary>True while an access that ran past its limit has not returned: every access fails at once meanwhile.</summary>
    public bool IsOverrun => _overrun;

    /// <summary>Raised on the lane's thread when an access that ran past its limit has returned. Handlers must not block.</summary>
    public event Action? Recovered;

    /// <summary>Runs <paramref name="access"/> on the lane, waiting at most <paramref name="limit"/> for it.</summary>
    /// <exception cref="HatIoOverrunException">The access did not finish within the limit, or an earlier one is still out.</exception>
    public T Run<T>(Func<T> access, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (_overrun)
        {
            throw new HatIoOverrunException("An earlier HAT access is still running past its time limit; nothing more is sent to the HAT until it returns.");
        }

        var started = Stopwatch.GetTimestamp();
        if (!_turn.Wait(limit))
        {
            throw new HatIoOverrunException($"The HAT stayed busy for {limit.TotalMilliseconds:0} ms; this access was not sent.");
        }

        // 0 running, 1 completed while the caller waits, 2 abandoned by the caller at its limit.
        var state = 0;
        var result = default(T)!;
        ExceptionDispatchInfo? error = null;
        var completed = new ManualResetEventSlim(false);
        Post(() =>
        {
            try
            {
                result = access();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }

            if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
            {
                completed.Set();
                return;
            }

            // The caller gave up on it: it has returned now, late. Later accesses no longer fail at once, but the lane's turn
            // is still held: the recovery runs first (the controller raises its relay fence and queues its all-off, whose
            // accesses wait for the turn rather than fail), and only then is the lane free again, so no access reaches
            // the HAT before it (PR #230 rereviews, Opus F17 and F22).
            _overrun = false;
            try
            {
                Recovered?.Invoke();
            }
            catch
            {
                // A handler's failure never stops the lane.
            }

            _turn.Release();
        });

        var remaining = limit - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero || !completed.Wait(remaining))
        {
            _overrun = true;
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                throw new HatIoOverrunException($"A HAT access ran past its {limit.TotalMilliseconds:0} ms limit; its outcome is unknown until it returns.");
            }

            // It completed just as the limit passed.
            _overrun = false;
        }

        // The event is never disposed: the worker may still be setting it as the limit passes.
        _turn.Release();
        error?.Throw();
        return result;
    }

    private void Post(Action work)
    {
        lock (_gate)
        {
            _work = work;
            if (_workerRunning)
            {
                Monitor.Pulse(_gate);
                return;
            }

            _workerRunning = true;

            // The thread carries no caller's execution context: one would keep that caller's host alive while it idles.
            using (ExecutionContext.SuppressFlow())
            {
                new Thread(Work) { IsBackground = true, Name = "HAT I/O" }.Start();
            }
        }
    }

    private void Work()
    {
        while (true)
        {
            Action? work;
            lock (_gate)
            {
                while (_work is null)
                {
                    if (!Monitor.Wait(_gate, IdleExit) && _work is null)
                    {
                        _workerRunning = false;
                        return;
                    }
                }

                work = _work;
                _work = null;
            }

            work();

            // While it idles, the thread keeps nothing the finished access reached (a stopped controller, say).
            work = null;
        }
    }
}

/// <summary>A HAT access that ran past its time limit, or one refused because an earlier one is still out (#220, AA5).</summary>
public sealed class HatIoOverrunException(string message) : TimeoutException(message);
