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
/// Safety input read failures: every start needs a fresh read, and repeated failures while moving stop the roof with a
/// latched <see cref="RoofControllerStopReason.InputReadFailure"/>.
/// </summary>
[TestClass]
public class RoofControllerInputReadFailureTests
{
    private const int Threshold = 3;

    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var svc = SimulatedRoofControllerService.Create(hat, time, opts => opts.MaxConsecutiveInputReadFailures = Threshold);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat, time);
    }

    [TestMethod]
    public async Task ReadFailuresWhileMoving_BelowTheThreshold_ShouldNotStop()
    {
        var (svc, hat, _) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        hat.Registers.FailInputReads = true;
        for (var i = 0; i < Threshold - 1; i++)
        {
            svc.RunSupervisionCycle();
        }

        svc.IsMoving.Should().BeTrue();
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.ConsecutiveInputReadFailures.Should().Be(Threshold - 1);
        snapshot.InputsHealthy.Should().BeFalse();
        hat.RelayMask.Should().Be(0x09);
    }

    [TestMethod]
    public async Task ReadFailuresWhileMoving_AtTheThreshold_ShouldStopAndLatchInputReadFailure()
    {
        var (svc, hat, _) = await CreateAsync();
        using var _ = svc;
        svc.Close().IsSuccessful.Should().BeTrue();

        hat.Registers.FailInputReads = true;
        for (var i = 0; i < Threshold; i++)
        {
            svc.RunSupervisionCycle();
        }

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.InputReadFailure);
        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.InputReadFailure);
        snapshot.Status.Should().Be(RoofControllerStatus.Error);
        snapshot.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified, "the relay register is still readable");
        hat.RelayMask.Should().Be(0x00);

        // Reads recover: the latch stays until ClearFault.
        hat.Registers.FailInputReads = false;
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().InputsHealthy.Should().BeTrue();
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
    }

    [TestMethod]
    public async Task ASuccessfulRead_ShouldResetTheConsecutiveCount()
    {
        var (svc, hat, _) = await CreateAsync();
        using var _ = svc;
        svc.Open().IsSuccessful.Should().BeTrue();

        for (var round = 0; round < 3; round++)
        {
            hat.Registers.FailNextInputReads = Threshold - 1;
            for (var i = 0; i < Threshold - 1; i++)
            {
                svc.RunSupervisionCycle();
            }

            svc.RunSupervisionCycle(); // succeeds
            svc.GetCurrentStatusSnapshot().ConsecutiveInputReadFailures.Should().Be(0);
        }

        svc.IsMoving.Should().BeTrue("the failures were never consecutive up to the threshold");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Start_WithAFailedFreshRead_ShouldBeRefusedAsHardwareUnavailable(bool opening)
    {
        var (svc, hat, _) = await CreateAsync();
        using var _ = svc;
        hat.ClearRelayWriteLog();
        hat.Registers.FailNextInputReads = 1;

        var result = opening ? svc.Open() : svc.Close();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.HardwareUnavailable);
        svc.IsMoving.Should().BeFalse();
        hat.RelayWriteLog.Should().BeEmpty("nothing is energized without a fresh read");
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse("a refused start is not a latched fault");

        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue("the next start reads successfully");
    }

    [TestMethod]
    public async Task IdleReadFailures_ShouldMarkInputsUnhealthy_WithoutLatching()
    {
        var (svc, hat, _) = await CreateAsync();
        using var _ = svc;

        hat.Registers.FailInputReads = true;
        for (var i = 0; i < Threshold + 2; i++)
        {
            svc.RunSupervisionCycle();
        }

        var snapshot = svc.GetCurrentStatusSnapshot();
        snapshot.InputsHealthy.Should().BeFalse();
        snapshot.IsFaultLatched.Should().BeFalse("nothing is moving, so there is nothing to stop");
        snapshot.LastError.Should().Contain("Safety input read failed");

        hat.Registers.FailInputReads = false;
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().InputsHealthy.Should().BeTrue();
    }

    [TestMethod]
    public async Task StaleInputs_ShouldBeReportedUnhealthy()
    {
        var (svc, _, time) = await CreateAsync();
        using var _ = svc;
        svc.GetCurrentStatusSnapshot().InputsHealthy.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(6)); // beyond max(5 s, 3 x cadence) with no read

        svc.GetCurrentStatusSnapshot().InputsHealthy.Should().BeFalse();
        svc.RunSupervisionCycle();
        svc.GetCurrentStatusSnapshot().InputsHealthy.Should().BeTrue();
    }

    [TestMethod]
    public async Task Initialize_WithAFailedRead_ShouldFailAsHardwareUnavailable_AndBeRetryable()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        hat.Registers.FailNextInputReads = 1;

        (await svc.Initialize(CancellationToken.None)).ErrorCode().Should().Be(RoofControllerErrorCode.HardwareUnavailable);
        svc.IsInitialized.Should().BeFalse();
        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.NotInitialized);

        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
    }
}
