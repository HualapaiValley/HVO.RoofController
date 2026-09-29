using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Scriptable <see cref="IRoofControllerServiceV4"/> for health check and host tests. The status snapshot is set
/// directly; initialization and shutdown outcomes are queued or delegated.
/// </summary>
internal sealed class FakeRoofControllerService : IRoofControllerServiceV4
{
    private readonly ConcurrentQueue<Result<bool>> _initializationResults = new();
    private int _initializationCallCount;
    private int _shutdownCallCount;
    private int _stopCallCount;
    private int _snapshotCallCount;
    private volatile bool _initialized;
    private RoofControllerOptionsV4 _options = new();

    /// <summary>A healthy, initialized, idle snapshot on physical hardware with fresh input and relay reads and a verified register.</summary>
    public static RoofStatusResponse HealthySnapshot() => new(
        RoofControllerStatus.Closed, false, RoofControllerStopReason.None, DateTimeOffset.UtcNow, false, null, false, true, false)
    {
        IsInitialized = true,
        InputsHealthy = true,
        RelayRegisterReadsHealthy = true,
        RelayRegisterState = RoofRelayRegisterState.Verified,
        RelayRegisterMask = 0,
        LastSuccessfulInputReadUtc = DateTimeOffset.UtcNow,
        LastSuccessfulRelayReadUtc = DateTimeOffset.UtcNow,
        StatusVersion = 1
    };

    public event EventHandler<RoofStatusChangedEventArgs>? StatusChanged;

    /// <summary>The snapshot returned by <see cref="GetCurrentStatusSnapshot"/>.</summary>
    public RoofStatusResponse Snapshot { get; set; } = HealthySnapshot();

    /// <summary>Thrown by <see cref="GetCurrentStatusSnapshot"/> when set.</summary>
    public Exception? SnapshotException { get; set; }

    /// <summary>Behavior of <see cref="ShutdownAsync"/>; defaults to a verified success.</summary>
    public Func<CancellationToken, Task<Result<RoofStatusResponse>>>? ShutdownBehavior { get; set; }

    public int InitializationCallCount => Volatile.Read(ref _initializationCallCount);

    public int ShutdownCallCount => Volatile.Read(ref _shutdownCallCount);

    public int StopCallCount => Volatile.Read(ref _stopCallCount);

    public bool IsInitialized => _initialized;

    public bool IsUsingPhysicalHardware => Snapshot.IsUsingPhysicalHardware;

    public bool IsIgnoringPhysicalLimitSwitches => Snapshot.IsIgnoringPhysicalLimitSwitches;

    public RoofControllerStatus Status => Snapshot.Status;

    public bool IsMoving => Snapshot.IsMoving;

    public RoofControllerStopReason LastStopReason => Snapshot.LastStopReason;

    public DateTimeOffset? LastTransitionUtc => Snapshot.LastTransitionUtc;

    public bool IsWatchdogActive => Snapshot.IsWatchdogActive;

    public double? WatchdogSecondsRemaining => Snapshot.WatchdogSecondsRemaining;

    public bool IsAtSpeed => Snapshot.IsAtSpeed;

    public bool IsServiceDisposed { get; set; }

    public bool IsShuttingDown => Snapshot.IsShuttingDown;

    public void EnqueueInitialization(Result<bool> result) => _initializationResults.Enqueue(result);

    public RoofStatusResponse GetCurrentStatusSnapshot()
    {
        Interlocked.Increment(ref _snapshotCallCount);
        return SnapshotException is { } ex ? throw ex : Snapshot;
    }

    /// <summary>How many times <see cref="GetCurrentStatusSnapshot"/> was called.</summary>
    public int SnapshotCallCount => Volatile.Read(ref _snapshotCallCount);

    /// <summary>True while anything is subscribed to <see cref="StatusChanged"/>.</summary>
    public bool HasStatusChangedHandlers => StatusChanged is not null;

    /// <summary>Raises <see cref="StatusChanged"/> with <paramref name="status"/> on the calling thread.</summary>
    public void RaiseStatusChanged(RoofStatusResponse status) => StatusChanged?.Invoke(this, new RoofStatusChangedEventArgs(status));

    public RoofControllerOptionsV4 GetConfigurationSnapshot() => _options;

    public RoofControllerConfigurationState GetConfigurationState() => new(_options, 1);

    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions)
    {
        _options = updatedOptions;
        return Result<RoofControllerOptionsV4>.Success(_options);
    }

    public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 updatedOptions, long expectedVersion)
        => UpdateConfiguration(updatedOptions);

    public Result<RoofControllerOptionsV4> ApplyConfiguration(RoofControllerOptionsV4 updatedOptions, bool includeLocalOnlySettings)
        => UpdateConfiguration(updatedOptions);

    public Result<RoofStatusResponse> RenewLease() => Result<RoofStatusResponse>.Success(Snapshot);

    public Task<Result<RoofStatusResponse>> ShutdownAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _shutdownCallCount);
        return ShutdownBehavior is { } behavior
            ? behavior(cancellationToken)
            : Task.FromResult(Result<RoofStatusResponse>.Success(Snapshot));
    }

    public Task<Result<bool>> Initialize(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _initializationCallCount);
        if (!_initializationResults.TryDequeue(out var result))
        {
            result = Result<bool>.Success(true);
        }

        if (result.IsSuccessful)
        {
            _initialized = true;
        }

        return Task.FromResult(result);
    }

    public Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop)
    {
        Interlocked.Increment(ref _stopCallCount);
        return Result<RoofControllerStatus>.Success(Snapshot.Status);
    }

    public Result<RoofControllerStatus> Open() => Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);

    public Result<RoofControllerStatus> Close() => Result<RoofControllerStatus>.Success(RoofControllerStatus.Closing);

    public void RefreshStatus(bool forceHardwareRead = false)
        => StatusChanged?.Invoke(this, new RoofStatusChangedEventArgs(Snapshot));

    public Task<Result<bool>> ClearFault(int pulseMs = RoofControllerLimits.DefaultClearFaultPulseMilliseconds, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<bool>.Success(true));
}
