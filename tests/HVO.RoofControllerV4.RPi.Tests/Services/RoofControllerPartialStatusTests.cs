using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
public class RoofControllerPartialStatusTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat)> CreateMidTravelAsync()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // NC: both limits released (mid-travel)
        var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(30));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (svc, hat);
    }

    [TestMethod]
    public async Task Open_ThenManualStop_ShouldTransitionToPartiallyOpen()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        svc.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse("an operator stop is not a fault");
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task Close_ThenManualStop_ShouldTransitionToPartiallyClose()
    {
        var (svc, hat) = await CreateMidTravelAsync();
        using var _ = svc;

        svc.Close().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closing);

        svc.Stop(RoofControllerStopReason.NormalStop).IsSuccessful.Should().BeTrue();

        svc.Status.Should().Be(RoofControllerStatus.PartiallyClose);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task IdleStop_ShouldNotChangeLastStopReasonOrPartialStatus()
    {
        var (svc, _) = await CreateMidTravelAsync();
        using var __ = svc;

        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Stop().IsSuccessful.Should().BeTrue();
        var count = svc.StopSequenceCount;

        svc.Stop(RoofControllerStopReason.EmergencyStop).IsSuccessful.Should().BeTrue();

        svc.StopSequenceCount.Should().Be(count + 1, "an idle stop still re-asserts and verifies all-off");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop, "an idle stop does not rewrite motion history");
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
    }
}
