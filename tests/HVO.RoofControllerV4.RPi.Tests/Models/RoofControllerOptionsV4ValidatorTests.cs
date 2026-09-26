using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Models;

[TestClass]
public sealed class RoofControllerOptionsV4ValidatorTests
{
    [TestMethod]
    public void Validate_ShouldReturnSuccess_ForDefaultOptions()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4();

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenOptionsAreNull()
    {
        var validator = new RoofControllerOptionsV4Validator();

        var result = validator.Validate("Test", null!);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainSingle().Which.Should().Be("Options instance is null");
    }

    [TestMethod]
    public void Validate_ShouldFail_ForInvalidRelayIds()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            OpenRelayId = 0,
            CloseRelayId = 5,
            ClearFaultRelayId = 6,
            StopRelayId = 7
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("OpenRelayId must be"));
        result.Failures.Should().Contain(f => f.Contains("CloseRelayId must be"));
        result.Failures.Should().Contain(f => f.Contains("ClearFaultRelayId must be"));
        result.Failures.Should().Contain(f => f.Contains("StopRelayId must be"));
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenRelayIdsNotUnique()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            OpenRelayId = 1,
            CloseRelayId = 1,
            ClearFaultRelayId = 3,
            StopRelayId = 4
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("must be unique"));
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenWatchdogOrIntervalsInvalid()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            SafetyWatchdogTimeout = TimeSpan.Zero,
            PeriodicVerificationInterval = TimeSpan.Zero,
            EnablePeriodicVerificationWhileMoving = true,
            EnableDigitalInputPolling = false
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("SafetyWatchdogTimeout must be between"));
        result.Failures.Should().Contain(f => f.Contains("PeriodicVerificationInterval must be between"));
    }

    [TestMethod]
    public void Validate_ShouldSucceed_WhenOnlyPeriodicVerificationSupervisesInputs()
    {
        // Periodic verification is the software fallback when polling is disabled (CORE-04), so this is valid.
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            EnableDigitalInputPolling = false,
            EnablePeriodicVerificationWhileMoving = true
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenNeitherPollingNorPeriodicVerificationIsEnabled()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            EnableDigitalInputPolling = false,
            EnablePeriodicVerificationWhileMoving = false
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("At least one of EnableDigitalInputPolling or EnablePeriodicVerificationWhileMoving"));
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenLeaseIsNotShorterThanWatchdog()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(30),
            OperatorLeaseTimeout = TimeSpan.FromSeconds(30)
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("OperatorLeaseTimeout must be shorter than SafetyWatchdogTimeout"));
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenPeriodicIntervalExceedsWatchdog()
    {
        var validator = new RoofControllerOptionsV4Validator();
        var options = new RoofControllerOptionsV4
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(5),
            PeriodicVerificationInterval = TimeSpan.FromSeconds(5)
        };

        var result = validator.Validate(string.Empty, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PeriodicVerificationInterval must be shorter than SafetyWatchdogTimeout"));
    }
}
