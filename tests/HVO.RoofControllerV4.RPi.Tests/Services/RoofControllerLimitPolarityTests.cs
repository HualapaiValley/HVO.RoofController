using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Limit switch (IN1/IN2) and drive fault (IN3) polarity.
/// </summary>
[TestClass]
public class RoofControllerLimitPolarityTests
{
    private static SimulatedRoofControllerService Create(FakeRoofHat hat, Action<RoofControllerOptionsV4>? configure = null)
        => SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), configure);

    [TestMethod]
    public async Task NcPolarity_LowEqualsLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // NC: travel = HIGH on both
        using var svc = Create(hat, opts => opts.UseNormallyClosedLimitSwitches = true);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        hat.SetInputs(false, true, false, false); // IN1 LOW -> open limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);
        svc.GetCurrentStatusSnapshot().IsOpenLimitActive.Should().BeTrue();

        hat.SetInputs(true, false, false, false); // IN2 LOW -> closed limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
        svc.GetCurrentStatusSnapshot().IsClosedLimitActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task NoPolarity_HighEqualsLimit()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false); // NO: travel = LOW on both
        using var svc = Create(hat, opts => opts.UseNormallyClosedLimitSwitches = false);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        hat.SetInputs(true, false, false, false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);

        hat.SetInputs(false, true, false, false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task NcPolarity_DisconnectedWiring_BothLow_IsContradictoryAndLatches()
    {
        // With NC switches an open circuit on both inputs reads LOW/LOW: both limits "active". This is a wiring fault.
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false);
        using var svc = Create(hat);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue("a startup fault latches but initialization succeeds");

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.ContradictoryLimitInputs);
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true, true, true, DisplayName = "active-high, raw HIGH = fault")]
    [DataRow(true, false, false, DisplayName = "active-high, raw LOW = no fault")]
    [DataRow(false, false, true, DisplayName = "active-low, raw LOW = fault")]
    [DataRow(false, true, false, DisplayName = "active-low, raw HIGH = no fault")]
    public async Task FaultInputPolarity_ShouldFollowConfiguration(bool activeHigh, bool rawHigh, bool expectFault)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, rawHigh, false);
        using var svc = Create(hat, opts => opts.FaultInputActiveHigh = activeHigh);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsDriveFaultActive.Should().Be(expectFault);
        snapshot.IsFaultLatched.Should().Be(expectFault);
        if (expectFault)
        {
            snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
            svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
            hat.LedMask.Should().Be(0x04, "LED3 shows the logical fault");
        }
        else
        {
            svc.Open().IsSuccessful.Should().BeTrue();
            hat.LedMask.Should().Be(0x00);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FaultInputBecomingActiveWhileMoving_ShouldStopAndLatch_ForEitherPolarity(bool activeHigh)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, !activeHigh, false); // inactive level
        using var svc = Create(hat, opts => opts.FaultInputActiveHigh = activeHigh);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Close().IsSuccessful.Should().BeTrue();

        svc.SimFaultRaw(activeHigh); // active level edge

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public void DefaultOptions_FaultInputIsActiveHigh()
    {
        new RoofControllerOptionsV4().FaultInputActiveHigh.Should().BeTrue("the default must not be flipped without bench verification");
    }
}
