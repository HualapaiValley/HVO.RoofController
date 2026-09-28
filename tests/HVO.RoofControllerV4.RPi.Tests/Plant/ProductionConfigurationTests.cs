using System;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Plant;

/// <summary>
/// The deployed <c>appsettings.json</c> against the documented wiring: the plant tests run with this configuration, so
/// it must stay valid and match the installation.
/// </summary>
[TestClass]
public class ProductionConfigurationTests
{
    [TestMethod]
    public void ProductionConfiguration_PassesTheOptionsValidator()
    {
        var options = ProductionOptions.Load();

        var result = new RoofControllerOptionsV4Validator().Validate(null, options);

        result.Succeeded.Should().BeTrue(result.FailureMessage);
    }

    [TestMethod]
    public void ProductionConfiguration_MatchesTheWiring()
    {
        var options = ProductionOptions.Load();

        options.UseNormallyClosedLimitSwitches.Should().BeFalse("IN1 and IN2 are on the ME-8108 NO pair, HIGH at the limit");
        options.LimitSwitchDebounce.Should().Be(TimeSpan.FromMilliseconds(25), "C3 assumes drive and motor noise settles inside it");
        options.FaultInputActiveHigh.Should().BeFalse("the drive relay (P140 = 3) opens on a trip, so IN3 is LOW when faulted");
        options.IgnorePhysicalLimitSwitches.Should().BeFalse();
        options.AllowIgnoringLimitSwitchesOnPhysicalHardware.Should().BeFalse();
        options.AtSpeedConfirmationTimeout.Should().Be(TimeSpan.FromSeconds(3), "IN4 carries the drive's run output (P142 = 1)");
        options.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromSeconds(150));
        options.MaxConsecutiveInputReadFailures.Should().Be(3);
        options.DepartureReleaseTimeout.Should().BeNull("it must be set against the release time with the installed P104 before it is turned on");
    }
}
