using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using HVO.RoofControllerV4.RPi.Logic;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Totals of the roof meter's counters, and the count, sum and maximum of its histograms (docs/telemetry.md, Roof
/// metrics), by instrument and tags, as a collector aggregates them: a long run holds one entry per tag combination,
/// not one per value. Tag values are written as the exporter writes them (<c>true</c>, not <c>True</c>). The meter is
/// process-wide, so this sees every host in the process: a scenario runs alone (<c>[DoNotParallelize]</c>).
/// </summary>
internal sealed class RoofMeterCounters : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<(string Instrument, string Tags), long> _totals = [];
    private readonly Dictionary<(string Instrument, string Tags), Distribution> _distributions = [];

    public RoofMeterCounters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RoofControllerTelemetry.InstrumentationName && instrument is Counter<long> or Histogram<double>)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnCount);
        _listener.SetMeasurementEventCallback<double>(OnValue);
        _listener.Start();
    }

    /// <summary>The total of <paramref name="instrument"/> over the measurements that carry every tag given.</summary>
    public long Total(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_totals)
        {
            return _totals.Where(t => Matches(t.Key, instrument, tags)).Sum(t => t.Value);
        }
    }

    /// <summary>The totals of <paramref name="instrument"/> by the value of the tag <paramref name="key"/>.</summary>
    public IReadOnlyDictionary<string, long> TotalsBy(string instrument, string key)
    {
        lock (_totals)
        {
            return _totals
                .Where(t => t.Key.Instrument == instrument)
                .GroupBy(t => t.Key.Tags.Split(';').FirstOrDefault(tag => tag.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Value));
        }
    }

    /// <summary>The values the histogram <paramref name="instrument"/> recorded with every tag given.</summary>
    public Distribution Values(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_distributions)
        {
            return _distributions.Where(d => Matches(d.Key, instrument, tags))
                .Select(d => d.Value)
                .Aggregate(Distribution.Empty, (all, next) => all.Add(next));
        }
    }

    public void Dispose() => _listener.Dispose();

    private static bool Matches((string Instrument, string Tags) key, string instrument, (string Key, string Value)[] tags)
        => key.Instrument == instrument && tags.All(t => key.Tags.Split(';').Contains($"{t.Key}={t.Value}"));

    private static (string, string) Key(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var pairs = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            var value = tag.Value is bool flag ? (flag ? "true" : "false") : Convert.ToString(tag.Value, CultureInfo.InvariantCulture);
            pairs.Add($"{tag.Key}={value}");
        }

        pairs.Sort(StringComparer.Ordinal);
        return (instrument.Name, string.Join(';', pairs));
    }

    private void OnCount(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var key = Key(instrument, tags);
        lock (_totals)
        {
            _totals[key] = _totals.GetValueOrDefault(key) + value;
        }
    }

    private void OnValue(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var key = Key(instrument, tags);
        lock (_distributions)
        {
            _distributions[key] = _distributions.GetValueOrDefault(key, Distribution.Empty).Add(new Distribution(1, value, value));
        }
    }

    /// <summary>A histogram's count, sum and maximum, as the exporter reports them (without the buckets).</summary>
    internal readonly record struct Distribution(long Count, double Sum, double Max)
    {
        public static Distribution Empty { get; } = new(0, 0, double.NegativeInfinity);

        public double Mean => Count == 0 ? 0 : Sum / Count;

        public Distribution Add(Distribution other) => new(Count + other.Count, Sum + other.Sum, Math.Max(Max, other.Max));
    }
}
