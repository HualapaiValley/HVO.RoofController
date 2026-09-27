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
/// Optional drive at-speed interlock (IN4). Applies only when <see cref="RoofControllerOptionsV4.AtSpeedConfirmationTimeout"/>
/// is set: the drive must report at-speed within the window after a start, or motion stops with a latched
/// <see cref="RoofControllerStopReason.DriveNotRunning"/>. After a stop, a drive still reporting at-speed once the same
/// window has elapsed is reported (diagnostic only).
/// </summary>
[TestClass]
public class RoofControllerAtSpeedTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time, CapturingLogger<RoofControllerServiceV4> Logger)> CreateAsync(TimeSpan? window)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.AtSpeedConfirmationTimeout = window;
        }, logger);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, time, logger);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NoAtSpeedWithinTheWindow_ShouldStopAndLatchDriveNotRunning(bool opening)
    {
        var (svc, hat, time, _) = await CreateAsync(Window);
        using var _ = svc;
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();

        time.Advance(Window - TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task AtSpeedReadBySupervision_ShouldConfirmTheDrive()
    {
        var (svc, hat, time, _) = await CreateAsync(Window);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.SetInputs(true, true, false, true);
        svc.RunSupervisionCycle();
        svc.IsAtSpeed.Should().BeTrue();

        time.Advance(Window + TimeSpan.FromSeconds(1));
        svc.RunSupervisionCycle();
        svc.IsMoving.Should().BeTrue("at-speed was confirmed inside the window");
    }

    [TestMethod]
    public async Task AtSpeedEdge_ShouldConfirmTheDrive()
    {
        var (svc, _, time, _) = await CreateAsync(Window);
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();

        svc.SimAtSpeedRaw(true); // polled IN4 edge confirms the start
        time.Advance(Window);
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeTrue("the start was confirmed inside the window");
    }

    [TestMethod]
    public async Task WithoutTheWindowConfigured_IN4IsNotRequired()
    {
        var (svc, hat, time, _) = await CreateAsync(window: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(5));
        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeTrue();
        hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task DriveStillAtSpeedAfterStop_ShouldBeReportedOnce_WithoutLatching()
    {
        var (svc, hat, time, logger) = await CreateAsync(Window);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        svc.RunSupervisionCycle();
        svc.Stop().IsSuccessful.Should().BeTrue();

        time.Advance(Window - TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse("the drive gets the same window to stop");

        time.Advance(TimeSpan.FromMilliseconds(1));
        svc.RunSupervisionCycle();
        svc.RunSupervisionCycle();

        logger.MessagesAt(LogLevel.Critical).Count(m => m.Contains("Drive still reports running")).Should().Be(1);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.LastError.Should().Contain("running");
        snapshot.IsFaultLatched.Should().BeFalse("the relays already read back off; this is a diagnostic");
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task DriveStillAtSpeedAfterStop_IsNotCheckedWithoutTheWindow()
    {
        var (svc, hat, time, logger) = await CreateAsync(window: null);
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();
        hat.SetInputs(true, true, false, true);
        svc.RunSupervisionCycle();
        svc.Stop().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(30));
        svc.RunSupervisionCycle();

        logger.Contains(LogLevel.Critical, "Drive still reports running").Should().BeFalse();
    }
}
