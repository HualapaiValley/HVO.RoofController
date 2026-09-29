namespace HVO.RoofControllerV4.Client;

/// <summary>The delay before a reconnect attempt: doubling from the initial delay up to the maximum, with jitter.</summary>
public static class RoofReconnectDelay
{
    private const int MaximumDoublings = 30;

    /// <summary>
    /// The delay before reconnect attempt <paramref name="attempt"/> (0 for the first retry). The base delay doubles with
    /// each attempt up to <paramref name="maximum"/>; <paramref name="jitter"/> spreads it by that fraction either way, but
    /// never below <paramref name="initial"/> or past the maximum, using <paramref name="random"/> (0 to 1) to place it.
    /// </summary>
    public static TimeSpan For(int attempt, TimeSpan initial, TimeSpan maximum, double jitter, double random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var baseTicks = Math.Min(maximum.Ticks, initial.Ticks * Math.Pow(2, Math.Min(attempt, MaximumDoublings)));
        var lower = Math.Max(initial.Ticks, baseTicks * (1 - jitter));
        var upper = Math.Min(maximum.Ticks, baseTicks * (1 + jitter));
        return TimeSpan.FromTicks((long)(lower + ((upper - lower) * Math.Clamp(random, 0, 1))));
    }
}
