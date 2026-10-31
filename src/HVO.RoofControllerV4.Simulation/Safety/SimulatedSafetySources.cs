using HVO.RoofControllerV4.Common.Safety;

namespace HVO.RoofControllerV4.Simulation.Safety;

/// <summary>How a simulated safety source answers a read.</summary>
public enum SimulatedSourceBehaviour
{
    /// <summary>Answers at once with its current value.</summary>
    Answers,

    /// <summary>Throws an <see cref="IOException"/>, as a device that cannot be reached.</summary>
    Fails,

    /// <summary>Does not answer until the read is cancelled, as a device that stopped responding.</summary>
    Hangs,

    /// <summary>
    /// Does not answer, and ignores cancellation, until <see cref="SimulatedSafetySource.Release"/>: an adapter that
    /// blocks.
    /// </summary>
    IgnoresCancellation
}

/// <summary>
/// What the simulated sources share: a behaviour to answer with, the time the device says it observed the value, and a
/// count of reads. Safe to change from a test while the safety service reads it.
/// </summary>
public abstract class SimulatedSafetySource : IRoofSafetySource
{
    private readonly object _gate = new();
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;

    protected SimulatedSafetySource(string name, TimeSpan? maxReadingAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        MaxReadingAge = maxReadingAge;
    }

    public string Name { get; }

    public TimeSpan? MaxReadingAge { get; }

    /// <summary>How the next read answers.</summary>
    public SimulatedSourceBehaviour Behaviour { get; set; } = SimulatedSourceBehaviour.Answers;

    /// <summary>When the device says it observed the value; null reports none (the read's own time counts).</summary>
    public DateTimeOffset? ObservedUtc { get; set; }

    /// <summary>Extra words the reading carries.</summary>
    public string? Detail { get; set; }

    /// <summary>Reads started so far.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Ends every read held by <see cref="SimulatedSourceBehaviour.IgnoresCancellation"/>, which then fails.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _released.TrySetResult();
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Waits as <see cref="Behaviour"/> says, then returns, or throws as a device that cannot be read.</summary>
    protected async Task AnswerAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        switch (Behaviour)
        {
            case SimulatedSourceBehaviour.Fails:
                throw new IOException($"Simulated safety source '{Name}' cannot be reached.");
            case SimulatedSourceBehaviour.Hangs:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                break;
            case SimulatedSourceBehaviour.IgnoresCancellation:
                Task released;
                lock (_gate)
                {
                    released = _released.Task;
                }

                await released.ConfigureAwait(false);
                throw new IOException($"Simulated safety source '{Name}' was released without an answer.");
        }
    }
}

/// <summary>A simulated telescope mount: answers with <see cref="IsParked"/>.</summary>
public sealed class SimulatedTelescopeParkSource : SimulatedSafetySource, ITelescopeParkSource
{
    public SimulatedTelescopeParkSource(string name = "Simulated mount", bool isParked = true, TimeSpan? maxReadingAge = null)
        : base(name, maxReadingAge)
    {
        IsParked = isParked;
    }

    /// <summary>What the next answer says.</summary>
    public bool IsParked { get; set; }

    /// <summary>Whether this mount can block the roof (true by default), as its setting says. Set before the service reads it.</summary>
    public bool BlocksRoof { get; init; } = true;

    public async Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken)
    {
        await AnswerAsync(cancellationToken).ConfigureAwait(false);
        return new TelescopeParkReading(IsParked, ObservedUtc, Detail);
    }
}

/// <summary>A simulated weather safety monitor: answers with <see cref="IsSafe"/>.</summary>
public sealed class SimulatedWeatherSafetySource : SimulatedSafetySource, IWeatherSafetySource
{
    public SimulatedWeatherSafetySource(string name = "Simulated weather", bool isSafe = true, TimeSpan? maxReadingAge = null)
        : base(name, maxReadingAge)
    {
        IsSafe = isSafe;
    }

    /// <summary>What the next answer says.</summary>
    public bool IsSafe { get; set; }

    /// <summary>Required (the default) or Ignored, as its setting says. Set before the service reads it.</summary>
    public RoofWeatherSourceRequirement Requirement { get; init; } = RoofWeatherSourceRequirement.Required;

    public async Task<WeatherSafetyReading> ReadWeatherSafetyAsync(CancellationToken cancellationToken)
    {
        await AnswerAsync(cancellationToken).ConfigureAwait(false);
        return new WeatherSafetyReading(IsSafe, ObservedUtc, Detail);
    }
}
