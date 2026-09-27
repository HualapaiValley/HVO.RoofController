using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// <see cref="RoofControllerOptionsV4.DepartureReleaseTimeout"/>: motion that starts on a limit must see that limit's
/// release verified (held for the debounce window) within the timeout, or it stops with a latched
/// <see cref="RoofControllerStopReason.DepartureLimitNotReleased"/>. NC limit wiring (the test default): raw LOW = active.
/// </summary>
[TestClass]
public class RoofControllerDepartureTimeoutTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private sealed record Rig(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time) : IDisposable
    {
        public void MidTravel() => Hat.SetInputs(true, true, false, false);

        public void Cycle() => Service.RunSupervisionCycle();

        public void Dispose() => Service.Dispose();
    }

    private static async Task<Rig> StartAsync(bool opening, bool onLimit = true, TimeSpan? timeout = null, bool configure = true)
    {
        var hat = new FakeRoofHat();
        if (onLimit)
        {
            // Opening departs from the closed limit (IN2), closing from the open limit (IN1).
            hat.SetInputs(opening, !opening, false, false);
        }
        else
        {
            hat.SetInputs(true, true, false, false);
        }

        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            opts.PeriodicVerificationInterval = TimeSpan.FromSeconds(1);
            opts.LimitSwitchDebounce = Debounce;
            if (configure)
            {
                opts.DepartureReleaseTimeout = timeout ?? Timeout;
            }
        });
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        (opening ? service.Open() : service.Close()).IsSuccessful.Should().BeTrue();
        return new Rig(service, hat, time);
    }

    [TestMethod]
    [DataRow(true, "closed")]
    [DataRow(false, "open")]
    public async Task LimitThatNeverReleases_StopsAndLatchesAtTheTimeout(bool opening, string limitName)
    {
        using var rig = await StartAsync(opening);

        rig.Time.Advance(Timeout - Ms);
        rig.Cycle();
        rig.Service.IsMoving.Should().BeTrue();

        rig.Time.Advance(Ms);
        rig.Cycle();

        rig.Service.IsMoving.Should().BeFalse();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        var snapshot = rig.Service.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        snapshot.LastError.Should().Contain($"The {limitName} limit did not release within 3 s");
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task LimitThatNeverReleases_IsStoppedByARepeatedCommandAfterTheTimeout()
    {
        using var rig = await StartAsync(opening: true);
        rig.Time.Advance(Timeout);

        rig.Service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReleaseVerifiedInTime_DisarmsTheTimeout(bool opening)
    {
        using var rig = await StartAsync(opening);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.MidTravel();
        rig.Cycle();                 // release observed
        rig.Time.Advance(Debounce);
        rig.Cycle();                 // release verified

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Cycle();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(opening ? (byte)0x09 : (byte)0x0A);
    }

    [TestMethod]
    public async Task ReleaseNotYetHeldForTheDebounce_AtTheTimeout_Stops()
    {
        using var rig = await StartAsync(opening: true);

        rig.Time.Advance(Timeout - TimeSpan.FromMilliseconds(50));
        rig.MidTravel();
        rig.Cycle();                 // release observed 50 ms before the timeout, not verified by it
        rig.Time.Advance(TimeSpan.FromMilliseconds(50));
        rig.Cycle();

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.DepartureLimitNotReleased);
    }

    [TestMethod]
    public async Task StartAwayFromTheLimits_DoesNotArmTheTimeout()
    {
        using var rig = await StartAsync(opening: true, onLimit: false);

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Cycle();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromSeconds(1), "only the periodic verification is pending");
    }

    [TestMethod]
    public async Task WithoutTheTimeout_ALimitThatNeverReleasesKeepsMoving()
    {
        using var rig = await StartAsync(opening: true, configure: false);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        rig.Cycle();

        rig.Service.IsMoving.Should().BeTrue("the watchdog is the only cap without the departure timeout");
    }

    [TestMethod]
    public async Task SupervisionDelay_IncludesTheDepartureDeadline()
    {
        using var rig = await StartAsync(opening: true, timeout: TimeSpan.FromMilliseconds(800));

        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromMilliseconds(800));
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [TestMethod]
    public async Task ObservedRelease_WakesTheSupervisionLoop_ToVerifyItAfterTheDebounce()
    {
        using var rig = await StartAsync(opening: true, timeout: TimeSpan.FromSeconds(10));
        var wakes = rig.Service.SupervisionWakeCount;

        rig.MidTravel();
        rig.Service.SimReverseLimitRaw(true);

        rig.Service.SupervisionWakeCount.Should().Be(wakes + 1);
        rig.Service.GetSupervisionDelay().Should().Be(Debounce);

        rig.Time.Advance(Debounce);
        rig.Cycle();
        rig.Service.SupervisionWakeCount.Should().Be(wakes + 1, "the release is already being timed");
        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromSeconds(1), "the release is verified; only the periodic verification is pending");
    }

    [TestMethod]
    public async Task ClearFault_AllowsAnotherAttempt_WithAFreshTimeout()
    {
        using var rig = await StartAsync(opening: true);
        rig.Time.Advance(Timeout);
        rig.Cycle();
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();

        var clear = rig.Service.ClearFault(pulseMs: 100);
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        (await clear.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeTrue();
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();

        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Time.Advance(Timeout - Ms);
        rig.Cycle();
        rig.Service.IsMoving.Should().BeTrue("the timeout restarts with the new command");
    }

    [TestMethod]
    public async Task DepartureStop_IsCountedAsALimitsSafetyStop()
    {
        var stops = new List<(string? Reason, string? Source)>();
        var gate = new object();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi" && instrument.Name == "roof.controller.safety.stops")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? reason = null, source = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "roof.stop.reason") reason = tag.Value?.ToString();
                if (tag.Key == "roof.stop.source") source = tag.Value?.ToString();
            }

            lock (gate)
            {
                stops.Add((reason, source));
            }
        });
        listener.Start();

        using var rig = await StartAsync(opening: true);
        rig.Time.Advance(Timeout);
        rig.Cycle();

        lock (gate)
        {
            stops.Should().Contain((nameof(RoofControllerStopReason.DepartureLimitNotReleased), "limits"));
        }
    }
}
