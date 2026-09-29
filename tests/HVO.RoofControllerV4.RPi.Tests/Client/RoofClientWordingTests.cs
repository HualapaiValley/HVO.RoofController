using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web.Roof;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Pins the wording every client shares (#44). Stop in particular reads the same in the web UI, the CLI, the kiosk and
/// the Mac app; a change here is deliberate, and made once, in the client library.
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
        RoofStopText.KeyRefused.Should().Be(
            "Stop was not sent because the controller did not accept the key. Use the stop control at the roof.");
        RoofStopText.PageSignedOut.Should().Be(
            "Stop was not sent because this page is signed out. Reload the page to sign in again, or use the stop control at the roof.");
        RoofStopText.PageOutOfDate.Should().Be(
            "Stop was not sent because this page is out of date. Reload the page, or use the stop control at the roof.");
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
    public void TheWebUi_UsesTheSharedStopWording()
    {
        var web = Path.Combine(RepositoryRoot, "src", "HVO.RoofControllerV4.Web");
        var app = File.ReadAllText(Path.Combine(web, "Components", "App.razor"));
        var page = File.ReadAllText(Path.Combine(web, "Components", "Pages", "Dashboard.razor"));
        var script = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "stop.js"));

        app.Should().Contain("</i> @RoofStopText.ButtonLabel").And.Contain("data-web-stop-texts=\"@WebStopTexts.Json\"");
        page.Should().Contain("@RoofStopText.ButtonLabel").And.Contain("@RoofStopText.AlwaysAvailable").And.NotContain("Stop is always");
        foreach (var wording in new[] { "Stop failed", "Stop was not sent", "Stop sent", "Stop acknowledged", "Use the stop control", "Sign in" })
        {
            script.Should().NotContain(wording, "the script shows only the texts the server renders into the page");
        }
    }

    [TestMethod]
    public void TheWebUisStopTexts_SayWhatEveryOtherClientSays()
    {
        using var document = JsonDocument.Parse(WebStopTexts.Json);
        var texts = document.RootElement;
        string Text(JsonElement parent, string name) => parent.GetProperty(name).GetString()!;

        Text(texts, "sending").Should().Be(RoofStopText.Sending);
        Text(texts, "signedOut").Should().Be(RoofStopText.PageSignedOut, "the page has no sign-in of its own");
        Text(texts, "timedOut").Should().Be("Stop failed: The web UI did not answer in time. Use the stop control at the roof.");
        Text(texts, "unreachable").Should().Be("Stop failed: The web UI could not be reached. Use the stop control at the roof.");

        var codes = texts.GetProperty("codes");
        foreach (var code in Enum.GetValues<RoofControllerErrorCode>())
        {
            Text(codes, code.ToString()).Should().Be(RoofStopText.Failed(RoofText.DescribeRefusal(409, code, code.ToString())));
        }

        Text(codes, "origin_not_allowed").Should().Be(
            "Stop failed: The web UI refused a request from this page's origin. [origin_not_allowed] Use the stop control at the roof.");

        // The script's lookup, for every status it can meet: its own wording, then any server error, then the rest by number.
        var statuses = texts.GetProperty("statuses");
        for (var status = 400; status < 600; status++)
        {
            if (status == 401)
            {
                continue;
            }

            var key = status.ToString(CultureInfo.InvariantCulture);
            var shown = statuses.TryGetProperty(key, out var own) ? own.GetString()
                : status >= 500 ? Text(texts, "serverError")
                : Text(texts, "other").Replace(WebStopTexts.StatusPlaceholder, key, StringComparison.Ordinal);
            shown.Should().Be(RoofStopText.Failed(RoofText.DescribeRefusal(status, null, null)), "HTTP {0}", status);
        }

        Text(statuses, "503").Should().Be("Stop failed: The controller is not ready. Try again shortly. Use the stop control at the roof.");
        Text(texts, "other").Should().Be("Stop failed: The controller refused the request (HTTP {status}). Use the stop control at the roof.");
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
        RoofText.DescribeRefusal(403, null, "some_future_code").Should().Be("Your role does not permit this.");
    }

    [TestMethod]
    public void AFailure_IsDescribedWithoutItsInternals()
    {
        RoofText.DescribeFailure(new HttpRequestException("socket 10.0.0.5:443 reset")).Should().Be(RoofText.Unreachable);
        RoofText.DescribeFailure(new TimeoutException("internal")).Should().Be(RoofText.TimedOut);
        RoofText.DescribeFailure(new TaskCanceledException("t", new TimeoutException())).Should().Be(RoofText.TimedOut);
        RoofText.DescribeFailure(new RoofProtocolException("unexpected token '<' at 0")).Should().Be(RoofText.AnswerUnreadable);
        RoofText.DescribeFailure(new InvalidOperationException("stack detail")).Should().Be(Unexpected);
        RoofText.DescribeFailure(null).Should().Be(Unexpected);
    }

    private static RoofStatusResponse RoofStatus(RoofRelayRegisterState state)
        => Controllers.RoofServiceMock.Snapshot(relayState: state);
}
