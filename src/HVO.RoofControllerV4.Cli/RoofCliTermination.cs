using System.Runtime.InteropServices;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// The termination signals of <c>hvo-roof</c>: SIGINT (Ctrl+C), SIGTERM, and SIGHUP (the terminal closing, or an SSH
/// session dropping). The first one cancels <see cref="Token"/> instead of ending the process, so a command that has
/// the roof moving sends Stop and exits 130. The process still ends: <see cref="CommandGrace"/> after the first signal it
/// exits 130, unless a command holds it (<see cref="Hold"/>), and never later than <see cref="StopGrace"/>. A later
/// signal ends the process at once, unless a command holds it: it never cuts a Stop short.
/// </summary>
public sealed class RoofCliTermination : IDisposable
{
    /// <summary>How long a command has to end after the first signal when no Stop is on its way.</summary>
    public static readonly TimeSpan CommandGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest the process runs after the first signal: the Stop timeout (10 s) and a margin for the answer to be
    /// printed and the terminal restored.
    /// </summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    private static readonly PosixSignal[] Signals = [PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP];

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<int> _exit;
    private readonly TimeProvider _time;
    private readonly List<PosixSignalRegistration> _registrations = [];
    private ITimer? _watchdog;
    private DateTimeOffset _signalledAt;
    private int _holds;
    private bool _disposed;

    /// <param name="exit">Ends the process with an exit code; tests give their own.</param>
    /// <param name="time">The clock of the grace periods.</param>
    public RoofCliTermination(Action<int> exit, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(exit);
        _exit = exit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Cancelled by the first signal.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>The signals of this process, handled here from now until <see cref="Dispose"/>.</summary>
    public static RoofCliTermination Register()
    {
        var termination = new RoofCliTermination(Environment.Exit);
        foreach (var signal in Signals)
        {
            try
            {
                termination._registrations.Add(PosixSignalRegistration.Create(signal, termination.OnSignal));
            }
            catch (PlatformNotSupportedException)
            {
                // A platform without this signal: the runtime's default applies to it.
            }
        }

        return termination;
    }

    /// <summary>
    /// Marks a command whose Stop no signal may cut short, until the result is disposed: one that has sent Open or
    /// Close, or is sending Stop, or the terminal interface. A signal then does not end the process before
    /// <see cref="StopGrace"/>. The hold is taken before the first signal can arrive: closing a terminal sends SIGHUP
    /// twice, well under a millisecond apart.
    /// </summary>
    public IDisposable Hold()
    {
        lock (_gate)
        {
            _holds++;
        }

        return new Release(this);
    }

    /// <summary>True while a command holds the process (<see cref="Hold"/>).</summary>
    internal bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _holds > 0;
            }
        }
    }

    /// <summary>Handles one signal: the first cancels <see cref="Token"/>, a later one ends the process unless held.</summary>
    internal void OnSignal(PosixSignalContext context)
    {
        // The command decides when the process ends; the runtime's default would end it at once.
        context.Cancel = true;
        bool first;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            first = _watchdog is null;
            if (first)
            {
                _signalledAt = _time.GetUtcNow();
                _watchdog = _time.CreateTimer(_ => OnWatchdog(), null, CommandGrace, Timeout.InfiniteTimeSpan);
            }
            else if (_holds > 0)
            {
                return;
            }
        }

        if (first)
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The command had already ended.
            }
        }
        else
        {
            _exit((int)RoofExitCode.Interrupted);
        }
    }

    private void OnWatchdog()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var last = _signalledAt + StopGrace - _time.GetUtcNow();
            if (_holds > 0 && last > TimeSpan.Zero)
            {
                _watchdog!.Change(last, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        _exit((int)RoofExitCode.Interrupted);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watchdog?.Dispose();
        }

        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _cancel.Dispose();
    }

    private sealed class Release(RoofCliTermination termination) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                lock (termination._gate)
                {
                    termination._holds--;
                }
            }
        }
    }
}
