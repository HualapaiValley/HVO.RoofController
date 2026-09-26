using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The safety watchdog is an absolute movement cap measured from motion start. All tests run on a
/// <see cref="ManualTimeProvider"/>, so no test depends on wall-clock timing.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RoofControllerWatchdogTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time, CapturingLogger<RoofControllerServiceV4> Logger)> CreateInitializedAsync(
        Action<RoofControllerOptionsV4>? configure = null)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel
        var time = new ManualTimeProvider();
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        var service = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = Watchdog;
            configure?.Invoke(opts);
        }, logger);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (service, hat, time, logger);
    }

    [TestMethod]
    public async Task WatchdogTimeoutWhileOpening_ShouldStopLatchAndPublishSafetyTelemetry()
    {
        var (svc, hat, time, logger) = await CreateInitializedAsync();
        using var _ = svc;

        var safetyStops = new List<(string? Reason, string? Source)>();
        var gate = new object();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi" && instrument.Name == "roof.controller.safety.stops")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? reason = null, source = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "roof.stop.reason") reason = tag.Value?.ToString();
                else if (tag.Key == "roof.stop.source") source = tag.Value?.ToString();
            }

            lock (gate)
            {
                safetyStops.Add((reason, source));
            }
        });
        meterListener.Start();

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);
        var startTransition = svc.LastTransitionUtc;

        time.Advance(Watchdog - TimeSpan.FromMilliseconds(1));
        svc.IsMoving.Should().BeTrue("the watchdog has not elapsed yet");
        svc.WatchdogSecondsRemaining.Should().BeApproximately(0.001, 0.0005);

        time.Advance(TimeSpan.FromMilliseconds(1));

        svc.IsMoving.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Error, "a watchdog stop latches a safety fault");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.LastTransitionUtc.Should().NotBe(startTransition);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        logger.Contains(LogLevel.Warning, "Safety watchdog TRIGGERED").Should().BeTrue();

        lock (gate)
        {
            safetyStops.Should().Contain((RoofControllerStopReason.SafetyWatchdogTimeout.ToString(), "watchdog"));
        }

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched, "motion stays refused until ClearFault");
    }

    [TestMethod]
    public async Task WatchdogTimeoutWhileClosing_ShouldStopRoof()
    {
        var (svc, hat, time, _) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Close().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closing);

        time.Advance(Watchdog);

        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedMovementCommand_ShouldNotExtendTheAbsoluteWatchdogCap(bool close)
    {
        var (svc, hat, time, _) = await CreateInitializedAsync();
        using var __ = svc;

        (close ? svc.Close() : svc.Open()).IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(3));

        var generation = svc.CurrentMotionGeneration;
        (close ? svc.Close() : svc.Open()).IsSuccessful.Should().BeTrue("a repeat command is accepted");
        svc.CurrentMotionGeneration.Should().Be(generation, "a repeat command does not restart motion");
        svc.WatchdogSecondsRemaining.Should().BeApproximately(7, 0.001, "the watchdog deadline is measured from motion start");

        time.Advance(TimeSpan.FromSeconds(7));

        svc.IsMoving.Should().BeFalse("the cap is absolute: 10 s after the first command, not after the repeat");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task DirectionChange_ShouldCreateFreshWatchdogDeadline()
    {
        var (svc, _, time, _) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(3));

        svc.Close().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(8));

        svc.Status.Should().Be(RoofControllerStatus.Closing, "the reversal started a new motion with a new cap");
        svc.IsWatchdogActive.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(2));
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task StaleWatchdogCallback_FromEarlierMotion_ShouldNotStopRoof()
    {
        var (svc, _, _, _) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        var staleGeneration = svc.CurrentMotionGeneration;
        svc.Stop().IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.CurrentMotionGeneration.Should().NotBe(staleGeneration);

        svc.TriggerWatchdog(staleGeneration);

        svc.Status.Should().Be(RoofControllerStatus.Opening);
        svc.IsWatchdogActive.Should().BeTrue();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);

        svc.TriggerWatchdog(svc.CurrentMotionGeneration);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout, "the current generation's callback stops motion");
    }

    [TestMethod]
    public async Task ManualStop_ShouldCancelWatchdog()
    {
        var (svc, hat, time, _) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        time.ActiveTimerCount.Should().Be(1, "the watchdog timer is armed");
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        time.ActiveTimerCount.Should().Be(0, "the stop disposes the watchdog timer");

        time.Advance(Watchdog + Watchdog);

        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        svc.IsWatchdogActive.Should().BeFalse();
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task LostWatchdogCallback_ShouldBeCaughtBySupervisionBackstop()
    {
        var (svc, hat, time, logger) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        time.SuppressTimerCallbacks = true; // the timer "fires" but its callback is lost
        time.Advance(Watchdog);
        svc.IsMoving.Should().BeTrue("the timer callback was lost");

        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        hat.RelayMask.Should().Be(0x00);
        logger.Contains(LogLevel.Warning, "supervision backstop").Should().BeTrue();
    }

    [TestMethod]
    public async Task WatchdogLatch_ShouldBeResetOnlyByClearFault()
    {
        var (svc, hat, time, _) = await CreateInitializedAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        time.Advance(Watchdog);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();

        // Stop, a supervision cycle and a refresh do not reset the latch.
        svc.Stop().IsSuccessful.Should().BeTrue();
        svc.RunSupervisionCycle();
        svc.RefreshStatus(forceHardwareRead: true);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        var clear = svc.ClearFault(RoofControllerLimits.MinClearFaultPulseMilliseconds, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(RoofControllerLimits.MinClearFaultPulseMilliseconds));
        (await clear.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeTrue();

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeFalse();
        snapshot.LatchedFaultReason.Should().BeNull();
        snapshot.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout, "the stop reason is history, not the latch");
        svc.Close().IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x0A);
    }
}
