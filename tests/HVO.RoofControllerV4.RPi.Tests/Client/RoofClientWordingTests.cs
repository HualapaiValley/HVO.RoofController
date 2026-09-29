using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Pins the wording every client shares (#44). Stop in particular reads the same in the web console, the CLI, the kiosk
/// and the Mac app; a change here is deliberate, and made once, in the client library.
/// </summary>
[TestClass]
public sealed class RoofClientWordingTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "..", ".."));

    private const string Unexpected = "The controller reported an unexpected error.";

    [TestMethod]
    public void TheStopWording_IsPinned()
    {
        RoofStopText.ButtonLabel.Should().Be("Stop roof");
        RoofStopText.AlwaysAvailable.Should().Be("Stop is always available.");
        RoofStopText.Sending.Should().Be("Stop sent. Waiting for the controller…");
        RoofStopText.AcknowledgedVerified.Should().Be("Stop acknowledged. Relay register verified de-energized.");
        RoofStopText.Acknowledged.Should().Be("Stop acknowledged by the controller.");
        RoofStopText.AcknowledgedUnverified.Should().Be(
            "Stop acknowledged, but the relay register could not be verified. Confirm at the roof that the motor has stopped.");
        RoofStopText.SentUnverified.Should().Be(
            "Stop sent, but the relay register could not be verified. Confirm at the roof that the motor has stopped. [RelayStateUnverified]");
        RoofStopText.UseRoofStop.Should().Be("Use the stop control at the roof.");
        RoofStopText.SignedOut.Should().Be(
            "Stop was not sent because the session is signed out. Sign in again, or use the stop control at the roof.");
        RoofStopText.Failed(RoofText.Unreachable).Should().Be("Stop failed: The controller could not be reached. Use the stop control at the roof.");
        RoofText.Unreachable.Should().Be("The controller could not be reached.");
        RoofText.TimedOut.Should().Be("The controller did not answer in time.");
    }

    [TestMethod]
    public void AStop_ClaimsOnlyWhatTheControllerSaid()
    {
        RoofStopText.Classify(true, RoofStatus(RoofRelayRegisterState.Verified), null, "")
            .Should().Be((RoofStopOutcome.Acknowledged, RoofStopText.AcknowledgedVerified));
        RoofStopText.Classify(true, RoofStatus(RoofRelayRegisterState.Unverified), null, "")
            .Should().Be((RoofStopOutcome.RelayUnverified, RoofStopText.AcknowledgedUnverified));
        RoofStopText.Classify(true, RoofStatus(RoofRelayRegisterState.Unknown), null, "")
            .Should().Be((RoofStopOutcome.Acknowledged, RoofStopText.Acknowledged));
        RoofStopText.Classify(true, null, null, "")
            .Should().Be((RoofStopOutcome.Acknowledged, RoofStopText.Acknowledged), "an answer that could not be read makes no relay claim");
        RoofStopText.Classify(false, null, RoofControllerErrorCode.RelayStateUnverified, "ignored")
            .Should().Be((RoofStopOutcome.RelayUnverified, RoofStopText.SentUnverified));
        RoofStopText.Classify(false, RoofStatus(RoofRelayRegisterState.Verified), RoofControllerErrorCode.ShuttingDown, "Refused.")
            .Should().Be((RoofStopOutcome.Failed, "Stop failed: Refused. Use the stop control at the roof."), "a refused stop is never reported as verified");
    }

    [TestMethod]
    public void TheWebConsole_UsesTheSharedStopWording()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "HVO.RoofControllerV4.RPi", "Components", "App.razor"));
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "HVO.RoofControllerV4.RPi", "wwwroot", "js", "console-stop.js"));

        app.Should().Contain($"</i> {RoofStopText.ButtonLabel}</button>");
        script.Should().Contain(RoofStopText.Sending)
            .And.Contain(RoofStopText.SignedOut)
            .And.Contain($"\" {RoofStopText.UseRoofStop}\"")
            .And.Contain("`Stop failed: ${reason}${useRoofStop}`")
            .And.Contain(RoofText.Unreachable)
            .And.Contain(RoofText.TimedOut);
    }

    [TestMethod]
    public void EveryErrorCode_HasItsOwnWording()
    {
        foreach (var code in Enum.GetValues<RoofControllerErrorCode>().Where(code => code != RoofControllerErrorCode.Unknown))
        {
            RoofText.DescribeErrorCode(code).Should().NotBe(Unexpected, "{0} needs its own wording", code);
            RoofText.DescribeRefusal(409, code, code.ToString()).Should().Be($"{RoofText.DescribeErrorCode(code)} [{code}]");
        }

        RoofText.DescribeErrorCode(RoofControllerErrorCode.Unknown).Should().Be(Unexpected);
    }

    [TestMethod]
    public void EveryStopReasonAndPosition_HasWording()
    {
        foreach (var reason in Enum.GetValues<RoofControllerStopReason>())
        {
            RoofText.DescribeStopReason(reason).Should().NotBe(reason.ToString(), "{0} needs its own wording", reason);
        }

        foreach (var status in Enum.GetValues<RoofControllerStatus>().Where(status => status != RoofControllerStatus.Unknown))
        {
            RoofText.DescribePosition(status).Should().NotBe("Unknown", "{0} needs its own wording", status);
        }

        RoofText.DescribePosition(RoofControllerStatus.PartiallyClose).Should().Be("Partially closed");
    }

    [TestMethod]
    [DataRow(400, "The request was invalid.")]
    [DataRow(401, "Not signed in, or the credential is no longer valid. Sign in again.")]
    [DataRow(403, "Your role does not permit this.")]
    [DataRow(404, "The controller does not offer this. It may be an older version.")]
    [DataRow(408, "The controller did not answer in time.")]
    [DataRow(429, "Too many requests. Wait, then try again.")]
    [DataRow(503, "The controller is not ready. Try again shortly.")]
    [DataRow(500, Unexpected)]
    [DataRow(502, Unexpected)]
    [DataRow(418, "The controller refused the request (HTTP 418).")]
    public void ARefusalWithoutACode_IsDescribedByItsStatus(int status, string text)
        => RoofText.DescribeRefusal(status, null, null).Should().Be(text);

    [TestMethod]
    public void ARefusalBeforeTheApi_IsDescribedByItsOwnCode()
    {
        RoofText.DescribeRefusal(403, null, "https_required").Should().Be("The controller requires HTTPS from this network. [https_required]");
        RoofText.DescribeRefusal(403, null, "origin_not_allowed").Should().Be("The controller refused a request from this page's origin. [origin_not_allowed]");
        RoofText.DescribeRefusal(403, null, "some_future_code").Should().Be("Your role does not permit this.");
    }

    [TestMethod]
    public void AFailure_IsDescribedWithoutItsInternals()
    {
        RoofText.DescribeFailure(new HttpRequestException("socket 10.0.0.5:443 reset")).Should().Be(RoofText.Unreachable);
        RoofText.DescribeFailure(new TimeoutException("internal")).Should().Be(RoofText.TimedOut);
        RoofText.DescribeFailure(new TaskCanceledException("t", new TimeoutException())).Should().Be(RoofText.TimedOut);
        RoofText.DescribeFailure(new InvalidOperationException("stack detail")).Should().Be(Unexpected);
        RoofText.DescribeFailure(null).Should().Be(Unexpected);
    }

    private static RoofStatusResponse RoofStatus(RoofRelayRegisterState state)
        => Controllers.RoofServiceMock.Snapshot(relayState: state);
}
