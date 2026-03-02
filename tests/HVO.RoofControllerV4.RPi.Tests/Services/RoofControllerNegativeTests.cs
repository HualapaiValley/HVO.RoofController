using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Negative / defensive tests focused on invalid or fault scenarios and idempotency.
/// These tests complement the positive-path relay behavior suite.
/// </summary>
[TestClass]
public class RoofControllerNegativeTests
{
    private class TestableRoofControllerService : RoofControllerServiceV4
    {
        public TestableRoofControllerService(IOptions<RoofControllerOptionsV4> opts, FakeRoofHat hat)
            : base(new NullLogger<RoofControllerServiceV4>(), opts, hat) { }

        public void SimFaultRaw(bool high) => OnFaultNotificationChanged(high);
        public void SimForwardLimitRaw(bool high) => OnForwardLimitSwitchChanged(high);
        public void SimReverseLimitRaw(bool high) => OnReverseLimitSwitchChanged(high);

        // Expose protected relay setter for guard validation
        public void ForceRelayStates(bool stop, bool open, bool close) => SetRelayStatesAtomically(stop, open, close);
    }

    private static TestableRoofControllerService Create(FakeRoofHat hat)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(5);
        });
        return new TestableRoofControllerService(Options.Create(options), hat);
    }

    [TestMethod]
    public async Task Open_WithBothLimitsActive_ShouldFailAndSetErrorStatus()
    {
        // Arrange
        // Both limits active (NC -> raw LOW means triggered) => in1 LOW, in2 LOW
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        var result = svc.Open();

        // Assert
        result.IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00, "No relays energized when command refused");
    }

    [TestMethod]
    public async Task Close_WithBothLimitsActive_ShouldFailAndSetErrorStatus()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        var result = svc.Close();

        // Assert
        result.IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task MovementAttemptWhileFaultActive_ShouldBeRefused()
    {
        // Arrange
        // Mid travel (both HIGH) but fault HIGH
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, true, false);
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        // Open refused
        var open = svc.Open();

        // Assert
        open.IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
        hat.RelayMask.Should().Be(0x00);

        // Close also refused
        var close = svc.Close();
        close.IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
    }

    [TestMethod]
    public async Task RelayGuard_ShouldPreventSimultaneousOpenAndClose()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid travel
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        // Force an invalid request (open & close true). Guard should neutralize open/close and leave STOP energized or not based on logic.
        svc.ForceRelayStates(stop: true, open: true, close: true);

        // Assert
        // After guard: open & close must NOT both be present
        var mask = hat.RelayMask;
        (mask & 0x06).Should().NotBe(0x06, "Open and Close relays must never be energized simultaneously");
    }

    [TestMethod]
    public async Task Stop_ShouldBeIdempotent_WhenAlreadyStopped()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid travel -> initializes as Stopped
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        var first = svc.Stop(RoofControllerStopReason.NormalStop);
        first.IsSuccessful.Should().BeTrue();
        var mask1 = hat.RelayMask;
        mask1.Should().Be(0x00);

        var second = svc.Stop(RoofControllerStopReason.NormalStop);

        // Assert
        second.IsSuccessful.Should().BeTrue();
        hat.RelayMask.Should().Be(mask1);
        svc.Status.Should().Be(RoofControllerStatus.Stopped);
    }

    [TestMethod]
    public async Task BothLimitsError_ShouldContinueToRefuseSubsequentCommands()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false); // both limits
        var svc = Create(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act & Assert
        svc.Open().IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Error);
        svc.Close().IsSuccessful.Should().BeFalse();
        svc.Status.Should().Be(RoofControllerStatus.Error);
        hat.RelayMask.Should().Be(0x00);
    }
}
