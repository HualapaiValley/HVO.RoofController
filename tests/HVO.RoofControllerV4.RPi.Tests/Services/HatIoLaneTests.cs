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
        // PR #230 rereview, Opus F17: the recovery raises the controller's fence; no access may reach the HAT before it.
        var lane = HatIoLane.For(new object());
        using var release = new ManualResetEventSlim(false);
        var overrunDuringRecovery = (bool?)null;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Recovered += () =>
        {
            overrunDuringRecovery = lane.IsOverrun;
            recovered.TrySetResult();
        };

        var hanging = () => lane.Run(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, Limit);
        hanging.Should().Throw<HatIoOverrunException>();
        release.Set();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        overrunDuringRecovery.Should().BeTrue("the lane reopens only after its recovery has run");
    }
}
