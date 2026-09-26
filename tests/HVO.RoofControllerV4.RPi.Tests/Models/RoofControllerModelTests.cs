using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Models;

[TestClass]
public sealed class RoofControllerModelTests
{
    [TestMethod]
    public void RoofControllerHostOptionsV4_ShouldUseExpectedDefaults()
    {
        var options = new RoofControllerHostOptionsV4();

        options.RestartOnFailureWaitTime.Should().Be(10);

        options.RestartOnFailureWaitTime = 3;
        options.RestartOnFailureWaitTime.Should().Be(3);

        var clone = options with { RestartOnFailureWaitTime = 7 };
        clone.RestartOnFailureWaitTime.Should().Be(7);
    }

    [TestMethod]
    public void RoofStatusResponse_ShouldExposeProvidedValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var response = new RoofStatusResponse(
            RoofControllerStatus.Open,
            IsMoving: true,
            LastStopReason: RoofControllerStopReason.NormalStop,
            LastTransitionUtc: timestamp,
            IsWatchdogActive: false,
            WatchdogSecondsRemaining: 12.5,
            IsAtSpeed: true,
            IsUsingPhysicalHardware: true,
            IsIgnoringPhysicalLimitSwitches: true);

        response.Status.Should().Be(RoofControllerStatus.Open);
        response.IsMoving.Should().BeTrue();
        response.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        response.LastTransitionUtc.Should().Be(timestamp);
        response.IsWatchdogActive.Should().BeFalse();
        response.WatchdogSecondsRemaining.Should().Be(12.5);
        response.IsAtSpeed.Should().BeTrue();
        response.IsUsingPhysicalHardware.Should().BeTrue();
    response.IsIgnoringPhysicalLimitSwitches.Should().BeTrue();

        var updated = response with { IsMoving = false };
        updated.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public void RoofControllerOptionsV4_ClearFaultAlias_ShouldMirrorRelayId()
    {
        var options = new RoofControllerOptionsV4
        {
            ClearFaultRelayId = 4
        };

#pragma warning disable CS0618 // Accessing obsolete alias intentionally to validate behavior
        options.ClearFault.Should().Be(4);
        options.ClearFault = 2;
#pragma warning restore CS0618

        options.ClearFaultRelayId.Should().Be(2);
    }
}
