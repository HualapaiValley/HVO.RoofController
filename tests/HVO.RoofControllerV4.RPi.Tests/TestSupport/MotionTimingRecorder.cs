using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using HVO.RoofControllerV4.RPi.Logic;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>One motion timing value (seconds, rounded to the microsecond) and its tags; null when the instrument has no such tag.</summary>
internal sealed record TimingSample(double Seconds, string? Direction, string? Reason = null, bool? FromLimit = null);

/// <summary>
/// Collects the roof meter's motion timing histograms (docs/telemetry.md, Motion timing). The meter is process-wide:
/// <see cref="ForThisTest"/> keeps only values recorded in the calling test's execution context, for a controller that
/// records them synchronously inside the test's calls (a manual clock, no background supervision). A scenario's host
/// records them on its own threads, so a scenario, which runs alone (<c>[DoNotParallelize]</c>), takes them all.
/// </summary>
internal sealed class MotionTimingRecorder : IDisposable
{
    private static readonly AsyncLocal<MotionTimingRecorder?> Current = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, TimingSample Sample)> _samples = [];
    private readonly bool _thisTestOnly;

    private MotionTimingRecorder(bool thisTestOnly)
    {
        _thisTestOnly = thisTestOnly;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RoofControllerTelemetry.InstrumentationName && instrument is Histogram<double>)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
    }

    /// <summary>
    /// Values recorded in the caller's execution context. Create it from the test method or a synchronous helper it
    /// calls: an async method restores the context when it returns, and the recorder would then see nothing.
    /// </summary>
    public static MotionTimingRecorder ForThisTest()
    {
        var recorder = new MotionTimingRecorder(thisTestOnly: true);
        Current.Value = recorder;
        return recorder;
    }

    /// <summary>Every value recorded in the process.</summary>
    public static MotionTimingRecorder ForTheProcess() => new(thisTestOnly: false);

    /// <summary>The values <paramref name="instrument"/> recorded, in order.</summary>
    public IReadOnlyList<TimingSample> Of(string instrument)
    {
        lock (_samples)
        {
            return _samples.Where(s => s.Instrument == instrument).Select(s => s.Sample).ToArray();
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (_thisTestOnly && Current.Value != this)
        {
            return;
        }

        string? direction = null, reason = null;
        bool? fromLimit = null;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "roof.direction": direction = (string?)tag.Value; break;
                case "roof.stop.reason": reason = (string?)tag.Value; break;
                case "roof.travel.from_limit": fromLimit = (bool?)tag.Value; break;
            }
        }

        lock (_samples)
        {
            _samples.Add((instrument.Name, new TimingSample(Math.Round(value, 6), direction, reason, fromLimit)));
        }
    }
}
