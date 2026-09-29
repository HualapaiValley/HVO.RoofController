using System;
using System.Collections.Generic;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static HVO.RoofControllerV4.RPi.Tests.Components.RoofConsoleHarness;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

[TestClass]
public class RoofConsoleRulesTests
{
    [TestMethod]
    public void ShouldApply_RejectsOlderVersion_AndAcceptsEqualOrNewer()
    {
        var current = Status(RoofControllerStatus.Open, 10);

        RoofConsoleRules.ShouldApply(null, current).Should().BeTrue();
        RoofConsoleRules.ShouldApply(current, Status(RoofControllerStatus.Closing, 9)).Should().BeFalse();
        RoofConsoleRules.ShouldApply(current, Status(RoofControllerStatus.Closing, 10)).Should().BeTrue();
        RoofConsoleRules.ShouldApply(current, Status(RoofControllerStatus.Closing, 11)).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldApply_AcceptsLowerVersion_FromNewControllerInstance()
    {
        var current = Status(RoofControllerStatus.Open, 500) with { ControllerInstanceId = "first" };
        var restarted = Status(RoofControllerStatus.Closed, 1) with { ControllerInstanceId = "second" };

        RoofConsoleRules.ShouldApply(current, restarted).Should().BeTrue();
        RoofConsoleRules.ShouldApply(current, restarted with { ControllerInstanceId = "first" }).Should().BeFalse();
    }

    [TestMethod]
    public void DetectSafetyAlert_IsNull_ForFirstSnapshot()
    {
        RoofConsoleRules.DetectSafetyAlert(null, Latched(RoofControllerStopReason.DriveFault, 1)).Should().BeNull();
    }

    [TestMethod]
    public void DetectSafetyAlert_ReportsNewlyLatchedFault_Once()
    {
        var moving = Status(RoofControllerStatus.Opening, 1);
        var latched = Latched(RoofControllerStopReason.DriveFault, 2);

        var alert = RoofConsoleRules.DetectSafetyAlert(moving, latched);

        alert.Should().NotBeNull();
        alert!.Title.Should().Be("Fault latched");
        alert.Message.Should().Contain("Drive fault input active");
        RoofConsoleRules.DetectSafetyAlert(latched, latched with { StatusVersion = 3 }).Should().BeNull();
    }

    [TestMethod]
    public void DetectSafetyAlert_IsNull_WhenFaultClears()
    {
        var latched = Latched(RoofControllerStopReason.DriveFault, 1);
        var cleared = latched with { StatusVersion = 2, IsFaultLatched = false, LatchedFaultReason = null };

        RoofConsoleRules.DetectSafetyAlert(latched, cleared).Should().BeNull();
    }

    [TestMethod]
    public void DetectSafetyAlert_IsNull_ForOperatorStop()
    {
        var moving = Status(RoofControllerStatus.Opening, 1);
        var stopped = Status(RoofControllerStatus.PartiallyOpen, 2) with { LastStopReason = RoofControllerStopReason.NormalStop };

        RoofConsoleRules.DetectSafetyAlert(moving, stopped).Should().BeNull();
    }

    [TestMethod]
    public void DetectSafetyAlert_ReportsSafetyStop_WhenMotionEndsForSafetyReason()
    {
        var moving = Status(RoofControllerStatus.Closing, 1);
        var stopped = Status(RoofControllerStatus.PartiallyClose, 2) with { LastStopReason = RoofControllerStopReason.OperatorLeaseExpired };

        var alert = RoofConsoleRules.DetectSafetyAlert(moving, stopped);

        alert.Should().NotBeNull();
        alert!.Title.Should().Be("Safety stop");
        alert.Message.Should().Be("Operator lease expired");
    }

    [TestMethod]
    [DataRow(RoofControllerStopReason.DriveNotRunning, "Drive not reporting running (IN4)")]
    [DataRow(RoofControllerStopReason.DepartureLimitNotReleased, "Starting limit did not release")]
    public void DetectSafetyAlert_ReportsTheDriveAndDepartureStops(RoofControllerStopReason reason, string description)
    {
        RoofConsoleRules.IsSafetyStopReason(reason).Should().BeTrue();
        RoofConsoleRules.DescribeStopReason(reason).Should().Be(description);
        var moving = Status(RoofControllerStatus.Opening, 1);

        var alert = RoofConsoleRules.DetectSafetyAlert(moving, Latched(reason, 2));

        alert.Should().NotBeNull();
        alert!.Title.Should().Be("Fault latched");
        alert.Message.Should().Contain(description);
    }

    [TestMethod]
    public void EveryStopReason_HasADescription()
    {
        foreach (var reason in Enum.GetValues<RoofControllerStopReason>())
        {
            RoofConsoleRules.DescribeStopReason(reason).Should().NotBe(reason.ToString(), $"{reason} needs operator wording");
        }
    }

    [TestMethod]
    public void ClassifyStop_Acknowledged_WhenRelayVerified()
    {
        var (outcome, message) = RoofConsoleRules.ClassifyStop(true, null, Status(RoofControllerStatus.Stopped, 1));

        outcome.Should().Be(RoofStopOutcome.Acknowledged);
        message.Should().Contain("verified de-energized");
    }

    [TestMethod]
    public void ClassifyStop_Acknowledged_WhenRelayStateUnknown()
    {
        var snapshot = Status(RoofControllerStatus.Stopped, 1) with { RelayRegisterState = RoofRelayRegisterState.Unknown };

        var (outcome, message) = RoofConsoleRules.ClassifyStop(true, null, snapshot);

        outcome.Should().Be(RoofStopOutcome.Acknowledged);
        message.Should().NotContain("verified de-energized");
    }

    [TestMethod]
    public void ClassifyStop_RelayUnverified_WhenAcceptedButRegisterUnverified()
    {
        var snapshot = Status(RoofControllerStatus.Stopped, 1) with { RelayRegisterState = RoofRelayRegisterState.Unverified };

        RoofConsoleRules.ClassifyStop(true, null, snapshot).Outcome.Should().Be(RoofStopOutcome.RelayUnverified);
    }

    [TestMethod]
    public void ClassifyStop_RelayUnverified_WhenControllerReportsRelayStateUnverified()
    {
        var error = new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "read-back failed");

        var (outcome, message) = RoofConsoleRules.ClassifyStop(false, error, null);

        outcome.Should().Be(RoofStopOutcome.RelayUnverified);
        message.Should().Contain("[RelayStateUnverified]");
    }

    [TestMethod]
    public void ClassifyStop_Failed_WithCode()
    {
        var error = new RoofControllerException(RoofControllerErrorCode.ShuttingDown, "internal detail");

        var (outcome, message) = RoofConsoleRules.ClassifyStop(false, error, null);

        outcome.Should().Be(RoofStopOutcome.Failed);
        message.Should().StartWith("Stop failed:").And.Contain("[ShuttingDown]").And.NotContain("internal detail");
    }

    [TestMethod]
    public void DescribeFailure_DoesNotLeakUnexpectedExceptionMessages()
    {
        var text = RoofConsoleRules.DescribeFailure(new InvalidOperationException("at Secret.Stack.Frame()"));

        text.Should().NotContain("Secret").And.Contain("server log");
    }

    [TestMethod]
    public void GetCommandedMotion_FallsBackToMovingStatus()
    {
        var legacy = Status(RoofControllerStatus.Closing, 0) with { CommandedMotion = RoofMotionDirection.None };

        RoofConsoleRules.GetCommandedMotion(legacy).Should().Be(RoofMotionDirection.Closing);
        RoofConsoleRules.GetCommandedMotion(Status(RoofControllerStatus.Open, 0)).Should().Be(RoofMotionDirection.None);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(0d, null)]
    [DataRow(-5d, null)]
    [DataRow(double.NaN, null)]
    [DataRow(0.6, 500d)]
    [DataRow(30d, 10000d)]
    [DataRow(600d, 30000d)]
    public void GetLeaseRenewalDelay_IsAThirdOfRemaining_WithinBounds(double? remaining, double? expectedMilliseconds)
    {
        var delay = RoofConsoleRules.GetLeaseRenewalDelay(remaining);

        if (expectedMilliseconds is null)
        {
            delay.Should().BeNull();
        }
        else
        {
            delay.Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds.Value));
        }
    }

    [TestMethod]
    public void ToPayload_HidesExceptionMessages_FromNonAdmins()
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["roof"] = new(HealthStatus.Unhealthy, "Relay unverified", TimeSpan.Zero, new InvalidOperationException("secret detail"), null)
            },
            TimeSpan.Zero);

        RoofConsoleRules.ToPayload(report, includeExceptions: false).Checks[0].Exception
            .Should().NotContain("secret detail").And.Contain("Administrators");
        RoofConsoleRules.ToPayload(report, includeExceptions: true).Checks[0].Exception.Should().Be("secret detail");
    }

    [TestMethod]
    [DataRow(null, "/")]
    [DataRow("", "/")]
    [DataRow("//evil.example", "/")]
    [DataRow("/\\evil.example", "/")]
    [DataRow("https://evil.example/", "/")]
    [DataRow("roof-control", "/")]
    [DataRow("/login?returnUrl=%2F", "/")]
    [DataRow("/LOGIN", "/")]
    [DataRow("/roof\r\nSet-Cookie:x", "/")]
    [DataRow("/roof-control", "/roof-control")]
    [DataRow("/roof-control?tab=camera", "/roof-control?tab=camera")]
    public void Login_SanitizeReturnUrl_OnlyAllowsLocalPaths(string? returnUrl, string expected)
    {
        Login.SanitizeReturnUrl(returnUrl).Should().Be(expected);
    }

    private static RoofStatusResponse Latched(RoofControllerStopReason reason, long version) =>
        Status(RoofControllerStatus.Stopped, version) with
        {
            LastStopReason = reason,
            IsFaultLatched = true,
            LatchedFaultReason = reason
        };
}
