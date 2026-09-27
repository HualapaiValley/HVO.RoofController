using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.Core.Results;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// The production controller and the real HAT library driving the emulated plant (<see cref="RoofPlant"/>) on a
/// <see cref="ManualTimeProvider"/>. The harness stands in for the two background loops, which would otherwise run on
/// real time: it polls the inputs every <see cref="RoofControllerOptionsV4.DigitalInputPollInterval"/> and delivers
/// IN1-IN4 edges in order, as the HAT library's poll loop does, and runs a supervision cycle whenever the controller's
/// supervision delay has elapsed, as the supervision loop does (recomputing the delay whenever the controller wakes the
/// loop, and only then). Everything else is production code.
/// </summary>
internal sealed class PlantHarness : IDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly bool _polling;
    private (bool, bool, bool, bool)? _lastInputs;
    private long _wakeCount;
    private DateTimeOffset _nextPoll;
    private DateTimeOffset _nextSupervision;

    private PlantHarness(RoofPlantOptions plantOptions, RoofControllerOptionsV4 options)
    {
        Time = new ManualTimeProvider();
        Plant = new RoofPlant(plantOptions, Time);
        Bus = new EmulatedHatRegisterClient(Plant);
        Hat = new FourRelayFourInputHat(Bus, ownsClient: true);
        Log = new CapturingLogger<RoofControllerServiceV4>();

        // The HAT library's poll loop runs on real time; the harness delivers its edges instead.
        _polling = options.EnableDigitalInputPolling;
        _pollInterval = options.DigitalInputPollInterval;
        options.EnableDigitalInputPolling = false;
        Options = options;
        Controller = new SimulatedRoofControllerService(options, Hat, Time, Log);
    }

    public ManualTimeProvider Time { get; }

    public RoofPlant Plant { get; }

    public EmulatedHatRegisterClient Bus { get; }

    public FourRelayFourInputHat Hat { get; }

    public SimulatedRoofControllerService Controller { get; }

    public CapturingLogger<RoofControllerServiceV4> Log { get; }

    /// <summary>The controller's options (with edge polling handed to the harness).</summary>
    public RoofControllerOptionsV4 Options { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>Register 0 as the HAT holds it: the relay coils the controller has commanded.</summary>
    public byte RelayRegister
    {
        get
        {
            lock (Plant.SyncRoot)
            {
                Plant.Sync();
                return Plant.Hat.RelayRegister;
            }
        }
    }

    public RoofControllerStatus Status => Controller.GetCurrentStatusSnapshot().Status;

    public RoofStatusResponse Snapshot => Controller.GetCurrentStatusSnapshot();

    /// <summary>IN4 as the plant drives it: the drive's run output (TB-14, P142 = 1).</summary>
    public bool DriveRunOutput
    {
        get
        {
            lock (Plant.SyncRoot)
            {
                Plant.Sync();
                return (Plant.InputBits & 0x08) != 0;
            }
        }
    }

    /// <summary>
    /// Creates the plant and the controller with the production configuration (<c>appsettings.json</c>), then
    /// <paramref name="configure"/>, and initializes the controller.
    /// </summary>
    public static async Task<PlantHarness> StartAsync(
        RoofPlantOptions? plant = null,
        Action<RoofControllerOptionsV4>? configure = null,
        bool initialize = true)
    {
        var options = ProductionOptions.Load();
        configure?.Invoke(options);
        var harness = new PlantHarness(plant ?? new RoofPlantOptions(), options);
        if (initialize)
        {
            var result = await harness.Controller.Initialize(CancellationToken.None);
            if (!result.IsSuccessful)
            {
                harness.Dispose();
                throw new InvalidOperationException($"Controller initialization failed: {result.Error?.Message}");
            }

            harness.Started();
        }

        return harness;
    }

    /// <summary>Starts the loops after an initialization the test ran itself.</summary>
    public void Started()
    {
        _wakeCount = Controller.SupervisionWakeCount;
        _lastInputs = null;
        _nextPoll = Now;
        Poll();
        _nextPoll = Now + _pollInterval;
        ScheduleSupervision();
    }

    public Result<RoofControllerStatus> Open() => Command(Controller.Open);

    public Result<RoofControllerStatus> Close() => Command(Controller.Close);

    public Result<RoofControllerStatus> Stop() => Command(() => Controller.Stop());

    public Result<RoofStatusResponse> RenewLease() => Controller.RenewLease();

    /// <summary>
    /// Runs a clear-fault pulse to completion: time advances (with polling and supervision) to the end of the pulse,
    /// then the harness waits for the pulse to finish before anything else runs.
    /// </summary>
    public async Task<Result<bool>> ClearFaultAsync(int pulseMs = RoofControllerLimits.DefaultClearFaultPulseMilliseconds)
    {
        var end = Now + TimeSpan.FromMilliseconds(pulseMs);
        var clear = Controller.ClearFault(pulseMs);
        while (!clear.IsCompleted && Now < end)
        {
            AdvanceTo(NextEvent(end));
            if (Now < end)
            {
                ServiceDue();
            }
        }

        var result = await clear.WaitAsync(TimeSpan.FromSeconds(10));
        FollowWakes();
        ServiceDue();
        return result;
    }

    /// <summary>Runs the loops for <paramref name="duration"/> of simulated time.</summary>
    public void RunFor(TimeSpan duration)
    {
        var end = Now + duration;
        while (Now < end)
        {
            Tick(end);
        }
    }

    /// <summary>Runs the loops until <paramref name="condition"/> holds; false when <paramref name="timeout"/> passes first.</summary>
    public bool RunUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = Now + timeout;
        while (!condition())
        {
            if (Now >= end)
            {
                return false;
            }

            Tick(end);
        }

        return true;
    }

    /// <summary>Runs until the controller is no longer moving and the roof is at rest.</summary>
    public bool RunUntilStopped(TimeSpan timeout)
        => RunUntil(() => !Controller.IsMoving && Plant.Velocity == 0 && !Plant.Drive.IsDriving, timeout);

    /// <summary>Violations of the plant's invariants so far.</summary>
    public PlantViolation[] Violations => Plant.Violations.ToArray();

    public void Dispose()
    {
        Controller.Dispose();
        Hat.Dispose();
    }

    private Result<RoofControllerStatus> Command(Func<Result<RoofControllerStatus>> command)
    {
        var result = command();
        FollowWakes();
        return result;
    }

    /// <summary>Recomputes the supervision delay when the controller has woken the loop, as the loop does.</summary>
    private void FollowWakes()
    {
        var wakes = Controller.SupervisionWakeCount;
        if (wakes != _wakeCount)
        {
            _wakeCount = wakes;
            ScheduleSupervision();
        }
    }

    private void Tick(DateTimeOffset limit)
    {
        AdvanceTo(NextEvent(limit));
        ServiceDue();
    }

    private DateTimeOffset NextEvent(DateTimeOffset limit)
    {
        var next = _nextSupervision < limit ? _nextSupervision : limit;
        return _polling && _nextPoll < next ? _nextPoll : next;
    }

    private void AdvanceTo(DateTimeOffset target)
    {
        if (target > Now)
        {
            Time.Advance(target - Now);
        }

        Plant.Sync();
    }

    private void ServiceDue()
    {
        // A timer callback (the watchdog) may have run during the advance.
        FollowWakes();
        if (_polling && Now >= _nextPoll)
        {
            Poll();
            _nextPoll = Now + _pollInterval;
        }

        if (Now >= _nextSupervision)
        {
            Controller.RunSupervisionCycle();
            _wakeCount = Controller.SupervisionWakeCount;
            ScheduleSupervision();
        }
    }

    private void ScheduleSupervision() => _nextSupervision = Now + Controller.GetSupervisionDelay();

    /// <summary>One pass of the HAT library's poll loop: a failed read keeps the previous levels and raises nothing.</summary>
    private void Poll()
    {
        if (!_polling)
        {
            return;
        }

        var read = Hat.GetAllDigitalInputs();
        if (!read.IsSuccessful)
        {
            return;
        }

        var current = read.Value;
        var previous = _lastInputs;
        _lastInputs = current;
        if (previous is not { } last)
        {
            return;
        }

        if (last.Item1 != current.Item1) Controller.SimForwardLimitRaw(current.Item1);
        if (last.Item2 != current.Item2) Controller.SimReverseLimitRaw(current.Item2);
        if (last.Item3 != current.Item3) Controller.SimFaultRaw(current.Item3);
        if (last.Item4 != current.Item4) Controller.SimAtSpeedRaw(current.Item4);
        FollowWakes();
    }
}

/// <summary>The controller options as deployed: <c>src/HVO.RoofControllerV4.RPi/appsettings.json</c>.</summary>
internal static class ProductionOptions
{
    public static string AppSettingsPath { get; } = FindAppSettings();

    public static RoofControllerOptionsV4 Load()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(AppSettingsPath, optional: false, reloadOnChange: false).Build();
        var section = configuration.GetSection(nameof(RoofControllerOptionsV4));
        if (!section.Exists())
        {
            throw new InvalidOperationException($"{AppSettingsPath} has no {nameof(RoofControllerOptionsV4)} section.");
        }

        var options = new RoofControllerOptionsV4();
        section.Bind(options);
        return options;
    }

    private static string FindAppSettings()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "HVO.RoofControllerV4.RPi", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("src/HVO.RoofControllerV4.RPi/appsettings.json was not found above the test directory.");
    }
}
