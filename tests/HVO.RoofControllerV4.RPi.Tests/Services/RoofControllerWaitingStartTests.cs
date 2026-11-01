using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The direction-change delay (<see cref="RoofControllerOptionsV4.DirectionChangeDelay"/>, #220 §19 M2, §20 N1/N2/N6):
/// every start waits it out since the last verified relay-off without holding the lock, reads as moving meanwhile, and is
/// checked again before its relay; Stop cancels it, a command in the same direction joins it, the opposite one replaces it.
/// </summary>
[TestClass]
public class RoofControllerWaitingStartTests
{
    private const byte RelaySetRegister = 1;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private sealed record Rig(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time) : IDisposable
    {
        public void Dispose() => Service.Dispose();

        /// <summary>Advances the clock in small steps, letting the waiting start's continuation run.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            var step = TimeSpan.FromMilliseconds(100);
            for (var done = TimeSpan.Zero; done < by; done += step)
            {
                Time.Advance(step);
                await Task.Delay(5);
            }
        }

        public int DirectionOnWrites(byte relay) => Hat.RelayWriteLog.Count(write => write.Register == RelaySetRegister && write.Value == relay);
    }

    private static async Task<Rig> CreateAsync(TimeSpan? delay = null)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time, options => options.DirectionChangeDelay = delay ?? Delay);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();
        return new Rig(service, hat, time);
    }

    private static async Task<Result<RoofControllerStatus>> Within(Task<Result<RoofControllerStatus>> task)
    {
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(task, "the command must answer");
        return await task;
    }

    [TestMethod]
    public async Task StartRightAfterAStop_WaitsTheDelay_ReadingAsMoving_ThenEnergizes()
    {
        using var rig = await CreateAsync();

        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        close.IsCompleted.Should().BeFalse("the start waits out the delay since initialization's all-off");
        var waiting = rig.Service.GetCurrentStatusSnapshot();
        waiting.Status.Should().Be(RoofControllerStatus.Closing);
        waiting.IsStartPending.Should().BeTrue();
        waiting.PendingMotion.Should().Be(RoofMotionDirection.Closing);
        waiting.IsMoving.Should().BeFalse();
        rig.Hat.RelayMask.Should().Be(0x00, "nothing is energized while the delay runs");

        await rig.AdvanceAsync(Delay + TimeSpan.FromMilliseconds(100));

        (await Within(close)).IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x0A);
        rig.Service.GetCurrentStatusSnapshot().IsStartPending.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheStartDelay_IsWhatAStartWouldReallyWait()
    {
        // PR #230 review F5: an Alpaca ticket's deadline counts only this.
        using var rig = await CreateAsync();
        ((IRoofControllerServiceV4)rig.Service).GetStartDelay(RoofMotionDirection.Closing).Should().Be(Delay, "right after the start's all-off");

        await rig.AdvanceAsync(Delay);
        ((IRoofControllerServiceV4)rig.Service).GetStartDelay(RoofMotionDirection.Closing).Should().Be(TimeSpan.Zero, "the delay has run");
        (await Within(rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening)))).IsSuccessful.Should().BeTrue();
        ((IRoofControllerServiceV4)rig.Service).GetStartDelay(RoofMotionDirection.Opening).Should().Be(TimeSpan.Zero, "a repeat waits for nothing");
        ((IRoofControllerServiceV4)rig.Service).GetStartDelay(RoofMotionDirection.Closing).Should().Be(Delay, "a reversal waits the whole delay after its stop");

        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        ((IRoofControllerServiceV4)rig.Service).GetStartDelay(RoofMotionDirection.Closing).Should().Be(Delay - TimeSpan.FromMilliseconds(500), "what is left after the stop");
    }

    [TestMethod]
    public async Task NoDelay_StartsAtOnce()
    {
        using var rig = await CreateAsync(TimeSpan.Zero);

        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        close.IsCompleted.Should().BeTrue();
        (await close).IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task StopDuringTheWait_AnswersAtOnce_AndNoRelayEverEnergizes()
    {
        using var rig = await CreateAsync();
        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        var stopped = Task.Run(() => rig.Service.Stop());
        (await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(stopped, "the waiting start holds no lock");
        (await stopped).IsSuccessful.Should().BeTrue();

        (await Within(close)).ErrorCode().Should().Be(RoofControllerErrorCode.Superseded);
        await rig.AdvanceAsync(Delay * 2);
        rig.Hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister);
        rig.Service.GetCurrentStatusSnapshot().Status.Should().NotBe(RoofControllerStatus.Closing);
    }

    [TestMethod]
    public async Task ASecondCommandInTheSameDirection_JoinsTheWait_OneStartBothSucceed()
    {
        using var rig = await CreateAsync();
        var first = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));
        var second = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));
        second.IsCompleted.Should().BeFalse("it gets the waiting start's real answer, never an early success");

        await rig.AdvanceAsync(Delay + TimeSpan.FromMilliseconds(100));

        (await Within(first)).IsSuccessful.Should().BeTrue();
        (await Within(second)).IsSuccessful.Should().BeTrue();
        rig.DirectionOnWrites(2).Should().Be(1, "one start");
    }

    [TestMethod]
    public async Task TheOppositeCommandDuringTheWait_ReplacesIt_AndWaitsTheRest()
    {
        using var rig = await CreateAsync();
        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));

        var open = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening));

        (await Within(close)).ErrorCode().Should().Be(RoofControllerErrorCode.Superseded);
        open.IsCompleted.Should().BeFalse("the rest of the delay still runs");
        rig.Service.GetCurrentStatusSnapshot().PendingMotion.Should().Be(RoofMotionDirection.Opening);

        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(1200));
        (await Within(open)).IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x09);
        rig.DirectionOnWrites(2).Should().Be(0);
    }

    [TestMethod]
    public async Task AFaultDuringTheWait_RefusesTheStart_AndNothingMoves()
    {
        using var rig = await CreateAsync();
        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        rig.Hat.SetInputs(true, true, true, false);
        rig.Service.SimFaultRaw(true);

        (await Within(close)).IsSuccessful.Should().BeFalse();
        await rig.AdvanceAsync(Delay * 2);
        rig.Hat.RelayWriteLog.Should().NotContain(write => write.Register == RelaySetRegister);
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeTrue();
    }

    [TestMethod]
    public async Task Reversal_StopsFirst_ThenWaitsTheWholeDelayBeforeTheOppositeRelay()
    {
        using var rig = await CreateAsync();
        var open = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening));
        await rig.AdvanceAsync(Delay + TimeSpan.FromMilliseconds(100));
        (await Within(open)).IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x09);

        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        rig.Hat.RelayMask.Should().Be(0x00, "the reversal switches the running relay off first");
        rig.Service.GetCurrentStatusSnapshot().Status.Should().Be(RoofControllerStatus.Closing);
        await rig.AdvanceAsync(Delay - TimeSpan.FromMilliseconds(300));
        rig.Hat.RelayMask.Should().Be(0x00, "the opposite relay waits the whole delay");

        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        (await Within(close)).IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x0A);
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
    }

    [TestMethod]
    public async Task ConfigurationChange_IsRefusedWhileAStartWaits()
    {
        using var rig = await CreateAsync();
        var close = rig.Service.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing));

        var change = rig.Service.UpdateConfiguration(rig.Service.GetConfigurationSnapshot() with { });

        change.ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        rig.Service.Stop();
        await Within(close);
    }
}
