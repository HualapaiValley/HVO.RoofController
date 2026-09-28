using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The roof meter as the OpenTelemetry SDK exports it: the motion timing histograms carry the bucket boundaries the
/// logging and telemetry reference (<c>docs/telemetry.md</c>, Buckets) documents, not the SDK's defaults.
/// </summary>
[TestClass]
public class RoofControllerTelemetryExportTests
{
    [TestMethod]
    public void TheMotionTimingHistograms_ExportTheBucketsTheTelemetryReferenceDocuments()
    {
        var exported = new Dictionary<string, double[]>();
        using (var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(RoofControllerTelemetry.InstrumentationName)
            .AddReader(new BaseExportingMetricReader(new BoundaryExporter(exported)))
            .Build())
        {
            RoofControllerTelemetry.RecordTravel(RoofMotionDirection.Opening, RoofControllerStopReason.LimitSwitchReached, true, TimeSpan.FromSeconds(20));
            RoofControllerTelemetry.RecordDriveStartDelay(RoofMotionDirection.Opening, TimeSpan.FromMilliseconds(100));
            RoofControllerTelemetry.RecordDriveStopDelay(RoofMotionDirection.Opening, RoofControllerStopReason.NormalStop, TimeSpan.FromSeconds(1));
            RoofControllerTelemetry.RecordDepartureRelease(RoofMotionDirection.Opening, TimeSpan.FromMilliseconds(500));
            provider.ForceFlush().Should().BeTrue();
        }

        var documented = DocumentedBuckets();
        documented.Keys.Should().BeEquivalentTo(
            "roof.controller.travel.duration", "roof.controller.drive.start_delay", "roof.controller.drive.stop_delay", "roof.controller.departure.release");
        foreach (var (instrument, boundaries) in documented)
        {
            exported.Should().ContainKey(instrument);
            exported[instrument].Should().Equal(boundaries, "{0} exports the documented buckets", instrument);
        }
    }

    /// <summary>The Buckets table of docs/telemetry.md: each row names histograms (without the <c>roof.controller.</c> prefix) and their boundaries.</summary>
    private static Dictionary<string, double[]> DocumentedBuckets()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "..", "..", "docs", "telemetry.md"));
        var lines = File.ReadAllLines(path);
        var table = lines.SkipWhile(l => l != "### Buckets").Skip(1).SkipWhile(l => !l.StartsWith('|')).TakeWhile(l => l.StartsWith('|')).Skip(2);
        var buckets = new Dictionary<string, double[]>();
        foreach (var row in table)
        {
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            var boundaries = cells[2].Split(',', StringSplitOptions.TrimEntries).Select(b => double.Parse(b, CultureInfo.InvariantCulture)).ToArray();
            foreach (Match name in Regex.Matches(cells[1], "`([^`]+)`"))
            {
                buckets.Add($"roof.controller.{name.Groups[1].Value}", boundaries);
            }
        }

        return buckets;
    }

    /// <summary>Keeps each exported histogram's explicit bucket boundaries (without the implicit +Inf bucket).</summary>
    private sealed class BoundaryExporter(Dictionary<string, double[]> boundaries) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram)
                {
                    continue;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var bounds = new List<double>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                        {
                            bounds.Add(bucket.ExplicitBound);
                        }
                    }

                    boundaries[metric.Name] = [.. bounds];
                    break;
                }
            }

            return ExportResult.Success;
        }
    }
}
