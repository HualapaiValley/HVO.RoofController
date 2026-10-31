using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The command generation and its relay fence (#220, §2 U1/P1/T1, R8-3): a Stop or a protective stop that comes after a
/// start's checks wins at the relay; Stop's wait for the controller lock is bounded (W3, AA5); refusals, repeats and
/// no-ops never move the generation.
/// </summary>
[TestClass]
public class RoofControllerCommandFencingTests
{
    private const byte RelaySetRegister = 1;

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateAsync(Action<RoofControllerOptionsV4>? configure = null)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel, healthy (NC limits: HIGH = not active)
        var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), configure);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (service, hat);
    }

    [TestMethod]
    public async Task StopBetweenTheChecksAndTheRelay_Wins_NothingIsEnergized_NoFaultLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task<HVO.Core.Results.Result<RoofControllerStatus>>? stop = null;
        service.BeforeEnergizeHook = () =>
        {
            // Another thread's Stop: it moves the generation and raises the fence at once, then waits for the lock.
            var admitted = service.CommandGeneration;
            stop = Task.Run(() => service.Stop());
            SpinWait.SpinUntil(() => service.CommandGeneration > admitted, TimeSpan.FromSeconds(5)).Should().BeTrue();
        };
        hat.ClearRelayWriteLog();

        var result = service.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.Superseded);
        (await stop!).IsSuccessful.Should().BeTrue("the Stop got the lock once the refused start let it go");
        hat.RelayMask.Should().Be(0x00);
        hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister, "no relay was ever switched on");
        var status = service.GetCurrentStatusSnapshot();
        status.IsFaultLatched.Should().BeFalse("a Stop overtaking a start is not a fault");
        status.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task DriveFaultTripBetweenTheChecksAndTheRelay_StopsTheStartedRoof_AndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? trip = null;
        service.BeforeEnergizeHook = () =>
        {
            hat.SetInputs(true, true, true, false);
            trip = Task.Run(() => service.SimFaultRaw(true)); // waits for the lock the start holds
            Thread.Sleep(50);
        };

        var result = service.Close();
        await trip!;

        result.IsSuccessful.Should().BeTrue("the start finished its write before the trip, which then switched it off");
        hat.RelayMask.Should().Be(0x00);
        var status = service.GetCurrentStatusSnapshot();
        status.IsMoving.Should().BeFalse();
        status.IsFaultLatched.Should().BeTrue();
        status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task WatchdogBetweenTheChecksAndTheRelay_StopsAndLatches()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        Task? watchdog = null;
        service.BeforeEnergizeHook = () =>
        {
            var generation = service.CurrentMotionGeneration;
            watchdog = Task.Run(() => service.TriggerWatchdog(generation)); // the start has armed this generation
            Thread.Sleep(50);
        };

        service.Close().IsSuccessful.Should().BeTrue();
        await watchdog!;

        hat.RelayMask.Should().Be(0x00);
        service.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
    }

    [TestMethod]
    public async Task Stop_WhileARelayWriteHangs_AnswersWithinItsBound_AndTheQueuedStopRunsAfter()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;
        using var release = new ManualResetEventSlim(false);
        using var hanging = new ManualResetEventSlim(false);
        hat.Registers.BeforeRelayWrite = (register, value) =>
        {
            if (register == RelaySetRegister && value == 2)
            {
                // The close relay's write hangs in the transfer, holding the controller lock.
                hanging.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var close = Task.Run(() => service.Close());
        hanging.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var watch = Stopwatch.StartNew();
        var stop = service.Stop();
        watch.Stop();

        stop.ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified, "Stop cannot verify the relays while the write hangs");
        watch.Elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromSeconds(1));
        service.DeferredStopCount.Should().Be(1);

        hat.Registers.BeforeRelayWrite = null;
        release.Set();
        await close;
        SpinWait.SpinUntil(() => service.DeferredStopCount == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();

        hat.RelayMask.Should().Be(0x00, "the queued relays-off ran as soon as the lock was free");
        service.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task Generation_MovesWithStartsAndStops_NeverWithRefusalsRepeatsOrNoOps()
    {
        var (service, hat) = await CreateAsync();
        using var _ = service;

        var before = service.CommandGeneration;
        service.Close().IsSuccessful.Should().BeTrue();
        var started = service.CommandGeneration;
        started.Should().BeGreaterThan(before, "an admitted start moves it");

        service.Close().IsSuccessful.Should().BeTrue();
        service.CommandGeneration.Should().Be(started, "a repeat does not");

        service.Stop().IsSuccessful.Should().BeTrue();
        var stopped = service.CommandGeneration;
        stopped.Should().BeGreaterThan(started, "a Stop does");

        hat.SetInputs(true, false, false, false); // the closed limit (NC: LOW = active)
        service.ForceStatusRefresh();
        service.Close().IsSuccessful.Should().BeTrue();
        service.CommandGeneration.Should().Be(stopped, "a no-op at the destination does not");

        hat.SetInputs(true, true, true, false); // the drive fault
        service.Open().IsSuccessful.Should().BeFalse();
        var latched = service.CommandGeneration;
        service.Open().IsSuccessful.Should().BeFalse();
        service.CommandGeneration.Should().Be(latched, "a refusal does not");
    }
}
