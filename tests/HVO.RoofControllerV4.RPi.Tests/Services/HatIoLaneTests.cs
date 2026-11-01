using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The HAT's bounded access lane (#220, §17 AA5): an access past its limit fails at the limit, every later one fails at
/// once until it returns, and the lane reopens only after its recovery (which raises the controller's fence) has run.
/// </summary>
[TestClass]
public sealed class HatIoLaneTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);

    [TestMethod]
    public void AnAccessPastItsLimit_FailsAtTheLimit_AndLaterOnesFailAtOnce_UntilItReturns()
    {
        var lane = HatIoLane.For(new object());
        using var release = new ManualResetEventSlim(false);
        using var recovered = new ManualResetEventSlim(false);
        lane.Recovered += recovered.Set;

        var watch = Stopwatch.StartNew();
        var hanging = () => lane.Run(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, Limit);
        hanging.Should().Throw<HatIoOverrunException>();
        watch.Elapsed.Should().BeLessThan(Limit + TimeSpan.FromMilliseconds(500));
        lane.IsOverrun.Should().BeTrue();

        watch.Restart();
        var next = () => lane.Run(() => 2, Limit);
        next.Should().Throw<HatIoOverrunException>("nothing more is sent while the hanging access is out");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100), "it fails at once");

        release.Set();
        recovered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        SpinWait.SpinUntil(() => !lane.IsOverrun, TimeSpan.FromSeconds(5)).Should().BeTrue();
        lane.Run(() => 3, Limit).Should().Be(3);
    }

    [TestMethod]
    public async Task TheLane_StaysClosed_UntilItsRecoveryHasRun()
    {
        // PR #230 rereviews, Opus F17 and F25: the recovery raises the controller's fence; no access may reach the HAT before
        // it. An access made while it runs waits for the lane's turn, which is still held ("stayed busy"); were the turn
        // released first, it would be posted to the lane's thread, busy with the recovery, and run past its limit instead.
        var lane = HatIoLane.For(new object());
        using var release = new ManualResetEventSlim(false);
        string? duringRecovery = null;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Recovered += () =>
        {
            var attempt = Task.Run(() =>
            {
                try
                {
                    lane.Run(() => 0, TimeSpan.FromMilliseconds(50));
                    return "it ran";
                }
                catch (HatIoOverrunException ex)
                {
                    return ex.Message;
                }
            });
            duringRecovery = attempt.Wait(TimeSpan.FromSeconds(2)) ? attempt.Result : "no answer";
            recovered.TrySetResult();
        };

        var hanging = () => lane.Run(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, Limit);
        hanging.Should().Throw<HatIoOverrunException>();
        release.Set();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        duringRecovery.Should().Contain("stayed busy", "the lane's turn is held until its recovery has run");
        SpinWait.SpinUntil(() => !lane.IsOverrun, TimeSpan.FromSeconds(5)).Should().BeTrue();
        lane.Run(() => 5, Limit).Should().Be(5, "the lane is open again after its recovery");
    }

    [TestMethod]
    public async Task TheRecoverysOwnAccesses_WaitForTheLane_RatherThanFailAsStillRunning()
    {
        // PR #230 rereview Opus F22 (P4): the all-off the recovery queues must reach the HAT, not fail with "an earlier HAT
        // access is still running".
        var lane = HatIoLane.For(new object());
        using var release = new ManualResetEventSlim(false);
        Task<int>? allOff = null;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Recovered += () =>
        {
            allOff = Task.Run(() => lane.Run(() => 0, TimeSpan.FromSeconds(5)));
            allOff.Wait(TimeSpan.FromMilliseconds(200)); // as the controller's queued stop may run before the handler returns
            recovered.TrySetResult();
        };

        var hanging = () => lane.Run(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, Limit);
        hanging.Should().Throw<HatIoOverrunException>();
        release.Set();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var run = async () => await allOff!.WaitAsync(TimeSpan.FromSeconds(5));
        (await run.Should().NotThrowAsync("the recovery's own access waits for the lane")).Subject.Should().Be(0);
    }
}
