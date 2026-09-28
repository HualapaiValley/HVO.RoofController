using System;
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
/// The drive run input (IN4) with the interlock configured (<see cref="RoofControllerOptionsV4.AtSpeedConfirmationTimeout"/>):
/// a start is refused while IN4 still reports running, IN4 dropping after it confirmed the start stops motion with a
/// latched <see cref="RoofControllerStopReason.DriveNotRunning"/> unless it returns or the destination limit arrives
/// within <see cref="RoofControllerServiceV4.RunLossConfirmationDelay"/>, and
/// <see cref="RoofControllerOptionsV4.DriveStopConfirmationTimeout"/> sets the stop-side diagnostic window.
/// NC limit wiring (the test default): raw HIGH = limit not active.
/// </summary>
[TestClass]
public class RoofControllerDriveRunTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunLoss = RoofControllerServiceV4.RunLossConfirmationDelay;
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private sealed record Rig(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time, CapturingLogger<RoofControllerServiceV4> Logger) : IDisposable
    {
        public void Dispose() => Service.Dispose();

        /// <summary>Sets IN4 at mid-travel (both limits released, no fault) and delivers the edge.</summary>
        public void SetRunning(bool running)
        {
            Hat.SetInputs(true, true, false, running);
            Service.SimAtSpeedRaw(running);
        }
    }

    private static async Task<Rig> CreateAsync(TimeSpan? atSpeed, TimeSpan? stopWindow = null, bool in4 = false)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, in4);
        var time = new ManualTimeProvider();
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            opts.PeriodicVerificationInterval = TimeSpan.FromSeconds(1); // longer than the run-loss window
            opts.AtSpeedConfirmationTimeout = atSpeed;
            opts.DriveStopConfirmationTimeout = stopWindow;
        }, logger);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return new Rig(svc, hat, time, logger);
    }

    private static async Task<Rig> CreateRunningAsync(bool opening = true)
    {
        var rig = await CreateAsync(Window);
        (opening ? rig.Service.Open() : rig.Service.Close()).IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.IsAtSpeed.Should().BeTrue();
        return rig;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Start_WhileIn4ReportsRunning_IsRefusedWithTheInterlock(bool opening)
    {
        using var rig = await CreateAsync(Window, in4: true);

        var result = opening ? rig.Service.Open() : rig.Service.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);
        result.Error!.Message.Should().Contain("IN4");
        rig.Service.IsMoving.Should().BeFalse();
        rig.Hat.RelayMask.Should().Be(0x00);
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse("a refused start is not a fault");
    }

    [TestMethod]
    public async Task Start_AfterIn4Drops_IsAllowed()
    {
        using var rig = await CreateAsync(Window, in4: true);
        rig.Service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);

        rig.SetRunning(false);

        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task Start_WhileIn4High_IsAllowedWithoutTheInterlock()
    {
        using var rig = await CreateAsync(atSpeed: null, in4: true);

        rig.Service.Open().IsSuccessful.Should().BeTrue();

        rig.Hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task Reversal_WhileTheDriveStillRuns_StopsAndIsRefused()
    {
        using var rig = await CreateRunningAsync(opening: true);

        var result = rig.Service.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive);
        rig.Service.IsMoving.Should().BeFalse("the reversal stops the roof before it checks the drive");
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        rig.Hat.RelayMask.Should().Be(0x00);
        rig.Hat.EverBothDirectionBits.Should().BeFalse();

        rig.SetRunning(false); // the drive finishes its stop
        rig.Service.Close().IsSuccessful.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RunLoss_ForTheConfirmationDelay_StopsAndLatchesDriveNotRunning(bool opening)
    {
        using var rig = await CreateRunningAsync(opening);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        rig.Service.RunSupervisionCycle();

        rig.SetRunning(false);
        rig.Logger.Contains(LogLevel.Warning, "Drive run input (IN4) dropped").Should().BeTrue();
        rig.Time.Advance(RunLoss - Ms);
        rig.Service.RunSupervisionCycle();
        rig.Service.IsMoving.Should().BeTrue("the drive gets the confirmation delay to report running again");

        rig.Time.Advance(Ms);
        rig.Service.RunSupervisionCycle();

        rig.Service.IsMoving.Should().BeFalse();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        var snapshot = rig.Service.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        snapshot.LastError.Should().Contain("stopped reporting running (IN4)");
        rig.Hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task RunLoss_WakesTheSupervisionLoop_OncePerWindow()
    {
        using var rig = await CreateRunningAsync();
        var wakes = rig.Service.SupervisionWakeCount;

        rig.SetRunning(false);
        rig.Service.SupervisionWakeCount.Should().Be(wakes + 1, "the loop may be sleeping for a whole verification interval");

        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Service.RunSupervisionCycle();
        rig.Service.SupervisionWakeCount.Should().Be(wakes + 1, "the window is already running");
    }

    [TestMethod]
    public async Task RunLoss_WithTheBackgroundLoop_StopsWithinTheWindow_NotAtTheNextVerification()
    {
        // An external stop that opens TB-1 for longer than the window, then closes it with the run input still held,
        // must not let the drive restart: the loop has to act when the window ends, not after its 5 s sleep.
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = SimulatedRoofControllerService.Create(hat, timeProvider: null, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            opts.PeriodicVerificationInterval = TimeSpan.FromSeconds(5);
            opts.AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(30);
        }, backgroundSupervision: true);

        try
        {
            (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
            svc.Open().IsSuccessful.Should().BeTrue();
            hat.SetInputs(true, true, false, true);
            svc.SimAtSpeedRaw(true);
            svc.IsAtSpeed.Should().BeTrue();

            hat.SetInputs(true, true, false, false);
            svc.SimAtSpeedRaw(false);

            // Well inside the 5 s verification interval, with margin for a loaded machine.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (svc.IsMoving && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            svc.IsMoving.Should().BeFalse("the run-loss wake reschedules the loop to the end of the window");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
            hat.RelayMask.Should().Be(0x00);
        }
        finally
        {
            await svc.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RunLoss_ThatRecovers_CancelsTheStop()
    {
        using var rig = await CreateRunningAsync();

        rig.SetRunning(false);
        rig.Time.Advance(RunLoss - TimeSpan.FromMilliseconds(50));
        rig.Service.RunSupervisionCycle();
        rig.SetRunning(true);
        rig.Logger.Contains(LogLevel.Information, "Drive run input (IN4) returned").Should().BeTrue();

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Service.RunSupervisionCycle();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x09);

        rig.SetRunning(false); // a later loss starts a fresh window
        rig.Time.Advance(RunLoss - Ms);
        rig.Service.RunSupervisionCycle();
        rig.Service.IsMoving.Should().BeTrue();
        rig.Time.Advance(Ms);
        rig.Service.RunSupervisionCycle();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
    }

    [TestMethod]
    public async Task RunLoss_BeforeTheStartWasConfirmed_IsLeftToTheAtSpeedWindow()
    {
        using var rig = await CreateAsync(Window);
        rig.Service.Open().IsSuccessful.Should().BeTrue();

        rig.SetRunning(false); // never confirmed: no run-loss window
        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        rig.Service.RunSupervisionCycle();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromMilliseconds(500), "only the at-speed deadline is pending");
    }

    [TestMethod]
    public async Task RunLoss_WithoutTheInterlock_IsIgnored()
    {
        using var rig = await CreateAsync(atSpeed: null);
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);

        rig.SetRunning(false);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Service.RunSupervisionCycle();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Logger.Contains(LogLevel.Warning, "Drive run input (IN4) dropped").Should().BeFalse();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DestinationLimit_NotYetDeliveredWhenTheRunLossWindowEnds_StopsAtTheLimit(bool opening)
    {
        using var rig = await CreateRunningAsync(opening);

        // At the limit the NC contact removes the run command a few ms before the NO contact closes: IN4 drops first, and
        // the limit edge is still undelivered when the window ends. The repeated command checks the deadlines, which
        // re-read the inputs before stopping.
        rig.SetRunning(false);
        rig.Hat.SetInputs(!opening, opening, false, false);
        rig.Time.Advance(RunLoss);

        (opening ? rig.Service.Open() : rig.Service.Close()).IsSuccessful.Should().BeTrue("a repeat that finds the destination has nothing left to do");

        rig.Service.IsMoving.Should().BeFalse();
        rig.Service.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        rig.Service.Status.Should().Be(opening ? RoofControllerStatus.Open : RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task DestinationLimit_FoundByTheReRead_IsReportedToALeaseRenewal()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(60);
            opts.AtSpeedConfirmationTimeout = Window;
            opts.OperatorLeaseTimeout = TimeSpan.FromSeconds(30);
        });
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        svc.SimAtSpeedRaw(true);

        hat.SetInputs(true, true, false, false);
        svc.SimAtSpeedRaw(false);
        hat.SetInputs(false, true, false, false);
        time.Advance(RunLoss);

        var renewal = svc.RenewLease();

        renewal.ErrorCode().Should().Be(RoofControllerErrorCode.LeaseNotActive);
        renewal.Error!.Message.Should().Contain("destination limit");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
    }

    [TestMethod]
    public async Task Reversal_AfterTheReReadFindsTheDestination_Starts()
    {
        using var rig = await CreateRunningAsync(opening: true);
        rig.SetRunning(false);
        rig.Hat.SetInputs(false, true, false, false);
        rig.Time.Advance(RunLoss);

        rig.Service.Close().IsSuccessful.Should().BeTrue();

        rig.Service.IsMoving.Should().BeTrue();
        rig.Hat.RelayMask.Should().Be(0x0A);
        rig.Hat.EverBothDirectionBits.Should().BeFalse();
    }

    [TestMethod]
    public async Task SupervisionDelay_IncludesTheRunLossWindow()
    {
        using var rig = await CreateRunningAsync();
        rig.Service.GetSupervisionDelay().Should().BeGreaterThan(RunLoss);

        rig.SetRunning(false);

        rig.Service.GetSupervisionDelay().Should().Be(RunLoss);
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Service.GetSupervisionDelay().Should().Be(RunLoss - TimeSpan.FromMilliseconds(100));
    }

    [TestMethod]
    public async Task DriveStopConfirmationTimeout_OverridesTheAtSpeedWindowAfterAStop()
    {
        var stopWindow = TimeSpan.FromSeconds(5);
        using var rig = await CreateAsync(Window, stopWindow);
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();

        rig.Time.Advance(Window);
        rig.Service.RunSupervisionCycle();
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse("the stop window replaces the at-speed window");

        rig.Time.Advance(stopWindow - Window - Ms);
        rig.Service.RunSupervisionCycle();
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse();

        rig.Time.Advance(Ms);
        rig.Service.RunSupervisionCycle();
        rig.Service.RunSupervisionCycle();
        rig.Logger.MessagesAt(LogLevel.Critical).Count(m => m.Contains("Drive still reports running")).Should().Be(1);
        rig.Service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
    }

    [TestMethod]
    public async Task DriveStopConfirmationTimeout_WorksWithoutTheInterlock()
    {
        var stopWindow = TimeSpan.FromSeconds(4);
        using var rig = await CreateAsync(atSpeed: null, stopWindow);
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();

        rig.Time.Advance(stopWindow - Ms);
        rig.Service.RunSupervisionCycle();
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse();

        rig.Time.Advance(Ms);
        rig.Service.RunSupervisionCycle();
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeTrue();

        rig.Service.Open().IsSuccessful.Should().BeTrue("without the interlock a running drive does not block a start");
    }

    [TestMethod]
    public async Task SupervisionDelay_WhileIdle_WakesAtTheDriveStopCheck()
    {
        var stopWindow = TimeSpan.FromSeconds(2.5);
        using var rig = await CreateAsync(Window, stopWindow);
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.Service.GetSupervisionDelay().Should().Be(RoofControllerServiceV4.IdleSupervisionInterval);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Service.GetSupervisionDelay().Should().Be(TimeSpan.FromMilliseconds(500), "the check is due before the next idle cycle");

        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        rig.Service.RunSupervisionCycle();
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeTrue("the check runs at the deadline");
        rig.Service.GetSupervisionDelay().Should().Be(RoofControllerServiceV4.IdleSupervisionInterval, "the stop is reported once");
    }

    [TestMethod]
    public async Task SupervisionDelay_WhileIdle_IsTheIdleInterval_AfterTheDriveStopCheckPassed()
    {
        using var rig = await CreateAsync(Window);
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();
        rig.SetRunning(false);

        rig.Time.Advance(Window + TimeSpan.FromSeconds(5));
        rig.Service.RunSupervisionCycle();

        rig.Service.GetSupervisionDelay().Should().Be(RoofControllerServiceV4.IdleSupervisionInterval, "a passed check never shortens the idle cycle");
        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse();
    }

    [TestMethod]
    public async Task DriveThatStopsWithinTheStopWindow_IsNotReported()
    {
        using var rig = await CreateAsync(Window, TimeSpan.FromSeconds(5));
        rig.Service.Open().IsSuccessful.Should().BeTrue();
        rig.SetRunning(true);
        rig.Service.Stop().IsSuccessful.Should().BeTrue();

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        rig.SetRunning(false);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Service.RunSupervisionCycle();

        rig.Logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse();
    }
}
