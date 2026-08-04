using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
[DoNotParallelize]
public class RoofControllerWatchdogTests
{
    private sealed class TestableRoofControllerService : RoofControllerServiceV4
    {
        public TestableRoofControllerService(IOptions<RoofControllerOptionsV4> options, FakeRoofHat hat)
            : base(new NullLogger<RoofControllerServiceV4>(), options, hat)
        {
        }

        public object? CurrentWatchdogTimer => _safetyWatchdogTimer;

        public void TriggerWatchdogCallback(object? sender) => SafetyWatchdog_Elapsed(sender, null!);
    }

    private TestableRoofControllerService CreateService(FakeRoofHat hat, TimeSpan watchdog)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts => opts.SafetyWatchdogTimeout = watchdog);
        return new TestableRoofControllerService(Options.Create(options), hat);
    }

    [TestMethod]
    public async Task WatchdogTimeoutWhileOpening_ShouldStopRoofAndPublishSafetyTelemetry()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromMilliseconds(150));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var safetyStops = new List<(string? Reason, string? Source)>();
        var watchdogStates = new List<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi"
                && instrument.Name is "roof.controller.safety.stops" or "roof.controller.watchdog.active")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "roof.controller.watchdog.active")
            {
                watchdogStates.Add(measurement);
                return;
            }

            string? reason = null;
            string? source = null;
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
            }

            safetyStops.Add((reason, source));
        });
        meterListener.Start();

        var errorSignal = CreateStatusSignal(svc, RoofControllerStatus.Error, out var handler);
        var result = svc.Open();
        result.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);
        var startTransition = svc.LastTransitionUtc;

        try
        {
            await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            svc.StatusChanged -= handler;
        }

        svc.Status.Should().Be(RoofControllerStatus.Error, "watchdog should force error state");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.LastTransitionUtc.Should().NotBeNull();
        svc.LastTransitionUtc.Should().NotBe(startTransition);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
        safetyStops.Should().Contain((RoofControllerStopReason.SafetyWatchdogTimeout.ToString(), "watchdog"));

        meterListener.RecordObservableInstruments();
        watchdogStates.Should().Contain(0);
    }

    [TestMethod]
    public async Task WatchdogTimeoutWhileClosing_ShouldStopRoof()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromMilliseconds(150));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var errorSignal = CreateStatusSignal(svc, RoofControllerStatus.Error, out var handler);
        try
        {
            svc.Close().IsSuccessful.Should().BeTrue();
            svc.Status.Should().Be(RoofControllerStatus.Closing);

            await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            svc.StatusChanged -= handler;
        }

        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedMovementCommand_ShouldRefreshWatchdogDeadline(bool close)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromSeconds(1));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var errorSignal = CreateStatusSignal(svc, RoofControllerStatus.Error, out var handler);
        try
        {
            var firstCommand = close ? svc.Close() : svc.Open();
            firstCommand.IsSuccessful.Should().BeTrue();

            await Task.Delay(300);

            var refreshCommand = close ? svc.Close() : svc.Open();
            refreshCommand.IsSuccessful.Should().BeTrue();

            await Task.Delay(800);

            svc.Status.Should().Be(close ? RoofControllerStatus.Closing : RoofControllerStatus.Opening);
            svc.IsWatchdogActive.Should().BeTrue();

            await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            svc.StatusChanged -= handler;
        }

        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
    }

    [TestMethod]
    public async Task DirectionChange_ShouldCreateFreshWatchdogDeadline()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromSeconds(1));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var errorSignal = CreateStatusSignal(svc, RoofControllerStatus.Error, out var handler);
        try
        {
            svc.Open().IsSuccessful.Should().BeTrue();
            await Task.Delay(300);

            svc.Close().IsSuccessful.Should().BeTrue();
            await Task.Delay(800);

            svc.Status.Should().Be(RoofControllerStatus.Closing);
            svc.IsWatchdogActive.Should().BeTrue();

            await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            svc.StatusChanged -= handler;
        }

        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
    }

    [TestMethod]
    public async Task StaleWatchdogCallbackAfterRefresh_ShouldNotStopRoof()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromSeconds(5));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        var staleTimer = svc.CurrentWatchdogTimer;

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.CurrentWatchdogTimer.Should().NotBeSameAs(staleTimer);

        svc.TriggerWatchdogCallback(staleTimer);

        svc.Status.Should().Be(RoofControllerStatus.Opening);
        svc.IsWatchdogActive.Should().BeTrue();
        svc.LastStopReason.Should().NotBe(RoofControllerStopReason.SafetyWatchdogTimeout);
    }

    [TestMethod]
    public async Task ManualStop_ShouldCancelWatchdog()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = CreateService(hat, TimeSpan.FromMilliseconds(150));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        await Task.Delay(350);

        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    private static TaskCompletionSource<RoofStatusChangedEventArgs> CreateStatusSignal(
        RoofControllerServiceV4 service,
        RoofControllerStatus expectedStatus,
        out EventHandler<RoofStatusChangedEventArgs> handler)
    {
        var signal = new TaskCompletionSource<RoofStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler = (_, args) =>
        {
            if (args.Status.Status == expectedStatus)
            {
                signal.TrySetResult(args);
            }
        };
        service.StatusChanged += handler;
        return signal;
    }
}