using System.Net;

namespace HVO.RoofControllerV4.Alpaca.Security;

/// <summary>
/// Slows down guessing (#220, O3): per client address, the answer to a request already refused as not authorised is
/// delayed, more with each refusal in a row (100 ms after the third, doubling, at most 2 s). It never delays forwarding a
/// command, never refuses a correct key, and never touches reads, AbortSlew or anything else. Forgotten 10 minutes after
/// the last refusal; a correct key forgets the address at once.
/// </summary>
public sealed class AlpacaAuthFailures(TimeProvider time)
{
    /// <summary>Refusals in a row before the answers slow down.</summary>
    internal const int FreeRefusals = 3;

    /// <summary>The longest delay.</summary>
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Memory = TimeSpan.FromMinutes(10);
    private const int MaximumAddresses = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (int Count, DateTimeOffset Last)> _failures = [];

    /// <summary>Counts a refusal from <paramref name="address"/> and returns how long its answer waits.</summary>
    public TimeSpan Refused(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_failures.Count >= MaximumAddresses && !_failures.ContainsKey(address))
            {
                foreach (var stale in _failures.Where(entry => now - entry.Value.Last > Memory).Select(entry => entry.Key).ToArray())
                {
                    _failures.Remove(stale);
                }

                if (_failures.Count >= MaximumAddresses)
                {
                    _failures.Remove(_failures.MinBy(entry => entry.Value.Last).Key);
                }
            }

            var count = _failures.TryGetValue(address, out var previous) && now - previous.Last <= Memory ? previous.Count + 1 : 1;
            _failures[address] = (count, now);
            if (count <= FreeRefusals)
            {
                return TimeSpan.Zero;
            }

            var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, Math.Min(count - FreeRefusals - 1, 10)));
            return delay > MaximumDelay ? MaximumDelay : delay;
        }
    }

    /// <summary>A correct key from <paramref name="address"/>: its count starts again.</summary>
    public void Accepted(IPAddress address)
    {
        lock (_gate)
        {
            _failures.Remove(address);
        }
    }

    /// <summary>The answer to a refusal as not authorised, after its delay.</summary>
    public Task DelayAsync(IPAddress address) => Refused(address) is { } delay && delay > TimeSpan.Zero
        ? Task.Delay(delay, time)
        : Task.CompletedTask;
}
