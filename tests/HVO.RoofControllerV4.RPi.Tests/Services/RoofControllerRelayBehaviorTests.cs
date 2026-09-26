using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Relay sequencing and edge-driven behaviour. Relay "verification" here is register read-back from the in-memory HAT;
/// on hardware it proves the HAT register, not the relay contacts.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RoofControllerRelayBehaviorTests
{
    private const byte SetRegister = 0x01;
    private const byte ClearRegister = 0x02;
    private const int OpenRelayIndex = 1;
    private const int CloseRelayIndex = 2;
    private const int ClearFaultRelayIndex = 3;
    private const int StopRelayIndex = 4;

    private static readonly byte StopPlusOpenMask = (byte)((1 << (StopRelayIndex - 1)) | (1 << (OpenRelayIndex - 1)));
    private static readonly byte StopPlusCloseMask = (byte)((1 << (StopRelayIndex - 1)) | (1 << (CloseRelayIndex - 1)));

    /// <summary>Stop: direction relays first, then the STOP permit, then the clear-fault relay.</summary>
    private static readonly (byte, byte)[] StopSequence =
    {
        (ClearRegister, OpenRelayIndex),
        (ClearRegister, CloseRelayIndex),
        (ClearRegister, StopRelayIndex),
        (ClearRegister, ClearFaultRelayIndex)
    };

    /// <summary>Open: the opposite direction is cleared (and verified) first, then STOP permit, then the direction.</summary>
    private static readonly (byte, byte)[] OpenSequence =
    {
        (ClearRegister, CloseRelayIndex),
        (SetRegister, StopRelayIndex),
        (SetRegister, OpenRelayIndex)
    };

    private static readonly (byte, byte)[] CloseSequence =
    {
        (ClearRegister, OpenRelayIndex),
        (SetRegister, StopRelayIndex),
        (SetRegister, CloseRelayIndex)
    };

    private sealed class TestableRoofControllerService : RoofControllerServiceV4
    {
        public TestableRoofControllerService(IOptions<RoofControllerOptionsV4> opts, FourRelayFourInputHat hat, TimeProvider? timeProvider = null)
            : base(new NullLogger<RoofControllerServiceV4>(), opts, hat, null, timeProvider)
        {
            EnableBackgroundSupervision = false;
        }

        // Expose protected handlers for deterministic event simulation
        public void SimForwardLimitRaw(bool high) => OnForwardLimitSwitchChanged(high);
        public void SimReverseLimitRaw(bool high) => OnReverseLimitSwitchChanged(high);
        public void SimFaultRaw(bool high) => OnFaultNotificationChanged(high);
        public void SimAtSpeedRaw(bool high) => OnAtSpeedChanged(high);
    }

    private static TestableRoofControllerService Create(FakeRoofHat hat, TimeSpan? watchdog = null, TimeSpan? debounce = null, TimeProvider? timeProvider = null)
    {
        var options = RoofControllerTestFactory.CreateWrappedOptions(opts =>
        {
            opts.SafetyWatchdogTimeout = watchdog ?? TimeSpan.FromSeconds(10);
            opts.LimitSwitchDebounce = debounce ?? TimeSpan.FromMilliseconds(25);
            opts.OpenRelayId = OpenRelayIndex;
            opts.CloseRelayId = CloseRelayIndex;
            opts.ClearFaultRelayId = ClearFaultRelayIndex;
            opts.StopRelayId = StopRelayIndex;
        });
        return new TestableRoofControllerService(options, hat, timeProvider);
    }

    private static (byte, byte)[] Last(IReadOnlyList<(byte Register, byte Value)> log, int count)
        => log.Skip(Math.Max(0, log.Count - count)).Select(e => (e.Register, e.Value)).ToArray();

    [TestMethod]
    public async Task IdlePowerUp_ShouldReflectRelaySafeState_AndStatusMatchesLimits()
    {
        // Scenario 1: Mid-travel (both HIGH) -> expect Stopped
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00, "all relays de-energized (STOP permit released) at idle");
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
        svc.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);

        // Scenario 2: Open limit engaged (IN1 LOW, IN2 HIGH)
        var hat2 = new FakeRoofHat();
        hat2.SetInputs(false, true, false, false);
        using var svc2 = Create(hat2);
        (await svc2.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc2.Status.Should().Be(RoofControllerStatus.Open);
        hat2.RelayMask.Should().Be(0x00);

        // Scenario 3: Closed limit engaged (IN1 HIGH, IN2 LOW)
        var hat3 = new FakeRoofHat();
        hat3.SetInputs(true, false, false, false);
        using var svc3 = Create(hat3);
        (await svc3.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc3.Status.Should().Be(RoofControllerStatus.Closed);
        hat3.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task DestinationLimitChatter_AfterStop_ShouldNotIssueFurtherStopsOrEnergize()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat, debounce: TimeSpan.FromMilliseconds(30));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        var preStopCount = svc.StopSequenceCount;

        // The destination limit is acted on immediately (no debounce on the stop path).
        hat.SetInputs(false, true, false, false);
        svc.SimForwardLimitRaw(false);
        svc.StopSequenceCount.Should().Be(preStopCount + 1);
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // Chatter after the stop: the limit releases briefly and asserts again.
        svc.SimForwardLimitRaw(true);
        svc.SimForwardLimitRaw(false);

        svc.StopSequenceCount.Should().Be(preStopCount + 1, "limit chatter while idle must not run further stop sequences");
        svc.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Open);
    }

    [TestMethod]
    public async Task StopCommand_ShouldDropDirectionBeforeDisableStopRelay()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);

        hat.ClearRelayWriteLog();

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        Last(hat.RelayWriteLog, StopSequence.Length).Should().Equal(StopSequence,
            "Stop must drop the direction relays before releasing the STOP permit");

        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        svc.GetCurrentStatusSnapshot().RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
    }

    [TestMethod]
    public async Task BothLimitGlitch_WhileMoving_ShouldStopOnceLatchAndDropAllRelays()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel baseline
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var initialStatus = svc.Status;
        using var recorder = new StatusChangeRecorder(svc);

        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask);
        var stopsBefore = svc.StopSequenceCount;

        hat.ClearRelayWriteLog();

        // Glitch: both limits read active at once (NC switches pull low), seen by a direct read.
        hat.SetInputs(false, false, false, false);
        svc.ForceStatusRefresh(true);

        // Further edges for the same glitch must not produce another stop or Error transition.
        svc.SimForwardLimitRaw(false);
        svc.SimReverseLimitRaw(false);

        // Drain the dispatcher so any extra (erroneous) transition would have been observed.
        (await recorder.DrainAsync()).Should().BeTrue();

        recorder.TransitionsInto(RoofControllerStatus.Error, initialStatus).Should().Be(1, "the Error state is entered exactly once for the glitch");
        svc.StopSequenceCount.Should().Be(stopsBefore + 1);
        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        hat.RelayMask.Should().Be(0x00, "all relays must be de-energized after the glitch stop");

        Last(hat.RelayWriteLog, StopSequence.Length).Should().Equal(StopSequence,
            "the glitch stop must drop direction relays before releasing the STOP permit");
    }

    [TestMethod]
    public async Task OpenSequence_ShouldClearOppositeFirst_ThenStopPermit_ThenOpen_AndDropAtLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask, "Stop + Open relays energized");
        svc.Status.Should().Be(RoofControllerStatus.Opening);
        hat.RelayWriteLog.Select(e => (e.Register, e.Value)).Should().Equal(OpenSequence,
            "Open must verify Close off before asserting STOP, then assert Open");

        // Simulate limit reached: raw LOW on IN1 for NC
        hat.ClearRelayWriteLog();
        hat.SetInputs(false, true, false, false);
        svc.SimForwardLimitRaw(false);
        hat.RelayMask.Should().Be(0x00, "All relays de-energized after limit stop");
        svc.Status.Should().Be(RoofControllerStatus.Open);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        hat.RelayWriteLog.Select(e => (e.Register, e.Value)).Should().Equal(StopSequence);

        svc.ForceStatusRefresh(true);
        svc.Status.Should().Be(RoofControllerStatus.Open);
    }

    [TestMethod]
    public async Task CloseSequence_ShouldClearOppositeFirst_ThenStopPermit_ThenClose_AndDropAtLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask, "Stop + Close relays energized");
        svc.Status.Should().Be(RoofControllerStatus.Closing);
        hat.RelayWriteLog.Select(e => (e.Register, e.Value)).Should().Equal(CloseSequence,
            "Close must verify Open off before asserting STOP, then assert Close");

        hat.ClearRelayWriteLog();
        hat.SetInputs(true, false, false, false); // closed limit engaged
        svc.SimReverseLimitRaw(false);
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Closed);
        hat.RelayWriteLog.Select(e => (e.Register, e.Value)).Should().Equal(StopSequence);
    }

    [TestMethod]
    public async Task LimitStop_ShouldRecordBoundedSafetyStopMetric()
    {
        var safetyStops = new List<(string? Reason, string? Source)>();
        var limitTransitions = new List<(string? Switch, string? State)>();
        var gate = new object();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi"
                && instrument.Name is "roof.controller.safety.stops" or "roof.controller.limit.switch.events")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? reason = null, source = null, limitSwitch = null, limitState = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "roof.stop.reason": reason = tag.Value?.ToString(); break;
                    case "roof.stop.source": source = tag.Value?.ToString(); break;
                    case "roof.limit.switch": limitSwitch = tag.Value?.ToString(); break;
                    case "roof.limit.state": limitState = tag.Value?.ToString(); break;
                }
            }

            lock (gate)
            {
                if (reason is not null)
                {
                    safetyStops.Add((reason, source));
                }
                else if (limitSwitch is not null)
                {
                    limitTransitions.Add((limitSwitch, limitState));
                }
            }
        });
        meterListener.Start();

        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(false, true, false, false);
        service.SimForwardLimitRaw(false);

        var closedHat = new FakeRoofHat();
        closedHat.SetInputs(true, true, false, false);
        using var closedService = Create(closedHat);
        (await closedService.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        closedService.Close().IsSuccessful.Should().BeTrue();

        closedHat.SetInputs(true, false, false, false);
        closedService.SimReverseLimitRaw(false);

        lock (gate)
        {
            safetyStops.Should().Contain((RoofControllerStopReason.LimitSwitchReached.ToString(), "open-limit"));
            limitTransitions.Should().Contain(("open", "reached"));
            safetyStops.Should().Contain((RoofControllerStopReason.LimitSwitchReached.ToString(), "closed-limit"));
            limitTransitions.Should().Contain(("closed", "reached"));
        }
    }

    [TestMethod]
    public async Task FaultAndWatchdogStops_ShouldRecordDistinctTelemetrySources()
    {
        var safetyStops = new List<(string? Reason, string? Source)>();
        var faultStates = new List<string?>();
        var gate = new object();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi"
                && instrument.Name is "roof.controller.safety.stops" or "roof.controller.fault.events")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? reason = null, source = null, faultState = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "roof.stop.reason": reason = tag.Value?.ToString(); break;
                    case "roof.stop.source": source = tag.Value?.ToString(); break;
                    case "roof.fault.state": faultState = tag.Value?.ToString(); break;
                }
            }

            lock (gate)
            {
                if (instrument.Name == "roof.controller.safety.stops")
                {
                    safetyStops.Add((reason, source));
                }
                else if (faultState is not null)
                {
                    faultStates.Add(faultState);
                }
            }
        });
        meterListener.Start();

        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(true, true, true, false);
        service.SimFaultRaw(true);

        var time = new ManualTimeProvider();
        var watchdogHat = new FakeRoofHat();
        watchdogHat.SetInputs(true, true, false, false);
        using var watchdogService = Create(watchdogHat, watchdog: TimeSpan.FromSeconds(10), timeProvider: time);
        (await watchdogService.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        watchdogService.Open().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10));
        watchdogService.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);

        lock (gate)
        {
            safetyStops.Should().Contain((RoofControllerStopReason.DriveFault.ToString(), "fault"));
            faultStates.Should().Contain("active");
            safetyStops.Should().Contain((RoofControllerStopReason.SafetyWatchdogTimeout.ToString(), "watchdog"));
        }
    }

    [TestMethod]
    public async Task ControllerStateGauges_ShouldPublishLiveSafetyState()
    {
        var longMeasurements = new List<(string Name, long Value, string? TagValue)>();
        var doubleMeasurements = new List<(string Name, double Value)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi"
                && instrument.Name is "roof.controller.limit.switch.state"
                    or "roof.controller.fault.active"
                    or "roof.controller.watchdog.active"
                    or "roof.controller.watchdog.remaining"
                    or "roof.controller.drive.at_speed"
                    or "roof.controller.status")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? tagValue = null;
            foreach (var tag in tags)
            {
                tagValue = tag.Value?.ToString();
            }

            longMeasurements.Add((instrument.Name, measurement, tagValue));
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            doubleMeasurements.Add((instrument.Name, measurement));
        });
        meterListener.Start();

        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        service.SimAtSpeedRaw(true);

        // The gauges are process-wide; another (leaked) controller instance could overwrite them between our refresh
        // and the observation, so re-publish and observe a few times.
        ObserveUntil(meterListener, service, longMeasurements, doubleMeasurements, () =>
            longMeasurements.Contains(("roof.controller.limit.switch.state", 0, "open"))
            && longMeasurements.Contains(("roof.controller.limit.switch.state", 0, "closed"))
            && longMeasurements.Contains(("roof.controller.fault.active", 0, null))
            && longMeasurements.Contains(("roof.controller.watchdog.active", 1, null))
            && longMeasurements.Contains(("roof.controller.drive.at_speed", 1, null))
            && longMeasurements.Contains(("roof.controller.status", 1, RoofControllerStatus.Opening.ToString()))
            && doubleMeasurements.Any(m => m.Name == "roof.controller.watchdog.remaining" && m.Value > 0 && m.Value <= 10))
            .Should().BeTrue("the gauges must reflect the moving, at-speed controller");

        hat.SetInputs(true, true, true, true);
        service.SimFaultRaw(true);

        ObserveUntil(meterListener, service, longMeasurements, doubleMeasurements, () =>
            longMeasurements.Contains(("roof.controller.fault.active", 1, null))
            && longMeasurements.Contains(("roof.controller.status", 1, RoofControllerStatus.Error.ToString())))
            .Should().BeTrue("the gauges must reflect the active drive fault");
    }

    private static bool ObserveUntil(
        MeterListener listener,
        TestableRoofControllerService service,
        List<(string Name, long Value, string? TagValue)> longs,
        List<(string Name, double Value)> doubles,
        Func<bool> condition)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            longs.Clear();
            doubles.Clear();
            service.ForceStatusRefresh(true);
            listener.RecordObservableInstruments();
            if (condition())
            {
                return true;
            }
        }

        return false;
    }

    [TestMethod]
    public async Task OpenCommand_WhenAlreadyOpening_ShouldAvoidRedundantWrites()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        svc.Open().IsSuccessful.Should().BeTrue();
        var writesAfterFirst = hat.RelayWriteLog.Count;
        writesAfterFirst.Should().BeGreaterThan(0, "First open should issue relay commands");
        var stopCountAfterFirst = svc.StopSequenceCount;
        svc.IsWatchdogActive.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue("Second open while opening should succeed");

        hat.RelayWriteLog.Count.Should().Be(writesAfterFirst, "Second open should not issue additional relay writes");
        svc.StopSequenceCount.Should().Be(stopCountAfterFirst, "Second open should not run a stop sequence");
        svc.IsWatchdogActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task CloseCommand_WhenAlreadyClosing_ShouldAvoidRedundantWrites()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        svc.Close().IsSuccessful.Should().BeTrue();
        var writesAfterFirst = hat.RelayWriteLog.Count;
        writesAfterFirst.Should().BeGreaterThan(0, "First close should issue relay commands");
        var stopCountAfterFirst = svc.StopSequenceCount;
        svc.IsWatchdogActive.Should().BeTrue();

        svc.Close().IsSuccessful.Should().BeTrue("Second close while closing should succeed");

        hat.RelayWriteLog.Count.Should().Be(writesAfterFirst, "Second close should not issue additional relay writes");
        svc.StopSequenceCount.Should().Be(stopCountAfterFirst, "Second close should not run a stop sequence");
        svc.IsWatchdogActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task Reversal_ShouldStopAndVerifyBeforeEnergizingTheOtherDirection()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();
        hat.ClearMaskHistory();

        svc.Close().IsSuccessful.Should().BeTrue();

        hat.RelayWriteLog.Select(e => (e.Register, e.Value)).Should().Equal(StopSequence.Concat(CloseSequence),
            "a reversal runs the full verified stop before the close sequence");
        hat.EverBothDirectionBits.Should().BeFalse();
        hat.RelayMask.Should().Be(StopPlusCloseMask);
        svc.Status.Should().Be(RoofControllerStatus.Closing);
    }

    [TestMethod]
    public async Task ManualStopMidTravel_ShouldDeenergizeRelaysAndSetPartialStatuses()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);

        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyClose);
    }

    [TestMethod]
    public async Task FaultTrip_ShouldStopMovement_Latch_AndRefuseCommandsUntilClearedWithInputInactive()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);

        // IN3 active (active-high default)
        hat.SetInputs(true, true, true, false);
        svc.SimFaultRaw(true);
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        // ClearFault while IN3 is still active pulses the relay but leaves the latch in place.
        var stillFaulted = await svc.ClearFault(50, CancellationToken.None);
        stillFaulted.ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00, "the clear-fault relay is released after the pulse");

        // The drive fault clears; ClearFault now resets the latch.
        hat.SetInputs(true, true, false, false);
        svc.SimFaultRaw(false);
        svc.Status.Should().Be(RoofControllerStatus.Error, "clearing IN3 alone does not reset the latch");

        (await svc.ClearFault(50, CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);

        svc.Open().IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task AtSpeedTransition_ShouldUpdateIsAtSpeedDuringMotion()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        using var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.IsAtSpeed.Should().BeFalse();

        hat.SetInputs(true, true, false, true); // IN4 HIGH
        svc.SimAtSpeedRaw(true);
        svc.ForceStatusRefresh(true);
        svc.IsAtSpeed.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening, "status remains Opening while motion is commanded");

        hat.SetInputs(false, true, false, true); // open limit engaged, at-speed remains TRUE
        svc.SimForwardLimitRaw(false);
        svc.Status.Should().Be(RoofControllerStatus.Open);
    }
}
