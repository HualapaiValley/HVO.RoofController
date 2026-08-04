using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
[DoNotParallelize]
public class RoofControllerRelayBehaviorTests
{
    private const int OpenRelayIndex = 1;
    private const int CloseRelayIndex = 2;
    private const int ClearFaultRelayIndex = 3;
    private const int StopRelayIndex = 4;

    private static readonly byte StopPlusOpenMask = (byte)((1 << (StopRelayIndex - 1)) | (1 << (OpenRelayIndex - 1)));
    private static readonly byte StopPlusCloseMask = (byte)((1 << (StopRelayIndex - 1)) | (1 << (CloseRelayIndex - 1)));
    private class TestableRoofControllerService : RoofControllerServiceV4
    {
        public int InternalStopCallCount { get; private set; }

        public TestableRoofControllerService(IOptions<RoofControllerOptionsV4> opts, FourRelayFourInputHat hat)
            : base(new NullLogger<RoofControllerServiceV4>(), opts, hat) { }

        // Expose protected handlers for deterministic event simulation
        public void SimForwardLimitRaw(bool high) => OnForwardLimitSwitchChanged(high);
        public void SimReverseLimitRaw(bool high) => OnReverseLimitSwitchChanged(high);
        public void SimFaultRaw(bool high) => OnFaultNotificationChanged(high);
        public void SimAtSpeedRaw(bool high) => OnAtSpeedChanged(high);

        protected override void InternalStop(RoofControllerStopReason reason = RoofControllerStopReason.None)
        {
            InternalStopCallCount++;
            base.InternalStop(reason);
        }

        public void TriggerSafetyWatchdog() => SafetyWatchdog_Elapsed(_safetyWatchdogTimer, null!);
    }

    private static TestableRoofControllerService Create(FakeRoofHat hat, TimeSpan? watchdog = null, TimeSpan? debounce = null)
    {
        var defaultDebounce = TimeSpan.FromMilliseconds(25);
        var options = RoofControllerTestFactory.CreateWrappedOptions(opts =>
        {
            opts.SafetyWatchdogTimeout = watchdog ?? TimeSpan.FromSeconds(10);
            opts.LimitSwitchDebounce = debounce ?? defaultDebounce;
            opts.OpenRelayId = OpenRelayIndex;
            opts.CloseRelayId = CloseRelayIndex;
            opts.ClearFaultRelayId = ClearFaultRelayIndex;
            opts.StopRelayId = StopRelayIndex;
        });
        return new TestableRoofControllerService(options, hat);
    }

    [TestMethod]
    public async Task IdlePowerUp_ShouldReflectRelaySafeState_AndStatusMatchesLimits()
    {
        var hat = new FakeRoofHat();
        // Scenario 1: Mid-travel (both HIGH) -> expect Stopped
        hat.SetInputs(true, true, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00, "all relays de-energized, STOP asserted (fail-safe) at idle");
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        // Scenario 2: Open limit engaged (IN1 LOW, IN2 HIGH)
        var hat2 = new FakeRoofHat();
        hat2.SetInputs(false, true, false, false);
        var svc2 = Create(hat2);
        (await svc2.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc2.Status.Should().Be(RoofControllerStatus.Open);
        hat2.RelayMask.Should().Be(0x00);

        // Scenario 3: Closed limit engaged (IN1 HIGH, IN2 LOW)
        var hat3 = new FakeRoofHat();
        hat3.SetInputs(true, false, false, false);
        var svc3 = Create(hat3);
        (await svc3.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc3.Status.Should().Be(RoofControllerStatus.Closed);
        hat3.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task LimitSwitchDebounce_ShouldIgnoreRapidRepeatedLimitEvents()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat, debounce: TimeSpan.FromMilliseconds(30));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        var preStopCount = svc.InternalStopCallCount;

        // First limit trip should count once
        hat.SetInputs(false, true, false, false);
        svc.SimForwardLimitRaw(false);
        svc.InternalStopCallCount.Should().Be(preStopCount + 1);
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // Simulate chatter: limit releases briefly within debounce window then asserts again
        await Task.Delay(10);
        svc.SimForwardLimitRaw(true);
        svc.Status.Should().Be(RoofControllerStatus.Open, "debounce should ignore flip-flop within window");

        svc.SimForwardLimitRaw(false);
        svc.InternalStopCallCount.Should().Be(preStopCount + 1, "debounce should filter rapid duplicate limit events");
    }

    [TestMethod]
    public async Task StopCommand_ShouldDropDirectionBeforeDisableStopRelay()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);

        hat.ClearRelayWriteLog();

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        var stopWrites = hat.RelayWriteLog;
        stopWrites.Should().NotBeNull();
        stopWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var stopSequence = stopWrites.Skip(Math.Max(0, stopWrites.Count - 3)).ToArray();
        stopSequence.Should().Equal(new[]
        {
            ((byte)0x02, (byte)OpenRelayIndex),
            ((byte)0x02, (byte)CloseRelayIndex),
            ((byte)0x02, (byte)StopRelayIndex)
        }, "Stop command should drop direction relays before disabling STOP");

        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
    }

    [TestMethod]
    public async Task BothLimitGlitch_ShouldTriggerErrorOnceAndDropAllRelays()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel baseline
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var errorTransitions = 0;
        var errorSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RoofStatusChangedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.Status.Status == RoofControllerStatus.Error)
            {
                Interlocked.Increment(ref errorTransitions);
                errorSignal.TrySetResult();
            }
        };
        svc.StatusChanged += handler;

        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask);

        hat.ClearRelayWriteLog();

        // Glitch: both limits report active momentarily (NC switches pull low)
        hat.SetInputs(false, false, false, false);
        svc.SimForwardLimitRaw(false);
        svc.SimReverseLimitRaw(false);

        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        errorTransitions.Should().Be(1, "Error state should be published only once during both-limit glitch");
        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.IsMoving.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00, "All relays must be de-energized after glitch stop");

        var stopWrites = hat.RelayWriteLog;
        stopWrites.Should().NotBeNull();
        stopWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var stopSequence = stopWrites.Skip(Math.Max(0, stopWrites.Count - 3)).ToArray();
        stopSequence.Should().Equal(new[]
        {
            ((byte)0x02, (byte)OpenRelayIndex),
            ((byte)0x02, (byte)CloseRelayIndex),
            ((byte)0x02, (byte)StopRelayIndex)
        }, "Both-limit glitch should drop direction relays before disabling STOP");

        svc.StatusChanged -= handler;
    }

    [TestMethod]
    public async Task OpenSequence_ShouldEnergizeStopAndOpenRelays_ThenDropAtLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        var openResult = svc.Open();
        openResult.IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask, "Stop + Open relays energized");
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        var openWrites = hat.RelayWriteLog;
        openWrites.Should().NotBeNull();
        openWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var openSequence = openWrites.Skip(Math.Max(0, openWrites.Count - 3)).ToArray();
        openSequence.Should().Equal(new[]
        {
            ((byte)0x01, (byte)StopRelayIndex),
            ((byte)0x02, (byte)CloseRelayIndex),
            ((byte)0x01, (byte)OpenRelayIndex)
        }, "Open command should enable STOP before establishing direction");

        // Simulate limit reached: raw LOW on IN1 for NC
        hat.ClearRelayWriteLog();
        hat.SetInputs(false, true, false, false); // hardware now shows open limit engaged
        svc.SimForwardLimitRaw(false);
        // Force a status refresh to ensure cached evaluation consistent in test context
        svc.ForceStatusRefresh(true);
        hat.RelayMask.Should().Be(0x00, "All relays de-energized after limit stop");
        svc.Status.Should().Be(RoofControllerStatus.Open);

        var stopWrites = hat.RelayWriteLog;
        stopWrites.Should().NotBeNull();
        stopWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var stopSequence = stopWrites.Skip(Math.Max(0, stopWrites.Count - 3)).ToArray();
        stopSequence.Should().Equal(new[]
        {
            ((byte)0x02, (byte)OpenRelayIndex),
            ((byte)0x02, (byte)CloseRelayIndex),
            ((byte)0x02, (byte)StopRelayIndex)
        }, "Limit stop should drop direction relays before de-energizing STOP");
    }

    [TestMethod]
    public async Task LimitStop_ShouldRecordBoundedSafetyStopMetric()
    {
        var safetyStops = new List<(string? Reason, string? Source)>();
        var limitTransitions = new List<(string? Switch, string? State)>();
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
            string? reason = null;
            string? source = null;
            string? limitSwitch = null;
            string? limitState = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "roof.stop.reason")
                {
                    reason = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.stop.source")
                {
                    source = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.limit.switch")
                {
                    limitSwitch = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.limit.state")
                {
                    limitState = tag.Value?.ToString();
                }
            }

            if (reason is not null)
            {
                safetyStops.Add((reason, source));
            }
            else if (limitSwitch is not null)
            {
                limitTransitions.Add((limitSwitch, limitState));
            }
        });
        meterListener.Start();

        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(false, true, false, false);
        service.SimForwardLimitRaw(false);

        safetyStops.Should().Contain((RoofControllerStopReason.LimitSwitchReached.ToString(), "open-limit"));
        limitTransitions.Should().Contain(("open", "reached"));

        var closedHat = new FakeRoofHat();
        closedHat.SetInputs(true, true, false, false);
        var closedService = Create(closedHat);
        (await closedService.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        closedService.Close().IsSuccessful.Should().BeTrue();

        closedHat.SetInputs(true, false, false, false);
        closedService.SimReverseLimitRaw(false);

        safetyStops.Should().Contain((RoofControllerStopReason.LimitSwitchReached.ToString(), "closed-limit"));
        limitTransitions.Should().Contain(("closed", "reached"));
    }

    [TestMethod]
    public async Task FaultAndWatchdogStops_ShouldRecordDistinctTelemetrySources()
    {
        var safetyStops = new List<(string? Reason, string? Source)>();
        var faultStates = new List<string?>();
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
            string? reason = null;
            string? source = null;
            string? faultState = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "roof.stop.reason")
                {
                    reason = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.stop.source")
                {
                    source = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.fault.state")
                {
                    faultState = tag.Value?.ToString();
                }
            }

            if (instrument.Name == "roof.controller.safety.stops")
            {
                safetyStops.Add((reason, source));
            }
            else if (faultState is not null)
            {
                faultStates.Add(faultState);
            }
        });
        meterListener.Start();

        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(true, true, true, false);
        service.SimFaultRaw(true);

        safetyStops.Should().Contain((RoofControllerStopReason.EmergencyStop.ToString(), "fault"));
        faultStates.Should().Contain("active");

        var watchdogHat = new FakeRoofHat();
        watchdogHat.SetInputs(true, true, false, false);
        var watchdogService = Create(watchdogHat);
        (await watchdogService.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        watchdogService.Open().IsSuccessful.Should().BeTrue();
        watchdogService.TriggerSafetyWatchdog();

        safetyStops.Should().Contain((RoofControllerStopReason.SafetyWatchdogTimeout.ToString(), "watchdog"));
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
        var service = Create(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        service.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        service.SimAtSpeedRaw(true);

        meterListener.RecordObservableInstruments();

        longMeasurements.Should().Contain(("roof.controller.limit.switch.state", 0, "open"));
        longMeasurements.Should().Contain(("roof.controller.limit.switch.state", 0, "closed"));
        longMeasurements.Should().Contain(("roof.controller.fault.active", 0, null));
        longMeasurements.Should().Contain(("roof.controller.watchdog.active", 1, null));
        longMeasurements.Should().Contain(("roof.controller.drive.at_speed", 1, null));
        longMeasurements.Should().Contain(("roof.controller.status", 1, RoofControllerStatus.Opening.ToString()));
        doubleMeasurements.Should().Contain(measurement =>
            measurement.Name == "roof.controller.watchdog.remaining"
            && measurement.Value > 0
            && measurement.Value <= 10);

        hat.SetInputs(true, true, true, true);
        service.SimFaultRaw(true);
        meterListener.RecordObservableInstruments();

        longMeasurements.Should().Contain(("roof.controller.fault.active", 1, null));
    }

    [TestMethod]
    public async Task OpenCommand_WhenAlreadyOpening_ShouldAvoidRedundantWrites()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        svc.Open().IsSuccessful.Should().BeTrue();
        var writesAfterFirst = hat.RelayWriteLog.Count;
        writesAfterFirst.Should().BeGreaterThan(0, "First open should issue relay commands");
        var stopCountAfterFirst = svc.InternalStopCallCount;
        svc.IsWatchdogActive.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue("Second open while opening should succeed");

        hat.RelayWriteLog.Count.Should().Be(writesAfterFirst, "Second open should not issue additional relay writes");
        svc.InternalStopCallCount.Should().Be(stopCountAfterFirst, "Second open should not call Stop");
        svc.IsWatchdogActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task CloseSequence_ShouldEnergizeStopAndCloseRelays_ThenDropAtLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        hat.ClearRelayWriteLog();
        var closeResult = svc.Close();
        closeResult.IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask, "Stop + Close relays energized");
        svc.Status.Should().Be(RoofControllerStatus.Closing);

        var closeWrites = hat.RelayWriteLog;
        closeWrites.Should().NotBeNull();
        closeWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var closeSequence = closeWrites.Skip(Math.Max(0, closeWrites.Count - 3)).ToArray();
        closeSequence.Should().Equal(new[]
        {
            ((byte)0x01, (byte)StopRelayIndex),
            ((byte)0x02, (byte)OpenRelayIndex),
            ((byte)0x01, (byte)CloseRelayIndex)
        }, "Close command should enable STOP before aligning direction");

        // Simulate reverse/closed limit reached: raw LOW on IN2
        hat.ClearRelayWriteLog();
        hat.SetInputs(true, false, false, false); // hardware closed limit engaged
        svc.SimReverseLimitRaw(false);
        svc.ForceStatusRefresh(true);
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Closed);

        var closeStopWrites = hat.RelayWriteLog;
        closeStopWrites.Should().NotBeNull();
        closeStopWrites.Count.Should().BeGreaterThanOrEqualTo(3);
        var closeStopSequence = closeStopWrites.Skip(Math.Max(0, closeStopWrites.Count - 3)).ToArray();
        closeStopSequence.Should().Equal(new[]
        {
            ((byte)0x02, (byte)OpenRelayIndex),
            ((byte)0x02, (byte)CloseRelayIndex),
            ((byte)0x02, (byte)StopRelayIndex)
        }, "Close limit stop should drop direction before disabling STOP");
    }

    [TestMethod]
    public async Task CloseCommand_WhenAlreadyClosing_ShouldAvoidRedundantWrites()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        hat.ClearRelayWriteLog();
        svc.Close().IsSuccessful.Should().BeTrue();
        var writesAfterFirst = hat.RelayWriteLog.Count;
        writesAfterFirst.Should().BeGreaterThan(0, "First close should issue relay commands");
        var stopCountAfterFirst = svc.InternalStopCallCount;
        svc.IsWatchdogActive.Should().BeTrue();

        svc.Close().IsSuccessful.Should().BeTrue("Second close while closing should succeed");

        // Assert
        hat.RelayWriteLog.Count.Should().Be(writesAfterFirst, "Second close should not issue additional relay writes");
        svc.InternalStopCallCount.Should().Be(stopCountAfterFirst, "Second close should not call Stop");
        svc.IsWatchdogActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task ManualStopMidTravel_ShouldDeenergizeRelaysAndSetPartialStatuses()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);

        // Now issue Close then manual stop
        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusCloseMask);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.PartiallyClose);
    }

    [TestMethod]
    public async Task FaultTrip_ShouldStopMovement_SetError_AndRefuseCommandsUntilCleared()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(StopPlusOpenMask);

        // Simulate fault raw HIGH on IN3 (update hardware first)
        hat.SetInputs(true, true, true, false);
        svc.SimFaultRaw(true);
        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Error);

        // Further movement commands should fail while fault active
        svc.Open().IsSuccessful.Should().BeFalse();
        svc.Close().IsSuccessful.Should().BeFalse();

        // ClearFault pulses relay 4 (ClearFault id). Provide short pulse.
        var clearResult = await svc.ClearFault(50, CancellationToken.None);
        clearResult.IsSuccessful.Should().BeTrue();
        // After clearing fault, clear hardware fault bit then simulate raw LOW transition
        hat.SetInputs(true, true, false, false); // mid-travel, fault cleared
        svc.SimFaultRaw(false); // fault cleared raw LOW
        var reopen = svc.Open();
        reopen.IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task AtSpeedTransition_ShouldUpdateIsAtSpeedDuringMotion()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.IsAtSpeed.Should().BeFalse();

        // Simulate at-speed raw HIGH transition (set hardware input and invoke handler)
        hat.SetInputs(true, true, false, true); // set IN4 HIGH in hardware
        svc.SimAtSpeedRaw(true);
        // Force refresh to read hardware if needed
        svc.ForceStatusRefresh(true);
        svc.IsAtSpeed.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening, "status remains Opening while watchdog active");

        // Simulate reaching open limit
        hat.SetInputs(false, true, false, true); // open limit engaged, at-speed remains TRUE
        svc.SimForwardLimitRaw(false);
        svc.Status.Should().Be(RoofControllerStatus.Open);
    }
}
