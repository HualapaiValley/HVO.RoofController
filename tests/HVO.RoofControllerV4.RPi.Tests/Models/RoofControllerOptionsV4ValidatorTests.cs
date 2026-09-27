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

    [TestMethod]
    [DataRow(0.5, true)]
    [DataRow(60d, true)]
    [DataRow(0.49, false)]
    [DataRow(60.1, false)]
    public void Validate_DriveStopConfirmationTimeout_MustBeInRange(double seconds, bool valid)
    {
        var options = new RoofControllerOptionsV4 { DriveStopConfirmationTimeout = TimeSpan.FromSeconds(seconds) };

        var result = new RoofControllerOptionsV4Validator().Validate(string.Empty, options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.Failures.Should().ContainSingle(f => f.Contains("DriveStopConfirmationTimeout must be between 0.5 and 60 seconds"));
        }
    }

    [TestMethod]
    [DataRow(0.5, true)]
    [DataRow(60d, true)]
    [DataRow(0.49, false)]
    [DataRow(60.1, false)]
    public void Validate_DepartureReleaseTimeout_MustBeInRange(double seconds, bool valid)
    {
        var options = new RoofControllerOptionsV4
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(90),
            DepartureReleaseTimeout = TimeSpan.FromSeconds(seconds)
        };

        var result = new RoofControllerOptionsV4Validator().Validate(string.Empty, options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.Failures.Should().ContainSingle(f => f.Contains("DepartureReleaseTimeout must be between 0.5 and 60 seconds"));
        }
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenDepartureReleaseTimeoutIsNotShorterThanWatchdog()
    {
        var options = new RoofControllerOptionsV4
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(30),
            DepartureReleaseTimeout = TimeSpan.FromSeconds(30)
        };

        var result = new RoofControllerOptionsV4Validator().Validate(string.Empty, options);

        result.Failures.Should().ContainSingle(f => f.Contains("DepartureReleaseTimeout must be shorter than SafetyWatchdogTimeout"));
    }

    [TestMethod]
    public void Validate_ShouldFail_WhenDepartureReleaseTimeoutIsNotLongerThanTheDebounce()
    {
        var options = new RoofControllerOptionsV4
        {
            LimitSwitchDebounce = TimeSpan.FromMilliseconds(500),
            DepartureReleaseTimeout = TimeSpan.FromMilliseconds(500)
        };

        var result = new RoofControllerOptionsV4Validator().Validate(string.Empty, options);

        result.Failures.Should().ContainSingle(f => f.Contains("DepartureReleaseTimeout must be longer than LimitSwitchDebounce"));
    }

    [TestMethod]
    public void Validate_ShouldSucceed_WhenTheNewTimeoutsAreUnset()
    {
        var options = new RoofControllerOptionsV4 { DriveStopConfirmationTimeout = null, DepartureReleaseTimeout = null };

        new RoofControllerOptionsV4Validator().Validate(string.Empty, options).Succeeded.Should().BeTrue();
    }
}
