using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The motion timing histograms (docs/telemetry.md, Motion timing): travel time, the drive run input's (IN4) start and
/// stop delays and the start limit's release, each measured on the controller's clock. NC limit wiring (the test
/// default): raw LOW = limit active; IN1 is the open limit, IN2 the closed one.
/// </summary>
[TestClass]
public class RoofControllerMotionTimingTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    private sealed record Rig(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time, MotionTimingRecorder Timing) : IDisposable
    {
        public void Advance(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));

        /// <summary>Sets the raw inputs (IN1, IN2, IN4; no drive fault) and runs a supervision cycle, which reads them.</summary>
        public void Inputs(bool openLimitActive, bool closedLimitActive, bool running)
        {
            Hat.SetInputs(!openLimitActive, !closedLimitActive, false, running);
            Service.RunSupervisionCycle();
        }

        public void Dispose()
        {
            Service.Dispose();
            Timing.Dispose();
        }
    }

    // Not async: the recorder marks the calling test's execution context, and an async method would undo that on return.
    private static Task<Rig> CreateAsync(bool atOpenLimit = false, bool atClosedLimit = false, bool running = false, TimeSpan? atSpeed = null)
        => CreateAsync(MotionTimingRecorder.ForThisTest(), atOpenLimit, atClosedLimit, running, atSpeed);

    private static async Task<Rig> CreateAsync(MotionTimingRecorder timing, bool atOpenLimit, bool atClosedLimit, bool running, TimeSpan? atSpeed)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(!atOpenLimit, !atClosedLimit, false, running);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(120);
            opts.PeriodicVerificationInterval = TimeSpan.FromSeconds(1);
            opts.LimitSwitchDebounce = Debounce;
            opts.AtSpeedConfirmationTimeout = atSpeed;
        });
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return new Rig(service, hat, time, timing);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AFullTravel_RecordsItsTimeByDirection_FromTheLimit_AndTheStartLimitsRelease(bool opening)
    {
        using var rig = await CreateAsync(atOpenLimit: !opening, atClosedLimit: opening);
        (opening ? rig.Service.Open() : rig.Service.Close()).IsSuccessful.Should().BeTrue();

        rig.Advance(1.25);
        rig.Inputs(false, false, false);     // the start limit releases
        rig.Advance(0.1);
        rig.Inputs(false, false, false);     // held for the debounce: verified
        rig.Advance(19.15);
        rig.Inputs(opening, !opening, false); // the destination limit

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        var direction = opening ? "opening" : "closing";
        rig.Timing.Of("roof.controller.travel.duration").Should().Equal(
            new TimingSample(20.5, direction, "LimitSwitchReached", FromLimit: true));
        rig.Timing.Of("roof.controller.departure.release").Should().Equal(new TimingSample(1.25, direction));
    }

    [TestMethod]
    public async Task AMoveFromBetweenTheLimits_IsNotFromALimit_AndHasNoStartLimitRelease()
    {
        using var rig = await CreateAsync();
        rig.Service.Close().IsSuccessful.Should().BeTrue();

        rig.Advance(7.25);
        rig.Inputs(false, false, false);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();

        rig.Timing.Of("roof.controller.travel.duration").Should().Equal(new TimingSample(7.25, "closing", "NormalStop", FromLimit: false));
        rig.Timing.Of("roof.controller.departure.release").Should().BeEmpty();
    }

    [TestMethod]
    public async Task AStartLimitThatChatters_RecordsTheReleaseThatHeld()
    {
        using var rig = await CreateAsync(atClosedLimit: true);
        rig.Service.Open().IsSuccessful.Should().BeTrue();

        rig.Advance(0.5);
        rig.Inputs(false, false, false);     // released
        rig.Advance(0.05);
        rig.Inputs(false, true, false);      // back before the debounce: chatter
        rig.Advance(0.2);
        rig.Inputs(false, false, false);     // released again
        rig.Advance(0.1);
        rig.Inputs(false, false, false);     // held: verified

        rig.Service.IsMoving.Should().BeTrue();
        rig.Timing.Of("roof.controller.departure.release").Should().Equal(new TimingSample(0.75, "opening"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TheDriveStartDelay_IsTheTimeUntilIn4FirstReportsRunning(bool withTheInterlock)
    {
        using var rig = await CreateAsync(atSpeed: withTheInterlock ? TimeSpan.FromSeconds(3) : null);
        rig.Service.Open().IsSuccessful.Should().BeTrue();

        rig.Advance(0.4);
        rig.Hat.SetInputs(true, true, false, true);
        rig.Service.SimAtSpeedRaw(true);
        rig.Advance(0.2);
        rig.Service.SimAtSpeedRaw(false);    // dropped and back within the run-loss window: not a second start
        rig.Service.SimAtSpeedRaw(true);

        rig.Service.IsMoving.Should().BeTrue();
        rig.Timing.Of("roof.controller.drive.start_delay").Should().Equal(new TimingSample(0.4, "opening"));
    }

    [TestMethod]
    public async Task TheDriveStopDelay_IsTheTimeFromTheStopUntilIn4Drops_OncePerStop()
    {
        using var rig = await CreateAsync(atSpeed: TimeSpan.FromSeconds(3));
        rig.Service.Close().IsSuccessful.Should().BeTrue();
        rig.Advance(0.3);
        rig.Service.SimAtSpeedRaw(true);
        rig.Advance(5);

        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Advance(1.75);
        rig.Service.SimAtSpeedRaw(false);
        rig.Advance(1);
        rig.Service.SimAtSpeedRaw(true);     // the drive runs again with no command: not another stop
        rig.Service.SimAtSpeedRaw(false);

        rig.Timing.Of("roof.controller.drive.stop_delay").Should().Equal(new TimingSample(1.75, "closing", "NormalStop"));
    }

    [TestMethod]
    public async Task AnIn4ThatDroppedBeforeTheLimitStop_RecordsAZeroStopDelay()
    {
        using var rig = await CreateAsync(atSpeed: TimeSpan.FromSeconds(3));
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Advance(0.3);
        rig.Inputs(false, false, true);
        rig.Advance(10);

        // At the limit the NC contact removes the run command a few milliseconds before the monitoring contact closes.
        rig.Hat.SetInputs(true, true, false, false);
        rig.Service.SimAtSpeedRaw(false);
        rig.Advance(0.005);
        rig.Inputs(true, false, false);

        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Timing.Of("roof.controller.drive.stop_delay").Should().Equal(new TimingSample(0, "opening", "LimitSwitchReached"));
    }

    [TestMethod]
    public async Task AStartThatIn4NeverConfirmed_RecordsNoStopDelay()
    {
        using var rig = await CreateAsync();
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Advance(2);

        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Inputs(false, false, false);

        rig.Timing.Of("roof.controller.drive.start_delay").Should().BeEmpty();
        rig.Timing.Of("roof.controller.drive.stop_delay").Should().BeEmpty();
        rig.Timing.Of("roof.controller.travel.duration").Should().ContainSingle();
    }

    [TestMethod]
    public async Task ANewMoveBeforeIn4Drops_DiscardsTheLastStopsDelay_AndMeasuresNoDriveTimingOfItsOwn()
    {
        // Without the interlock a start is allowed while IN4 still reports running: its HIGH is the last move's run-down,
        // so the new move has no drive start delay, and no stop delay when IN4 has dropped by its stop.
        using var rig = await CreateAsync();
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Advance(0.3);
        rig.Hat.SetInputs(true, true, false, true);
        rig.Service.SimAtSpeedRaw(true);
        rig.Advance(5);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Advance(2);

        rig.Service.Close().IsSuccessful.Should().BeTrue();
        rig.Advance(0.5);
        rig.Inputs(false, false, true);
        rig.Advance(0.5);
        rig.Inputs(false, false, false);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Advance(1);
        rig.Inputs(false, false, false);

        rig.Timing.Of("roof.controller.drive.start_delay").Should().Equal(new TimingSample(0.3, "opening"));
        rig.Timing.Of("roof.controller.drive.stop_delay").Should().BeEmpty();
        rig.Timing.Of("roof.controller.travel.duration").Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ANewMoveBeforeIn4Drops_MeasuresItsOwnStopDelay()
    {
        using var rig = await CreateAsync();
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Hat.SetInputs(true, true, false, true);
        rig.Service.SimAtSpeedRaw(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Advance(2);

        rig.Service.Close().IsSuccessful.Should().BeTrue();
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Advance(0.5);
        rig.Service.SimAtSpeedRaw(false);

        rig.Timing.Of("roof.controller.drive.stop_delay").Should().Equal(new TimingSample(0.5, "closing", "NormalStop"));
    }
}
