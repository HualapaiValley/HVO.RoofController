using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The words every client uses for a status: the same rows in the same order, nothing claimed that the controller did
/// not send, and times in UTC.
/// </summary>
[TestClass]
public sealed class RoofStatusTextTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 12, 34, 56, TimeSpan.Zero);

    [TestMethod]
    public void DescribeRows_ListsEveryPart_InOrder()
    {
        var status = RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with
        {
            LastStopReason = RoofControllerStopReason.LimitSwitchReached,
            LastTransitionUtc = At,
            SnapshotUtc = At.AddSeconds(4),
            StatusVersion = 42,
            IsOpenLimitActive = false,
            IsClosedLimitActive = null,
            IsDriveFaultActive = false,
            LeaseSecondsRemaining = 7.6,
            WatchdogSecondsRemaining = 59.4,
            ControllerName = "roof-pi",
            HatMode = RoofHatMode.Physical,
            LastError = "Relay 3 read back high",
        };

        RoofStatusText.DescribeRows(status).Select(row => $"{row.Label}: {row.Value}").ToList().Should().Equal(
            "Roof: Opening, commanded to open",
            "Last stop: Limit switch reached, 2026-09-29 12:34:56Z",
            "Fault: none",
            "Limits: open inactive, closed unknown",
            "Drive fault: inactive",
            "Inputs: read OK",
            "Relays: register read-back matched",
            "Lease: 8 s left",
            "Watchdog: 59 s left",
            "Controller: roof-pi, physical HAT, snapshot 42 at 2026-09-29 12:35:00Z",
            "Last error: Relay 3 read back high");
    }

    [TestMethod]
    public void DescribeRows_LeavesOutWhatTheControllerDidNotSend()
    {
        var status = RoofServiceMock.Snapshot() with { LastTransitionUtc = null, LastError = "  ", ControllerName = null, HatMode = RoofHatMode.Emulated };

        RoofStatusText.DescribeRows(status).Select(row => row.Label).ToList()
            .Should().Equal("Roof", "Last stop", "Fault", "Limits", "Drive fault", "Inputs", "Relays", "Controller");
        RoofStatusText.DescribeLastStop(status).Should().Be("Stopped by operator");
        RoofStatusText.DescribeController(status).Should().StartWith("controller, emulated HAT, snapshot 11 at ");
    }

    [TestMethod]
    public void DescribeRoof_SaysWhenTheControllerIsNotReady()
    {
        RoofStatusText.DescribeRoof(RoofServiceMock.Snapshot()).Should().Be("Closed");
        RoofStatusText.DescribeRoof(RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing)).Should().Be("Closing, commanded to close");
        RoofStatusText.DescribeRoof(RoofServiceMock.Snapshot() with { IsInitialized = false }).Should().Be("Closed (controller not initialized)");
        RoofStatusText.DescribeRoof(RoofServiceMock.Snapshot() with { IsShuttingDown = true }).Should().Be("Closed (controller shutting down)");
    }

    [TestMethod]
    public void DescribeFault_SaysClearing_ThenLatched_WithTheReasonWhenSent()
    {
        var status = RoofServiceMock.Snapshot();

        RoofStatusText.DescribeFault(status).Should().Be("none");
        RoofStatusText.DescribeFault(status with { IsFaultLatched = true }).Should().Be("LATCHED");
        RoofStatusText.DescribeFault(status with { IsFaultLatched = true, LatchedFaultReason = RoofControllerStopReason.DriveFault })
            .Should().Be("LATCHED: Drive fault input active");
        RoofStatusText.DescribeFault(status with { IsFaultLatched = true, IsClearFaultInProgress = true }).Should().Be("clearing");
    }

    [TestMethod]
    public void DescribeRelays_ClaimsOnlyTheRegister_AndCountsFailingReads()
    {
        RoofStatusText.DescribeRelays(RoofServiceMock.Snapshot()).Should().Be("register read-back matched");
        RoofStatusText.DescribeRelays(RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified))
            .Should().Be("UNVERIFIED: confirm at the roof that the motor has stopped");
        RoofStatusText.DescribeRelays(RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unknown) with { RelayRegisterReadsHealthy = false, ConsecutiveRelayReadFailures = 3 })
            .Should().Be("not read back yet; register reads failing (3 in a row)");
        RoofStatusText.DescribeRelays(RoofServiceMock.Snapshot() with { RelayRegisterReadsHealthy = false })
            .Should().Be("register read-back matched; register reads failing");
    }

    [TestMethod]
    public void DescribeInputs_AndEachInput_SayUnknownForWhatCouldNotBeRead()
    {
        RoofStatusText.DescribeInputs(RoofServiceMock.Snapshot()).Should().Be("read OK");
        RoofStatusText.DescribeInputs(RoofServiceMock.Snapshot() with { InputsHealthy = false, ConsecutiveInputReadFailures = 2 }).Should().Be("reads failing (2 in a row)");
        RoofStatusText.DescribeInputs(RoofServiceMock.Snapshot() with { InputsHealthy = false }).Should().Be("reads failing");
        RoofStatusText.DescribeInput(true).Should().Be("active");
        RoofStatusText.DescribeInput(false).Should().Be("inactive");
        RoofStatusText.DescribeInput(null).Should().Be("unknown");
    }

    [TestMethod]
    public void DescribeModeWarnings_WarnWhenTheRoofDoesNotMove_OrTheLimitsAreIgnored()
    {
        RoofStatusText.DescribeModeWarnings(RoofServiceMock.Snapshot() with { HatMode = RoofHatMode.Physical }).Should().BeEmpty();
        RoofStatusText.DescribeModeWarnings(RoofServiceMock.Snapshot() with { HatMode = RoofHatMode.Emulated }).Should().Equal(RoofStatusText.EmulatedHat);
        RoofStatusText.DescribeModeWarnings(RoofServiceMock.Snapshot() with { HatMode = RoofHatMode.Simulation, IsIgnoringPhysicalLimitSwitches = true })
            .Should().Equal(RoofStatusText.Simulation, RoofStatusText.LimitsIgnored);
        RoofStatusText.EmulatedHat.Should().Contain("The observatory roof does not move.");
        RoofStatusText.Simulation.Should().Contain("The observatory roof does not move.");
    }

    [TestMethod]
    [DataRow(RoofHatMode.Unknown, false)]
    [DataRow(RoofHatMode.Physical, true)]
    [DataRow(RoofHatMode.Emulated, false)]
    [DataRow(RoofHatMode.Simulation, true)]
    public void DescribeModeWarnings_FromTheAnonymousModeRead_MatchTheStatus(RoofHatMode hatMode, bool ignoringLimits)
    {
        var status = RoofServiceMock.Snapshot() with { HatMode = hatMode, IsIgnoringPhysicalLimitSwitches = ignoringLimits };

        RoofStatusText.DescribeModeWarnings(new RoofModeResponse(hatMode, ignoringLimits))
            .Should().Equal(RoofStatusText.DescribeModeWarnings(status));
    }

    [TestMethod]
    public void DescribeHat_NamesEveryMode()
    {
        foreach (var mode in Enum.GetValues<RoofHatMode>())
        {
            RoofStatusText.DescribeHat(mode).Should().NotBeNullOrWhiteSpace();
        }

        RoofStatusText.DescribeHat((RoofHatMode)99).Should().Be("HAT not reported");
    }

    [TestMethod]
    public void Time_IsUtc_ToTheSecond_MarkedZ()
    {
        RoofStatusText.Time(new DateTimeOffset(2026, 9, 29, 5, 34, 56, 789, TimeSpan.FromHours(-7))).Should().Be("2026-09-29 12:34:56Z");
    }

    [TestMethod]
    [DataRow(12.4, "12 s")]
    [DataRow(12.6, "13 s")]
    [DataRow(0.0, "0 s")]
    [DataRow(-3.0, "0 s")]
    public void Seconds_AreWhole_AndNeverNegative(double seconds, string expected)
        => RoofStatusText.Seconds(seconds).Should().Be(expected);
}
