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
/// Refused commands, the relay guard and idempotency. Deterministic: manual time, no background supervision.
/// </summary>
[TestClass]
public class RoofControllerNegativeTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateAsync(bool in1, bool in2, bool in3, bool in4 = false)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(in1, in2, in3, in4);
        var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        hat.ClearRelayWriteLog();
        return (svc, hat);
    }

    [TestMethod]
    public async Task Open_WithBothLimitsActive_ShouldBeRefusedAsFaultLatched()
    {
        // NC: raw LOW on both = both limits "active" = wiring fault, latched at initialization.
        var (svc, hat) = await CreateAsync(false, false, false);
        using var _ = svc;

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00);
        hat.RelayWriteLog.Should().BeEmpty("a refused command writes nothing");
    }

    [TestMethod]
    public async Task Close_WithBothLimitsActive_ShouldBeRefusedAsFaultLatched()
    {
        var (svc, hat) = await CreateAsync(false, false, false);
        using var _ = svc;

        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task MovementAttemptWhileFaultActive_ShouldBeRefused()
    {
        var (svc, hat) = await CreateAsync(true, true, true); // mid-travel, IN3 active (active-high default)
        using var _ = svc;

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);

        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task FaultAppearingAfterInitialization_ShouldRefuseTheNextStartAsInterlockActive()
    {
        var (svc, hat) = await CreateAsync(true, true, false);
        using var _ = svc;

        hat.SetInputs(true, true, true, false); // IN3 asserts; nothing has evaluated it yet

        var result = svc.Open();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive, "the fresh read at start sees IN3");
        svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    [DataRow(true, true, true, DisplayName = "Both directions")]
    [DataRow(false, true, false, DisplayName = "Open without STOP permit")]
    [DataRow(false, false, true, DisplayName = "Close without STOP permit")]
    public async Task RelayGuard_ShouldRefuseInvalidCombinations_AndDriveAllOff(bool stop, bool open, bool close)
    {
        var (svc, hat) = await CreateAsync(true, true, false);
        using var _ = svc;

        svc.ForceRelayStates(stop, open, close).Should().BeFalse();

        hat.EverBothDirectionBits.Should().BeFalse("open and close are never energized together");
        hat.RelayMask.Should().Be(0x00, "an invalid request drives every relay off");
    }

    [TestMethod]
    public async Task Stop_ShouldBeIdempotent_WhenAlreadyStopped()
    {
        var (svc, hat) = await CreateAsync(true, true, false);
        using var _ = svc;

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(0x00);
        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        hat.RelayMask.Should().Be(0x00);
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.None, "idle stops do not rewrite motion history");
    }

    [TestMethod]
    public async Task BothLimitsError_ShouldContinueToRefuseSubsequentCommands()
    {
        var (svc, hat) = await CreateAsync(false, false, false);
        using var _ = svc;

        for (var i = 0; i < 3; i++)
        {
            svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
            svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.FaultLatched);
            svc.Status.Should().Be(RoofControllerStatus.Error);
        }

        hat.RelayMask.Should().Be(0x00);
        hat.EverBothDirectionBits.Should().BeFalse();
    }

    [TestMethod]
    public void CommandsBeforeInitialize_ShouldBeRefusedAsNotInitialized_ExceptStop()
    {
        var hat = new FakeRoofHat();
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());

        svc.Open().ErrorCode().Should().Be(RoofControllerErrorCode.NotInitialized);
        svc.Close().ErrorCode().Should().Be(RoofControllerErrorCode.NotInitialized);
        svc.RenewLease().ErrorCode().Should().Be(RoofControllerErrorCode.NotInitialized);
        svc.Stop().IsSuccessful.Should().BeTrue("Stop is always allowed and re-asserts all-off");
        hat.RelayMask.Should().Be(0x00);
    }
}
