using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HVO.Core.Results;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Logic;

/// <summary>
/// Roof controller for the Sequent Microsystems four-relay / four-input HAT.
/// </summary>
/// <remarks>
/// <para>
/// Safety model (see the repository safety documentation for the full contract):
/// </para>
/// <list type="bullet">
/// <item><description>Commanded motion (<see cref="RoofMotionDirection"/>) is tracked separately from the displayed
/// <see cref="RoofControllerStatus"/>. <see cref="IsMoving"/> is true exactly while motion is commanded, and all
/// supervision keys off commanded motion.</description></item>
/// <item><description>Every relay transition is followed by a read-back of the HAT relay register. A read-back proves the
/// register accepted the write; it does not prove the physical relay contacts moved.</description></item>
/// <item><description>Safety faults (watchdog, drive fault, relay verification, input read failure, contradictory limits,
/// start-limit reassertion, drive not running) latch. While latched, Open/Close are refused; Stop is always allowed.
/// Only <see cref="ClearFault"/> resets the latch, and only when inputs read healthy with IN3 inactive.</description></item>
/// <item><description>All state lives behind a single lock (<see cref="_syncLock"/>). No code awaits or blocks on another
/// task while holding it. Status notifications are delivered in order on a background dispatcher.</description></item>
/// </list>
/// </remarks>
public partial class RoofControllerServiceV4 : IRoofControllerServiceV4, IAsyncDisposable, IDisposable
{
    /// <summary>Idle supervision cadence. Keeps the input cache fresh and detects idle faults when polling is off.</summary>
    internal static readonly TimeSpan IdleSupervisionInterval = TimeSpan.FromSeconds(1);

    /// <summary>Supervision cadence while moving when periodic verification is disabled (edge events are primary).</summary>
    internal static readonly TimeSpan MovingFallbackSupervisionInterval = TimeSpan.FromSeconds(1);

    /// <summary>Shortest delay between supervision cycles, so a passed deadline cannot spin the loop.</summary>
    internal static readonly TimeSpan MinimumSupervisionDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How long the drive run input (IN4) may stay low after it confirmed a start before motion stops with
    /// <see cref="RoofControllerStopReason.DriveNotRunning"/> (at-speed interlock only). At the destination limit the NC
    /// contact removes the run command a few milliseconds before the NO monitoring contact closes (the ME-8108 transfer
    /// time), so IN4 can drop before IN1/IN2 report the limit; this window covers that transfer with a wide margin, and the
    /// inputs are re-read before the stop so an undelivered limit edge still wins.
    /// </summary>
    internal static readonly TimeSpan RunLossConfirmationDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// SM-I-010 LED mode register value written at initialization: LED1-LED3 manual (driven by the controller with the
    /// logical open limit, closed limit and drive fault states), LED4 automatic (the HAT shows IN4, the drive run output).
    /// </summary>
    internal const byte IndicatorLedModes = 0x07;

    /// <summary>Minimum age after which the cached safety inputs, or the last relay register read, are considered stale.</summary>
    internal static readonly TimeSpan MinimumReadStaleness = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive failed relay register reads after which supervision stops motion (latching
    /// <see cref="RoofControllerStopReason.RelayVerificationFailed"/>) or, when idle, re-runs the all-off sequence.
    /// Fixed rather than configurable: the register read-back is the only evidence of the relay state, so a single
    /// failure is tolerated and a second one is not.
    /// </summary>
    internal const int MaxConsecutiveRelayReadFailures = 2;

    /// <summary>
    /// Interval between all-off attempts after a shutdown stop could not verify the relay register. Supervision has
    /// stopped by then, so this retry is the only path that re-drives the relays off before the process exits.
    /// </summary>
    internal static readonly TimeSpan ShutdownStopRetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the shutdown stop retry keeps trying before it gives up (it also ends at disposal).</summary>
    internal static readonly TimeSpan ShutdownStopRetryWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long dispose waits for the controller lock (held by a call blocked in HAT I/O, for example), and then for the
    /// supervision loop, the shutdown stop retry and the status dispatcher to finish.
    /// </summary>
    internal static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(2);

    // One relay transaction lock per HAT instance, so relay sequences from different service instances sharing a HAT
    // (tests, misconfiguration) never interleave. Lock order: _syncLock -> HAT transaction lock -> HAT internal lock.
    private static readonly ConditionalWeakTable<FourRelayFourInputHat, object> HatTransactionLocks = new();

    [ThreadStatic]
    private static bool t_onStatusDispatcherThread;

    /// <summary>The single lock protecting all controller state.</summary>
    protected readonly object _syncLock = new();

    private readonly ILogger<RoofControllerServiceV4> _logger;
    private readonly FourRelayFourInputHat _hat;
    private readonly TimeProvider _timeProvider;

    // Anchor of the monotonic controller clock (see Now): the wall clock and the monotonic timestamp at construction.
    private readonly DateTimeOffset _clockOriginUtc;
    private readonly long _clockOriginTimestamp;

    private readonly object _hatTransactionLock;
    private readonly string _controllerName;
    private readonly string _controllerInstanceId = Guid.NewGuid().ToString();
    private readonly RoofControllerOptionsV4Validator _optionsValidator = new();

    // Configuration (immutable copy; replaced atomically by UpdateConfiguration).
    private RoofControllerOptionsV4 _options;
    private long _configurationVersion = 1;

    // Lifecycle.
    private bool _initialized;
    private volatile bool _shuttingDown;
    private volatile bool _disposed;

    // Set once when disposal timed out on the lock; runs the all-off stop when the blocked call returns.
    private Task? _deferredDisposalTask;

    // Bounded all-off retry after an unverified shutdown stop (see ShutdownStopRetryInterval).
    private Task<bool>? _shutdownStopRetryTask;
    private CancellationTokenSource? _shutdownStopRetryCts;

    // Commanded motion and supervision deadlines.
    private RoofMotionDirection _commandedMotion = RoofMotionDirection.None;
    private RoofMotionDirection _lastMotionDirection = RoofMotionDirection.None;
    private long _motionGeneration;
    private DateTimeOffset? _motionStartUtc;
    private DateTimeOffset? _watchdogDeadlineUtc;
    private ITimer? _watchdogTimer;
    private DateTimeOffset? _leaseDeadlineUtc;
    private DateTimeOffset? _atSpeedDeadlineUtc;
    private bool _atSpeedConfirmed;

    // When IN4 dropped after confirming the start (at-speed interlock only); null while it reports running.
    private DateTimeOffset? _runLostUtc;

    // IN4 reported stopped when the move started, so its first HIGH is this move's drive start and not the last move's
    // run-down (a start while IN4 runs is refused with the interlock). Motion timing measures the drive only then.
    private bool _driveStoppedAtStart;

    // Departure supervision: the limit opposite to the direction of travel, and whether the move started on it (motion
    // timing tells a full travel from a partial one by it).
    private bool _startedAtLimit;
    private bool _departureReleaseVerified;
    private DateTimeOffset? _releaseObservedUtc;
    private DateTimeOffset? _departureDeadlineUtc;

    // Relay register state (read-back of the HAT register, not contact state).
    private RoofRelayRegisterState _relayRegisterState = RoofRelayRegisterState.Unknown;
    private int? _relayRegisterMask;
    private DateTimeOffset? _lastSuccessfulRelayReadUtc;
    private int _consecutiveRelayReadFailures;

    // Raw electrical input levels from the last successful read or edge event (null until known).
    private bool? _rawIn1;
    private bool? _rawIn2;
    private bool? _rawIn3;
    private bool? _rawIn4;
    private DateTimeOffset? _lastSuccessfulInputReadUtc;
    private int _consecutiveInputReadFailures;

    // Fault latch and diagnostics.
    private bool _faultLatched;
    private RoofControllerStopReason? _latchedFaultReason;
    private string? _lastError;
    private RoofControllerStopReason _lastStopReason = RoofControllerStopReason.None;
    private DateTimeOffset? _lastMotionStopUtc;
    private bool _driveRunningAfterStopReported;

    // The last stop while the drive run input (IN4) still reported running, until IN4 drops (its drive stop delay is then
    // recorded) or the next move starts.
    private (DateTimeOffset At, RoofMotionDirection Direction, RoofControllerStopReason Reason)? _driveStopDelayPending;

    // Clear-fault pulse.
    private readonly SemaphoreSlim _clearFaultGate = new(1, 1);
    private bool _clearFaultInProgress;
    private CancellationTokenSource? _clearFaultCts;

    // Displayed status and publication.
    private RoofControllerStatus _status = RoofControllerStatus.NotInitialized;
    private DateTimeOffset? _lastTransitionUtc;
    private long _statusVersion;
    private RoofStatusResponse? _lastPublishedKey;
    private readonly Channel<RoofStatusResponse> _statusChannel;
    private readonly Task _statusDispatcherTask;

    // Background supervision.
    private CancellationTokenSource? _supervisionCts;
    private CancellationTokenSource? _supervisionWakeCts;
    private long _supervisionWakeCount;
    private Task? _supervisionTask;

    // HAT input event subscriptions.
    private EventHandler<bool>? _hatIn1Handler;
    private EventHandler<bool>? _hatIn2Handler;
    private EventHandler<bool>? _hatIn3Handler;
    private EventHandler<bool>? _hatIn4Handler;

    // Indicator LED cache and telemetry edge tracking.
    private byte? _lastIndicatorLedMask;
    private byte? _lastAttemptedIndicatorLedMask;
    private bool _ledModesApplied;
    private bool? _telemetryOpenLimit;
    private bool? _telemetryClosedLimit;
    private bool? _telemetryFault;

    private long _stopSequenceCount;

    // What answers the HAT register accesses; fixed for the controller's lifetime.
    private readonly RoofHatMode _hatMode;

    public RoofControllerServiceV4(
        ILogger<RoofControllerServiceV4> logger,
        IOptions<RoofControllerOptionsV4> roofControllerOptions,
        FourRelayFourInputHat fourRelayFourInputHat,
        IOptions<RoofControllerHostOptionsV4>? hostOptions = null,
        TimeProvider? timeProvider = null,
        RoofHatConnection? hatConnection = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(roofControllerOptions);
        ArgumentNullException.ThrowIfNull(fourRelayFourInputHat);

        _logger = logger;
        _hat = fourRelayFourInputHat;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clockOriginUtc = _timeProvider.GetUtcNow();
        _clockOriginTimestamp = _timeProvider.GetTimestamp();
        _options = (roofControllerOptions.Value ?? new RoofControllerOptionsV4()) with { };
        _hatTransactionLock = HatTransactionLocks.GetValue(fourRelayFourInputHat, static _ => new object());
        _hatMode = RoofHatConnection.ModeFor(hatConnection?.IsEmulated == true, _hat.IsHardwareBacked);

        var configuredName = hostOptions?.Value?.ControllerName;
        _controllerName = string.IsNullOrWhiteSpace(configuredName) ? new RoofControllerHostOptionsV4().ControllerName : configuredName;

        _statusChannel = Channel.CreateBounded<RoofStatusResponse>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _statusDispatcherTask = Task.Run(DispatchStatusChangesAsync);

        if (_hatMode == RoofHatMode.Emulated)
        {
            _logger.LogWarning("RoofControllerServiceV4 using the HAT emulator at {Endpoint} for the relay HAT, not the physical HAT. Controller={ControllerName} Instance={InstanceId}",
                hatConnection!.EmulatorEndpoint, _controllerName, _controllerInstanceId);
        }
        else
        {
            _logger.LogInformation("RoofControllerServiceV4 using {HardwareMode} mode for relay HAT. Controller={ControllerName} Instance={InstanceId}",
                _hat.ConnectionMode, _controllerName, _controllerInstanceId);
        }
    }

    /// <inheritdoc />
    public event EventHandler<RoofStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Test seam: when false, <see cref="Initialize"/> does not start the background supervision loop and tests drive
    /// supervision deterministically through <see cref="RunSupervisionCycle"/>. Set before initialization.
    /// </summary>
    internal bool EnableBackgroundSupervision { get; set; } = true;

    /// <summary>Number of stop sequences (all-relays-off transitions requested by a stop) executed so far.</summary>
    internal long StopSequenceCount => Interlocked.Read(ref _stopSequenceCount);

    /// <summary>Generation of the current (or most recent) motion. Incremented whenever motion starts or stops.</summary>
    internal long CurrentMotionGeneration
    {
        get { lock (_syncLock) { return _motionGeneration; } }
    }

    /// <summary>
    /// Controller time: monotonic, anchored to the wall clock at construction. Every deadline, staleness check and internal
    /// timestamp uses it, so a wall-clock step (an NTP correction, or a Pi without an RTC battery setting its clock after
    /// boot) neither keeps an expired lease alive nor expires one early. Published timestamps are converted to wall-clock
    /// UTC with <see cref="WallClockOffset"/>.
    /// </summary>
    private DateTimeOffset Now => _clockOriginUtc + _timeProvider.GetElapsedTime(_clockOriginTimestamp);

    /// <summary>
    /// Current wall clock minus controller time (<see cref="Now"/>): zero until the wall clock is stepped or drifts. Adding
    /// it to a controller-time timestamp gives the wall-clock UTC with the same age.
    /// </summary>
    private TimeSpan WallClockOffset => _timeProvider.GetUtcNow() - Now;

    public bool IsInitialized
    {
        get { lock (_syncLock) { return _initialized; } }
    }

    public RoofControllerStatus Status
    {
        get { lock (_syncLock) { return _status; } }
    }

    /// <summary>True exactly while motion is commanded (<see cref="RoofMotionDirection.Opening"/> or <see cref="RoofMotionDirection.Closing"/>).</summary>
    public bool IsMoving
    {
        get { lock (_syncLock) { return _commandedMotion != RoofMotionDirection.None; } }
    }

    public RoofControllerStopReason LastStopReason
    {
        get { lock (_syncLock) { return _lastStopReason; } }
    }

    public DateTimeOffset? LastTransitionUtc
    {
        get { lock (_syncLock) { return _lastTransitionUtc + WallClockOffset; } }
    }

    public bool IsWatchdogActive
    {
        get { lock (_syncLock) { return IsWatchdogActive_NoLock; } }
    }

    public double? WatchdogSecondsRemaining
    {
        get { lock (_syncLock) { return WatchdogSecondsRemaining_NoLock(Now); } }
    }

    public bool IsAtSpeed
    {
        get { lock (_syncLock) { return _rawIn4 == true; } }
    }

    public bool IsServiceDisposed => _disposed;

    public bool IsShuttingDown => _shuttingDown;

    public bool IsUsingPhysicalHardware => _hat.IsHardwareBacked;

    /// <summary>What answers the HAT register accesses: the physical HAT, the HAT emulator or the register simulation.</summary>
    public RoofHatMode HatMode => _hatMode;

    public bool IsIgnoringPhysicalLimitSwitches
    {
        get { lock (_syncLock) { return _options.IgnorePhysicalLimitSwitches; } }
    }

    public RoofStatusResponse GetCurrentStatusSnapshot()
    {
        lock (_syncLock)
        {
            var now = Now;
            PublishIfChanged_NoLock(now);
            return BuildSnapshot_NoLock(now, forKey: false);
        }
    }

    public RoofControllerOptionsV4 GetConfigurationSnapshot()
    {
        lock (_syncLock)
        {
            return _options with { };
        }
    }

    public RoofControllerConfigurationState GetConfigurationState()
    {
        lock (_syncLock)
        {
            return new RoofControllerConfigurationState(_options with { }, _configurationVersion);
        }
    }

    public void RefreshStatus(bool forceHardwareRead = false)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            var now = Now;
            if (forceHardwareRead || !_options.EnableDigitalInputPolling || !InputsHealthy_NoLock(now))
            {
                ReadInputs_NoLock();
            }

            Evaluate_NoLock(Now);
            FinishMutation_NoLock();
        }
    }

    /// <summary>
    /// Test helper: reads the inputs from the HAT, evaluates the safety rules and republishes status.
    /// </summary>
    internal void ForceStatusRefresh(bool forceHardwareRead = false)
    {
        _ = forceHardwareRead;
        RefreshStatus(forceHardwareRead: true);
    }

    #region Logical input view

    private bool? OpenLimitActive_NoLock => LogicalLimit(_rawIn1);

    private bool? ClosedLimitActive_NoLock => LogicalLimit(_rawIn2);

    private bool? LogicalLimit(bool? raw)
    {
        if (_options.IgnorePhysicalLimitSwitches || raw is null)
        {
            return null;
        }

        return _options.UseNormallyClosedLimitSwitches ? !raw.Value : raw.Value;
    }

    private bool? DriveFaultActive_NoLock => _rawIn3 is null ? null : (_options.FaultInputActiveHigh ? _rawIn3.Value : !_rawIn3.Value);

    private bool LimitsContradictory_NoLock => OpenLimitActive_NoLock == true && ClosedLimitActive_NoLock == true;

    private TimeSpan ReadStalenessLimit_NoLock
    {
        get
        {
            var cadence = IdleSupervisionInterval;
            if (_options.EnablePeriodicVerificationWhileMoving && _options.PeriodicVerificationInterval > cadence)
            {
                cadence = _options.PeriodicVerificationInterval;
            }

            var limit = TimeSpan.FromTicks(cadence.Ticks * 3);
            return limit > MinimumReadStaleness ? limit : MinimumReadStaleness;
        }
    }

    private bool InputsHealthy_NoLock(DateTimeOffset now)
        => _lastSuccessfulInputReadUtc is { } lastRead
           && _consecutiveInputReadFailures == 0
           && now - lastRead <= ReadStalenessLimit_NoLock;

    private bool RelayRegisterReadsHealthy_NoLock(DateTimeOffset now)
        => _lastSuccessfulRelayReadUtc is { } lastRead
           && _consecutiveRelayReadFailures == 0
           && now - lastRead <= ReadStalenessLimit_NoLock;

    private bool IsWatchdogActive_NoLock => _commandedMotion != RoofMotionDirection.None && _watchdogDeadlineUtc is not null;

    private double? WatchdogSecondsRemaining_NoLock(DateTimeOffset now)
    {
        if (!IsWatchdogActive_NoLock || _watchdogDeadlineUtc is not { } deadline)
        {
            return null;
        }

        var remaining = (deadline - now).TotalSeconds;
        return remaining > 0 ? remaining : 0;
    }

    private double? LeaseSecondsRemaining_NoLock(DateTimeOffset now)
    {
        if (_commandedMotion == RoofMotionDirection.None || _leaseDeadlineUtc is not { } deadline)
        {
            return null;
        }

        var remaining = (deadline - now).TotalSeconds;
        return remaining > 0 ? remaining : 0;
    }

    #endregion

    #region Status, snapshot and publication

    /// <summary>
    /// The only place the displayed status is derived. Called after every mutation.
    /// </summary>
    private void RecomputeStatus_NoLock(DateTimeOffset now)
    {
        RoofControllerStatus status;
        if (_relayRegisterState == RoofRelayRegisterState.Unverified)
        {
            status = RoofControllerStatus.Error;
        }
        else if (!_initialized)
        {
            status = RoofControllerStatus.NotInitialized;
        }
        else if (_faultLatched || LimitsContradictory_NoLock)
        {
            status = RoofControllerStatus.Error;
        }
        else if (_commandedMotion == RoofMotionDirection.Opening)
        {
            status = RoofControllerStatus.Opening;
        }
        else if (_commandedMotion == RoofMotionDirection.Closing)
        {
            status = RoofControllerStatus.Closing;
        }
        else if (OpenLimitActive_NoLock == true)
        {
            status = RoofControllerStatus.Open;
        }
        else if (ClosedLimitActive_NoLock == true)
        {
            status = RoofControllerStatus.Closed;
        }
        else
        {
            status = _lastMotionDirection switch
            {
                RoofMotionDirection.Opening => RoofControllerStatus.PartiallyOpen,
                RoofMotionDirection.Closing => RoofControllerStatus.PartiallyClose,
                _ => RoofControllerStatus.Stopped
            };
        }

        if (status != _status)
        {
            _logger.LogDebug("Roof status {Previous} -> {Status} (Commanded={Commanded}, Latched={Latched}, Relay={RelayState})",
                _status, status, _commandedMotion, _faultLatched, _relayRegisterState);
            _status = status;
            _lastTransitionUtc = now;
        }
    }

    /// <summary>
    /// Completes a state mutation: derives status, updates LEDs and telemetry, and publishes a snapshot if anything changed.
    /// </summary>
    private void FinishMutation_NoLock()
    {
        var now = Now;
        RecomputeStatus_NoLock(now);
        UpdateIndicatorLeds_NoLock();
        RecordTelemetryState_NoLock(now);
        PublishIfChanged_NoLock(now);
    }

    private RoofStatusResponse BuildSnapshot_NoLock(DateTimeOffset now, bool forKey)
    {
        var watchdogRemaining = WatchdogSecondsRemaining_NoLock(now);
        var leaseRemaining = LeaseSecondsRemaining_NoLock(now);
        if (forKey)
        {
            // Countdowns change continuously; publish at whole-second granularity.
            watchdogRemaining = watchdogRemaining is { } w ? Math.Ceiling(w) : null;
            leaseRemaining = leaseRemaining is { } l ? Math.Ceiling(l) : null;
        }

        // Published timestamps are wall-clock UTC. The key keeps controller time, so a wall-clock step (or drift between
        // the two clocks) is never a change by itself. Countdowns are differences of controller time and need no offset.
        var toWallClock = forKey ? TimeSpan.Zero : WallClockOffset;

        return new RoofStatusResponse(
            _status,
            _commandedMotion != RoofMotionDirection.None,
            _lastStopReason,
            _lastTransitionUtc + toWallClock,
            IsWatchdogActive_NoLock,
            watchdogRemaining,
            _rawIn4 == true,
            _hat.IsHardwareBacked,
            _options.IgnorePhysicalLimitSwitches)
        {
            StatusVersion = forKey ? 0 : _statusVersion,
            SnapshotUtc = forKey ? default : now + toWallClock,
            CommandedMotion = _commandedMotion,
            RelayRegisterState = _relayRegisterState,
            RelayRegisterMask = _relayRegisterMask,
            RelayRegisterReadsHealthy = RelayRegisterReadsHealthy_NoLock(now),
            LastSuccessfulRelayReadUtc = forKey ? null : _lastSuccessfulRelayReadUtc + toWallClock,
            ConsecutiveRelayReadFailures = _consecutiveRelayReadFailures,
            IsFaultLatched = _faultLatched,
            LatchedFaultReason = _faultLatched ? _latchedFaultReason : null,
            IsDriveFaultActive = DriveFaultActive_NoLock,
            IsOpenLimitActive = OpenLimitActive_NoLock,
            IsClosedLimitActive = ClosedLimitActive_NoLock,
            InputsHealthy = InputsHealthy_NoLock(now),
            // The read timestamp advances every supervision cycle; it is reported but does not by itself bump the version.
            LastSuccessfulInputReadUtc = forKey ? null : _lastSuccessfulInputReadUtc + toWallClock,
            ConsecutiveInputReadFailures = _consecutiveInputReadFailures,
            LeaseSecondsRemaining = leaseRemaining,
            IsClearFaultInProgress = _clearFaultInProgress,
            IsInitialized = _initialized,
            IsShuttingDown = _shuttingDown,
            ControllerName = _controllerName,
            ControllerInstanceId = _controllerInstanceId,
            HatMode = _hatMode,
            LastError = _lastError
        };
    }

    /// <summary>
    /// Increments <see cref="RoofStatusResponse.StatusVersion"/> and queues a snapshot for the dispatcher when any
    /// published field changed. Never invokes handlers on the calling thread.
    /// </summary>
    private void PublishIfChanged_NoLock(DateTimeOffset now)
    {
        var key = BuildSnapshot_NoLock(now, forKey: true);
        if (_lastPublishedKey is not null && _lastPublishedKey.Equals(key))
        {
            return;
        }

        _lastPublishedKey = key;
        _statusVersion++;
        var snapshot = BuildSnapshot_NoLock(now, forKey: false);
        _statusChannel.Writer.TryWrite(snapshot);
    }

    /// <summary>
    /// Single ordered reader. Handlers run one at a time, outside the controller lock; a throwing handler is logged and
    /// does not prevent delivery to other handlers. When the queue is full, the oldest pending snapshot is dropped.
    /// </summary>
    private async Task DispatchStatusChangesAsync()
    {
        var reader = _statusChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var snapshot))
                {
                    var handlers = StatusChanged;
                    if (handlers is null)
                    {
                        continue;
                    }

                    var args = new RoofStatusChangedEventArgs(snapshot);
                    t_onStatusDispatcherThread = true;
                    try
                    {
                        foreach (var handler in handlers.GetInvocationList())
                        {
                            try
                            {
                                ((EventHandler<RoofStatusChangedEventArgs>)handler)(this, args);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "StatusChanged handler threw; continuing with remaining handlers. StatusVersion={StatusVersion}", snapshot.StatusVersion);
                            }
                        }
                    }
                    finally
                    {
                        t_onStatusDispatcherThread = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status dispatcher stopped unexpectedly");
        }
    }

    /// <summary>
    /// Updates HAT LEDs (LED1 = open limit, LED2 = closed limit, LED3 = drive fault) with minimal I2C traffic.
    /// All three are off while limit switches are ignored. LED4 is left to the HAT, which shows IN4 (drive running).
    /// </summary>
    private void UpdateIndicatorLeds_NoLock()
    {
        byte mask = 0;
        if (!_options.IgnorePhysicalLimitSwitches)
        {
            if (OpenLimitActive_NoLock == true) mask |= 0x01;
            if (ClosedLimitActive_NoLock == true) mask |= 0x02;
            if (DriveFaultActive_NoLock == true) mask |= 0x04;
        }

        // While moving, the LEDs are written once per mask change whether or not the write succeeded: each retry would
        // add two bus transactions to every poll. While idle, a failed write is retried with every update.
        var moving = _commandedMotion != RoofMotionDirection.None;
        if (moving ? _lastAttemptedIndicatorLedMask == mask : _lastIndicatorLedMask == mask && _ledModesApplied)
        {
            return;
        }

        _lastAttemptedIndicatorLedMask = mask;
        try
        {
            // The SM-I-010 powers up with every LED following its input, where LED3 would show the raw IN3 level (lit
            // while healthy with the active-low fault wiring). Re-assert the modes with each change, and while idle until
            // a write succeeds, so a HAT that reset since initialization shows the logical states again.
            ApplyIndicatorLedModes_NoLock();
            var result = _hat.SetLedsMask(mask);
            if (result.IsSuccessful)
            {
                _lastIndicatorLedMask = mask;
            }
            else
            {
                _logger.LogDebug(result.Error, "Failed to set indicator LED mask 0x{Mask:X2}", mask);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception while setting indicator LED mask 0x{Mask:X2}", mask);
        }
    }

    /// <summary>
    /// Sets the HAT LED modes (<see cref="IndicatorLedModes"/>). Cosmetic: a failure is logged and never fatal, and the
    /// next LED change, or the next update while idle, tries again.
    /// </summary>
    private void ApplyIndicatorLedModes_NoLock()
    {
        _ledModesApplied = false;
        try
        {
            var result = _hat.SetLedModesMask(IndicatorLedModes);
            if (result.IsSuccessful)
            {
                _ledModesApplied = true;
            }
            else
            {
                _logger.LogDebug(result.Error, "Failed to set the indicator LED modes 0x{Modes:X2}", IndicatorLedModes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception while setting the indicator LED modes 0x{Modes:X2}", IndicatorLedModes);
        }
    }

    /// <summary>
    /// Reads the HAT LED modes back while idle. A HAT that resets between two input reads (a short supply dip) comes
    /// back with every LED following its input and no failed read to show it; a mismatch, or a failed read, makes the
    /// next LED update re-apply <see cref="IndicatorLedModes"/>. Cosmetic: never fatal.
    /// </summary>
    private void CheckIndicatorLedModes_NoLock()
    {
        if (!_ledModesApplied)
        {
            return;
        }

        try
        {
            var result = _hat.GetLedModesMask();
            if (result.IsSuccessful && (result.Value & 0x0F) == IndicatorLedModes)
            {
                return;
            }

            _logger.LogDebug(result.IsSuccessful ? null : result.Error, "Indicator LED modes read back {Modes}; re-applying 0x{Expected:X2}",
                result.IsSuccessful ? $"0x{result.Value:X2}" : "nothing", IndicatorLedModes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception while reading the indicator LED modes");
        }

        _ledModesApplied = false;
    }

    private void RecordTelemetryState_NoLock(DateTimeOffset now)
    {
        RoofControllerTelemetry.RecordControllerState(
            OpenLimitActive_NoLock == true,
            ClosedLimitActive_NoLock == true,
            DriveFaultActive_NoLock == true,
            _rawIn4 == true,
            IsWatchdogActive_NoLock,
            WatchdogSecondsRemaining_NoLock(now),
            _status);
    }

    private void RecordInputTransitions_NoLock()
    {
        var open = OpenLimitActive_NoLock;
        if (open is { } openValue && openValue != (_telemetryOpenLimit ?? false))
        {
            RoofControllerTelemetry.RecordLimitSwitchTransition("open", openValue);
        }

        if (open is not null)
        {
            _telemetryOpenLimit = open;
        }

        var closed = ClosedLimitActive_NoLock;
        if (closed is { } closedValue && closedValue != (_telemetryClosedLimit ?? false))
        {
            RoofControllerTelemetry.RecordLimitSwitchTransition("closed", closedValue);
        }

        if (closed is not null)
        {
            _telemetryClosedLimit = closed;
        }

        var fault = DriveFaultActive_NoLock;
        if (fault is { } faultValue && faultValue != (_telemetryFault ?? false))
        {
            RoofControllerTelemetry.RecordFaultTransition(faultValue);
        }

        if (fault is not null)
        {
            _telemetryFault = fault;
        }
    }

    #endregion

    #region Failure helpers

    private RoofControllerException Rejection_NoLock(RoofControllerErrorCode code, string message, Exception? inner = null)
        => new(code, message, BuildSnapshot_NoLock(Now, forKey: false), inner);

    private Result<T> Reject_NoLock<T>(RoofControllerErrorCode code, string message, Exception? inner = null)
    {
        _logger.LogInformation("Roof controller request rejected: {Code} - {Message}", code, message);
        return Result<T>.Failure(Rejection_NoLock(code, message, inner));
    }

    private Result<T> RejectShuttingDown_NoLock<T>()
        => _disposed
            ? Reject_NoLock<T>(RoofControllerErrorCode.ShuttingDown, "The roof controller has been disposed.", new ObjectDisposedException(nameof(RoofControllerServiceV4)))
            : Reject_NoLock<T>(RoofControllerErrorCode.ShuttingDown, "The roof controller is shutting down.");

    #endregion
}
