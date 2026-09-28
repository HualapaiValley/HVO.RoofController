using HVO.Iot.Devices.Abstractions;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.Simulation.Emulator;

/// <summary>What the HAT emulator runs: the plant, its bus timing and its clock.</summary>
public sealed record HatEmulatorSessionOptions
{
    /// <summary>The plant: the documented installation by default.</summary>
    public RoofPlantOptions Plant { get; init; } = new();

    /// <summary>The I2C bus the emulated HAT reports; the controller checks it matches its own.</summary>
    public int BusId { get; init; } = 1;

    /// <summary>
    /// The bus time each access takes before the response is sent: the HAT library's by default (the transfer at 100 kHz,
    /// then 15 ms), so the controller sees the physical bus's latency.
    /// </summary>
    public EmulatedBusTiming BusTiming { get; init; } = EmulatedBusTiming.LibraryDefault;

    /// <summary>How many times as fast as real time the plant runs. The controller's own timing is not scaled.</summary>
    public double TimeScale { get; init; } = 1;

    /// <summary>How often the plant is brought up to the clock between accesses.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Plant);
        ArgumentNullException.ThrowIfNull(BusTiming);
        Plant.Validate();
        BusTiming.Validate();
        if (BusId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BusId));
        }

        if (TickInterval <= TimeSpan.Zero || TickInterval > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(TickInterval), "The tick interval must be positive and at most 1 s.");
        }

        _ = new ScaledTimeProvider(scale: TimeScale);
    }
}

/// <summary>
/// The HAT emulator's plant on a (scaled) real-time clock, with the emulated register client the server exposes and the
/// controls the emulator's HTTP API offers. <see cref="Reset"/> replaces the plant and the client; the server reads the
/// client again for every access.
/// </summary>
public sealed class HatEmulatorSession : IDisposable
{
    private readonly object _gate = new();
    private readonly HatEmulatorSessionOptions _options;
    private readonly ITimer _ticker;
    private RoofPlant _plant;
    private EmulatedHatRegisterClient _client;
    private bool _disposed;

    public HatEmulatorSession(HatEmulatorSessionOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new HatEmulatorSessionOptions();
        _options.Validate();
        Clock = new ScaledTimeProvider(timeProvider ?? TimeProvider.System, _options.TimeScale);
        (_plant, _client) = Create(_options.Plant);
        _ticker = (timeProvider ?? TimeProvider.System).CreateTimer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
    }

    /// <summary>The plant's clock.</summary>
    public ScaledTimeProvider Clock { get; }

    public HatEmulatorSessionOptions Options => _options;

    /// <summary>The current plant.</summary>
    public RoofPlant Plant
    {
        get { lock (_gate) { return _plant; } }
    }

    /// <summary>The current emulated register client, with its bus-failure controls.</summary>
    public EmulatedHatRegisterClient Client
    {
        get { lock (_gate) { return _client; } }
    }

    /// <summary>The number of resets so far.</summary>
    public int Generation { get; private set; }

    /// <summary>The client that answers an access now (for <see cref="HatEmulatorServer"/>).</summary>
    public II2cRegisterClient CurrentClient() => Client;

    /// <summary>
    /// Replaces the plant with a new one: the HAT at power-on (relays off), the drive healthy, the limits and the roof at
    /// <paramref name="initialPosition"/> (or the configured start) and the given wiring. Bus failures and the injected
    /// faults are cleared.
    /// </summary>
    public void Reset(double? initialPosition = null, WiringFault? wiring = null)
    {
        var plantOptions = _options.Plant with
        {
            InitialPosition = initialPosition ?? _options.Plant.InitialPosition,
            Wiring = wiring ?? _options.Plant.Wiring
        };
        var (plant, client) = Create(plantOptions);
        EmulatedHatRegisterClient old;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            old = _client;
            _plant = plant;
            _client = client;
            Generation++;
        }

        old.Dispose();
    }

    /// <summary>A snapshot of the plant and the bus controls.</summary>
    public HatEmulatorStatus GetStatus()
    {
        RoofPlant plant;
        EmulatedHatRegisterClient client;
        int generation;
        lock (_gate)
        {
            plant = _plant;
            client = _client;
            generation = Generation;
        }

        lock (plant.SyncRoot)
        {
            plant.Sync();
            var travel = plant.Options.Mechanics.TravelMeters;
            return new HatEmulatorStatus
            {
                Generation = generation,
                Elapsed = plant.Elapsed,
                TimeScale = Clock.Scale,
                PositionMeters = plant.Position,
                VelocityMetersPerSecond = plant.Velocity,
                TravelMeters = travel,
                OpenPercent = Math.Clamp(plant.Position / travel * 100, 0, 100),
                RelayRegister = plant.Hat.RelayRegister,
                RelayContacts = [plant.Hat.IsContactClosed(1), plant.Hat.IsContactClosed(2), plant.Hat.IsContactClosed(3), plant.Hat.IsContactClosed(4)],
                InputBits = plant.Hat.InputBits,
                HatPowered = plant.Hat.Powered,
                DrivePowered = plant.Drive.Powered,
                DriveMode = plant.Drive.Mode,
                DriveTrip = plant.Drive.Trip,
                OutputFrequencyHz = plant.Drive.OutputFrequencyHz,
                OutputDirection = plant.Drive.OutputDirection,
                OpenLimitActuated = plant.OpenLimit.Actuated,
                ClosedLimitActuated = plant.ClosedLimit.Actuated,
                OpenLimitFault = plant.OpenLimit.Fault,
                ClosedLimitFault = plant.ClosedLimit.Fault,
                Wiring = plant.Wiring,
                ExternalStopOpen = plant.ExternalStopOpen,
                Jammed = plant.Jammed,
                Violations = plant.Violations.Count,
                BusReads = client.ReadCount,
                BusWrites = client.WriteCount,
                InjectedBusFailures = client.InjectedFailures,
                FailReads = client.FailReads,
                FailWrites = client.FailWrites,
                FailInputReads = client.FailInputReads
            };
        }
    }

    public void Dispose()
    {
        EmulatedHatRegisterClient client;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            client = _client;
        }

        _ticker.Dispose();
        client.Dispose();
    }

    private (RoofPlant Plant, EmulatedHatRegisterClient Client) Create(RoofPlantOptions plantOptions)
    {
        var plant = new RoofPlant(plantOptions, Clock);

        // Bus time is spent in real time by the thread serving the access, as the library's I2C client sleeps.
        var client = new EmulatedHatRegisterClient(plant, _options.BusId, _options.BusTiming, Thread.Sleep);
        return (plant, client);
    }

    private void Tick()
    {
        try
        {
            Plant.Sync();
        }
        catch (Exception)
        {
            // A scheduled test action threw; the next access reports the plant's state.
        }
    }
}

/// <summary>A snapshot of the HAT emulator's plant and bus controls.</summary>
public sealed record HatEmulatorStatus
{
    /// <summary>The number of resets so far.</summary>
    public int Generation { get; init; }

    /// <summary>Plant time since the last reset.</summary>
    public TimeSpan Elapsed { get; init; }

    public double TimeScale { get; init; }

    /// <summary>Roof position: 0 at the closed limit's operating point, <see cref="TravelMeters"/> at the open limit's.</summary>
    public double PositionMeters { get; init; }

    public double VelocityMetersPerSecond { get; init; }

    public double TravelMeters { get; init; }

    /// <summary>Position as a percentage of the travel, clamped to 0-100.</summary>
    public double OpenPercent { get; init; }

    /// <summary>Register 0: the relay coils the controller commanded (bit 0 = RLY1).</summary>
    public byte RelayRegister { get; init; }

    /// <summary>RLY1-RLY4 contact states (a welded or dead contact can differ from the coil).</summary>
    public IReadOnlyList<bool> RelayContacts { get; init; } = [];

    /// <summary>Register 3: the opto input levels (bit 0 = IN1).</summary>
    public byte InputBits { get; init; }

    public bool HatPowered { get; init; }

    public bool DrivePowered { get; init; }

    public SmVectorMode DriveMode { get; init; }

    public SmVectorTrip DriveTrip { get; init; }

    public double OutputFrequencyHz { get; init; }

    /// <summary>+1 forward (open with the documented wiring), -1 reverse, 0 stopped.</summary>
    public int OutputDirection { get; init; }

    public bool OpenLimitActuated { get; init; }

    public bool ClosedLimitActuated { get; init; }

    public LimitSwitchFault OpenLimitFault { get; init; }

    public LimitSwitchFault ClosedLimitFault { get; init; }

    public WiringFault Wiring { get; init; }

    public bool ExternalStopOpen { get; init; }

    public bool Jammed { get; init; }

    /// <summary>The number of broken invariants (a hard-stop contact, both direction contacts closed, ...).</summary>
    public int Violations { get; init; }

    public long BusReads { get; init; }

    public long BusWrites { get; init; }

    public long InjectedBusFailures { get; init; }

    public bool FailReads { get; init; }

    public bool FailWrites { get; init; }

    public bool FailInputReads { get; init; }
}
