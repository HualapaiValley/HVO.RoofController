using System;
using System.Collections.Generic;
using System.Linq;
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
/// StatusChanged delivery: a single ordered dispatcher off the caller's thread and outside the controller lock, a
/// bounded queue that drops the oldest pending snapshot, and a monotonic <see cref="RoofStatusResponse.StatusVersion"/>
/// that only changes when a published field changes. The newest state is always delivered last.
/// </summary>
[TestClass]
public class RoofControllerStatusDispatchTests
{
    private const int QueueCapacity = 64;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static void ShouldBeStrictlyIncreasing(IReadOnlyList<RoofStatusResponse> snapshots)
    {
        for (var i = 1; i < snapshots.Count; i++)
        {
            snapshots[i].StatusVersion.Should().BeGreaterThan(snapshots[i - 1].StatusVersion,
                $"snapshot {i} must be newer than snapshot {i - 1}");
        }
    }

    private static SimulatedRoofControllerService CreateMidTravel(FakeRoofHat hat, CapturingLogger<RoofControllerServiceV4>? logger = null)
    {
        hat.SetInputs(true, true, false, false);
        return SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), logger: logger);
    }

    [TestMethod]
    public async Task Snapshots_ShouldBeDeliveredInVersionOrder_EndingWithTheCurrentVersion()
    {
        var hat = new FakeRoofHat();
        using var svc = CreateMidTravel(hat);
        using var recorder = new StatusChangeRecorder(svc);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(false, true, false, false);
        svc.RunSupervisionCycle(); // open limit reached
        svc.Close().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, false);
        svc.RunSupervisionCycle(); // open limit released
        svc.Stop().IsSuccessful.Should().BeTrue();

        (await recorder.DrainAsync(DrainTimeout)).Should().BeTrue();

        var snapshots = recorder.Snapshots;
        snapshots.Should().NotBeEmpty();
        ShouldBeStrictlyIncreasing(snapshots);
        recorder.LastDeliveredVersion.Should().Be(svc.GetCurrentStatusSnapshot().StatusVersion);

        var statuses = snapshots.Select(s => s.Status).ToList();
        statuses.IndexOf(RoofControllerStatus.Opening).Should().BeLessThan(statuses.IndexOf(RoofControllerStatus.Open));
        statuses.IndexOf(RoofControllerStatus.Closing).Should().BeGreaterThan(statuses.IndexOf(RoofControllerStatus.Open));
        snapshots[^1].Status.Should().Be(RoofControllerStatus.PartiallyClose);
        snapshots[^1].IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task BlockedHandler_ShouldDropTheOldestPendingSnapshots_AndStillDeliverTheNewestLast()
    {
        var hat = new FakeRoofHat();
        using var svc = CreateMidTravel(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        using var release = new ManualResetEventSlim(false);
        var blocked = 0;
        void Blocking(object? sender, RoofStatusChangedEventArgs args)
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                release.Wait(DrainTimeout);
            }
        }

        svc.StatusChanged += Blocking;
        using var recorder = new StatusChangeRecorder(svc);
        var startVersion = svc.GetCurrentStatusSnapshot().StatusVersion;

        const int Changes = 150;
        for (var i = 0; i < Changes; i++)
        {
            hat.SetInputs(true, true, false, i % 2 == 0); // IN4 toggles: each refresh publishes one new version
            svc.RefreshStatus(forceHardwareRead: true);
        }

        var currentVersion = svc.GetCurrentStatusSnapshot().StatusVersion;
        (currentVersion - startVersion).Should().Be(Changes, "every published change bumps the version exactly once");

        release.Set();
        (await recorder.DrainAsync(DrainTimeout)).Should().BeTrue();
        svc.StatusChanged -= Blocking;

        var snapshots = recorder.Snapshots;
        snapshots.Count.Should().BeLessThanOrEqualTo(QueueCapacity + 1, "a blocked handler holds one snapshot; the queue keeps the newest 64");
        ShouldBeStrictlyIncreasing(snapshots);
        recorder.LastDeliveredVersion.Should().Be(currentVersion, "the newest state is never dropped");
        snapshots[^1].IsAtSpeed.Should().Be(svc.IsAtSpeed);
    }

    [TestMethod]
    public async Task ThrowingHandler_ShouldNotPreventDeliveryToOtherHandlers()
    {
        var hat = new FakeRoofHat();
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        using var svc = CreateMidTravel(hat, logger);
        void Throwing(object? sender, RoofStatusChangedEventArgs args) => throw new InvalidOperationException("handler failure");
        svc.StatusChanged += Throwing;
        using var recorder = new StatusChangeRecorder(svc);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();

        (await recorder.DrainAsync(DrainTimeout)).Should().BeTrue();
        recorder.Snapshots.Should().Contain(s => s.Status == RoofControllerStatus.Opening);
        logger.Contains(LogLevel.Error, "StatusChanged handler threw").Should().BeTrue();
        svc.IsMoving.Should().BeTrue("a handler failure never affects the controller");
        svc.StatusChanged -= Throwing;
    }

    [TestMethod]
    public async Task NoPublishedChange_ShouldNotBumpTheVersion()
    {
        var hat = new FakeRoofHat();
        var time = new ManualTimeProvider();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, time);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        var version = svc.GetCurrentStatusSnapshot().StatusVersion;

        svc.GetCurrentStatusSnapshot();
        svc.RefreshStatus(forceHardwareRead: true);
        svc.RunSupervisionCycle();
        time.Advance(TimeSpan.FromMilliseconds(500));
        svc.RunSupervisionCycle(); // the read timestamp moves, but it is not a published change

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.StatusVersion.Should().Be(version);
        snapshot.LastSuccessfulInputReadUtc.Should().Be(time.GetUtcNow());

        hat.SetInputs(false, true, false, false);
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().StatusVersion.Should().Be(version + 1);
    }

    [TestMethod]
    public async Task Handlers_ShouldNeverRunOnTheCallersThreadDuringACommand()
    {
        var hat = new FakeRoofHat();
        using var svc = CreateMidTravel(hat);
        var callingThread = -1;
        var inCommand = false;
        var invokedInline = false;
        svc.StatusChanged += (_, _) =>
        {
            if (Volatile.Read(ref inCommand) && Environment.CurrentManagedThreadId == Volatile.Read(ref callingThread))
            {
                invokedInline = true;
            }
        };
        using var recorder = new StatusChangeRecorder(svc);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        for (var i = 0; i < 20; i++)
        {
            Volatile.Write(ref callingThread, Environment.CurrentManagedThreadId);
            Volatile.Write(ref inCommand, true);
            svc.Open().IsSuccessful.Should().BeTrue();
            svc.Stop().IsSuccessful.Should().BeTrue();
            Volatile.Write(ref inCommand, false);
        }

        (await recorder.DrainAsync(DrainTimeout)).Should().BeTrue();
        invokedInline.Should().BeFalse();
    }

    [TestMethod]
    public async Task HandlerCallingStop_ShouldNotDeadlock()
    {
        var hat = new FakeRoofHat();
        using var svc = CreateMidTravel(hat);
        var stopResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.StatusChanged += (_, args) =>
        {
            if (args.Status.IsMoving && !stopResult.Task.IsCompleted)
            {
                stopResult.TrySetResult(svc.Stop().IsSuccessful);
            }
        };

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();

        (await stopResult.Task.WaitAsync(DrainTimeout)).Should().BeTrue();
        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        hat.RelayMask.Should().Be(0x00);
    }
}
