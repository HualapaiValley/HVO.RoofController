using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// How the roof's state and the controller's refusals read in Alpaca (#220): the shutter, Slewing, and the fixed table
/// of error numbers.
/// </summary>
[TestClass]
public sealed class AlpacaMappingTests
{
    [TestMethod]
    public void EveryRoofState_ReadsAsAShutterState_AndOnlyMotionReadsAsMoving()
    {
        foreach (var status in Enum.GetValues<RoofControllerStatus>())
        {
            var expected = status switch
            {
                RoofControllerStatus.Open => DomeShutterState.Open,
                RoofControllerStatus.Closed => DomeShutterState.Closed,
                RoofControllerStatus.Opening => DomeShutterState.Opening,
                RoofControllerStatus.Closing => DomeShutterState.Closing,
                _ => DomeShutterState.Error,
            };

            DomeShutter.StateOf(RoofServiceMock.Snapshot(status)).Should().Be(expected, $"{status}");
        }
    }

    [TestMethod]
    [DataRow(RoofControllerStatus.Stopped)]
    [DataRow(RoofControllerStatus.PartiallyOpen)]
    [DataRow(RoofControllerStatus.PartiallyClose)]
    [DataRow(RoofControllerStatus.NotInitialized)]
    [DataRow(RoofControllerStatus.Unknown)]
    [DataRow(RoofControllerStatus.Error)]
    public void ARoofThatIsNotMoving_IsNeverReportedAsMoving(RoofControllerStatus status)
    {
        var snapshot = RoofServiceMock.Snapshot(status);

        DomeShutter.StateOf(snapshot).Should().Be(DomeShutterState.Error);
        DomeShutter.IsSlewing(snapshot).Should().BeFalse();
    }

    [TestMethod]
    public void Slewing_IsTheCommandedMotion_OrTheDriveStillRunningAfterAStop()
    {
        DomeShutter.IsSlewing(RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening)).Should().BeTrue();
        DomeShutter.IsSlewing(RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing)).Should().BeTrue();
        DomeShutter.IsSlewing(RoofServiceMock.Snapshot(RoofControllerStatus.PartiallyOpen) with { IsAtSpeed = true })
            .Should().BeTrue("the drive reports running (IN4) while the roof coasts after a stop");
        DomeShutter.IsSlewing(RoofServiceMock.Snapshot(RoofControllerStatus.Closed)).Should().BeFalse();
    }

    [TestMethod]
    public void EveryControllerCode_HasANumberFromTheTable()
    {
        foreach (var code in Enum.GetValues<RoofControllerErrorCode>())
        {
            AlpacaErrors.All.Should().Contain(AlpacaErrors.For(code), $"{code}");
            AlpacaErrors.For(code).Should().NotBe(AlpacaErrors.NotImplemented).And.NotBe(AlpacaErrors.NotConnected);
        }
    }

    [TestMethod]
    [DataRow(RoofControllerErrorCode.FaultLatched)]
    [DataRow(RoofControllerErrorCode.InterlockActive)]
    [DataRow(RoofControllerErrorCode.OperationInProgress)]
    [DataRow(RoofControllerErrorCode.SafetyInterlockActive)]
    [DataRow(RoofControllerErrorCode.SafetyOverrideRequired)]
    public void ARefusalForTheRoofsState_IsInvalidOperation(RoofControllerErrorCode code)
        => AlpacaErrors.For(code).Should().Be(AlpacaErrors.InvalidOperation);

    [TestMethod]
    public void TheControllersOwnFailures_HaveANumberEach()
    {
        var numbers = new[]
        {
            AlpacaErrors.For(RoofControllerErrorCode.NotInitialized),
            AlpacaErrors.For(RoofControllerErrorCode.ShuttingDown),
            AlpacaErrors.For(RoofControllerErrorCode.HardwareUnavailable),
            AlpacaErrors.For(RoofControllerErrorCode.RelayStateUnverified),
        };

        numbers.Should().OnlyHaveUniqueItems().And.OnlyContain(number => number >= 0x500 && number <= 0xFFF);
        numbers.Should().NotContain(AlpacaErrors.ControllerUnavailable);
    }

    [TestMethod]
    public void TheTable_HasNoNumberTwice_AndDriverNumbersInTheirRange()
    {
        AlpacaErrors.All.Should().OnlyHaveUniqueItems();
        AlpacaErrors.All.Where(number => number >= 0x500).Should().OnlyContain(number => number <= 0xFFF);
    }

    [TestMethod]
    public void AFailedRequest_HasTheNumberOfItsCause()
    {
        AlpacaRoof.ErrorNumberFor(new HttpRequestException("refused")).Should().Be(AlpacaErrors.ControllerUnavailable);
        AlpacaRoof.ErrorNumberFor(new TimeoutException()).Should().Be(AlpacaErrors.ControllerUnavailable);
        AlpacaRoof.ErrorNumberFor(Refusal(HttpStatusCode.Unauthorized, null)).Should().Be(AlpacaErrors.ControllerRefusedKey);
        AlpacaRoof.ErrorNumberFor(Refusal(HttpStatusCode.Forbidden, null)).Should().Be(AlpacaErrors.ControllerRefusedKey);
        AlpacaRoof.ErrorNumberFor(Refusal(HttpStatusCode.ServiceUnavailable, RoofControllerErrorCode.RelayStateUnverified)).Should().Be(AlpacaErrors.RelayStateUnverified);
        AlpacaRoof.ErrorNumberFor(Refusal(HttpStatusCode.InternalServerError, null)).Should().Be(AlpacaErrors.ControllerError);
        AlpacaRoof.ErrorNumberFor(new InvalidOperationException()).Should().Be(AlpacaErrors.ControllerError);
    }

    [TestMethod]
    public void TheStatusText_SaysWhyTheRoofReadsAsError()
    {
        var text = AlpacaDome.DescribeStatus(RoofServiceMock.Snapshot(RoofControllerStatus.Error, faultLatched: true) with
        {
            LatchedFaultReason = RoofControllerStopReason.DriveFault,
            LastError = "The drive reported a fault",
        });

        text.Should().StartWith("Shutter: Error.").And.Contain("LATCHED").And.Contain("The drive reported a fault");
    }

    [TestMethod]
    [DataRow("Speed too high.", "Last error: Speed too high.")]
    [DataRow("  Speed too high.  ", "Last error: Speed too high.")]
    [DataRow("Speed too high", "Last error: Speed too high.")]
    [DataRow("Is the drive powered?", "Last error: Is the drive powered?")]
    [DataRow("Relay state is unknown!", "Last error: Relay state is unknown!")]
    public void TheStatusText_EndsTheLastErrorWithOneFullStop_WhetherOrNotItHasItsOwn(string lastError, string expected)
    {
        // A latched drive fault with the conditions and the Alpaca state too: every sentence the action can append.
        var status = RoofServiceMock.Snapshot(RoofControllerStatus.Error, faultLatched: true) with
        {
            LatchedFaultReason = RoofControllerStopReason.DriveFault,
            LastError = lastError,
            Safety = new RoofSafetyStatus { State = RoofSafetyState.Unsafe, Reasons = ["Telescope 'Main' is not parked"], InterlockMode = RoofSafetyInterlockMode.Warn },
            Alpaca = new RoofAlpacaStatus(),
        };

        var text = AlpacaDome.DescribeStatus(status);

        Regex.Matches(text, Regex.Escape(expected)).Should().ContainSingle("the last error is said once, as the controller wrote it");
        text.Should().NotContain("..", "no sentence ends in two full stops").And.NotContain("?.").And.NotContain("!.");
        text.Should().Contain($"{expected} Safety: UNSAFE", "the next sentence follows the last error's own ending");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void TheStatusText_SaysNoLastError_WhenThereIsNone(string? lastError)
    {
        var text = AlpacaDome.DescribeStatus(RoofServiceMock.Snapshot() with { LastError = lastError });

        text.Should().NotContain("Last error").And.NotContain("..").And.EndWith(".");
    }

    private static RoofApiException Refusal(HttpStatusCode status, RoofControllerErrorCode? code)
        => new(status, code);
}
