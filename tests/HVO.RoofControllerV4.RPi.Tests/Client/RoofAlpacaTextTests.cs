using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The shared Alpaca wording for Open grants (#220, phase 4), pinned: every UI says these, so a change is deliberate. The
/// Close wording stays what phase 3 pinned in the UIs' own tests.
/// </summary>
[TestClass]
public sealed class RoofAlpacaTextTests
{
    [TestMethod]
    public void TheGrantWording_IsTheRefusedCommands()
    {
        RoofAlpacaText.GrantOpenLabel.Should().Be("Allow this Open once");
        RoofAlpacaText.GrantLabel(RoofAlpacaCommandKind.Open).Should().Be("Allow this Open once");
        RoofAlpacaText.GrantLabel(RoofAlpacaCommandKind.Close).Should().Be(RoofAlpacaText.GrantCloseLabel).And.Be("Allow this Close once");

        RoofAlpacaText.GrantQuestionTitleFor(RoofAlpacaCommandKind.Open).Should().Be("Allow one Alpaca Open?");
        RoofAlpacaText.GrantQuestionTitleFor(RoofAlpacaCommandKind.Close).Should().Be(RoofAlpacaText.GrantQuestionTitle);
        RoofAlpacaText.GrantConfirmLabelFor(RoofAlpacaCommandKind.Open).Should().Be("Allow one Open");
        RoofAlpacaText.GrantConfirmLabelFor(RoofAlpacaCommandKind.Close).Should().Be(RoofAlpacaText.GrantConfirmLabel);
        RoofAlpacaText.GrantCancelledFor(RoofAlpacaCommandKind.Open).Should().Be("No Open was allowed: the question was cancelled.");
        RoofAlpacaText.GrantCancelledFor(RoofAlpacaCommandKind.Close).Should().Be(RoofAlpacaText.GrantCancelled);
        RoofAlpacaText.GrantWithdrawnFor(RoofAlpacaCommandKind.Open).Should().Be("No Open was allowed: Stop withdrew the question.");
        RoofAlpacaText.GrantWithdrawnFor(RoofAlpacaCommandKind.Close).Should().Be(RoofAlpacaText.GrantWithdrawn);
        RoofAlpacaText.DescribeRevoked(RoofAlpacaCommandKind.Open).Should().Be("The grant for one Open is revoked.");
    }

    [TestMethod]
    public void TheOpenReasons_AreSaidInWords()
    {
        RoofAlpacaText.DescribeReasons(["weather-unsafe:Sky", "weather-unknown:Cloud", "no-required-weather-source", "no-blocking-scopes", "scope-unknown:Main"])
            .Should().Be("Weather monitor 'Sky' reports unsafe weather; Weather monitor 'Cloud' state unknown; no required weather source; "
                + "no telescope set to block the roof; Telescope 'Main' state unknown");
    }

    [TestMethod]
    public void AGrant_CanBeMadeFromARefusedOpen_WhoseEveryReasonAnOpenGrantLifts_AndFromNoCloseForTheWeather()
    {
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky", "scope-not-clear:Main", "no-blocking-scopes", "no-required-weather-source"))
            .Should().BeTrue();
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky", "stop-hold")).Should().BeFalse("no grant lifts the Stop hold");
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky") with { Acknowledged = true }).Should().BeFalse();
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky") with { Kind = "MidMotion" }).Should().BeFalse();
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Close, "weather-unsafe:Sky")).Should().BeFalse("the weather never gates a Close");
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Close, "no-blocking-scopes")).Should().BeFalse();
        RoofAlpacaText.CanGrantFrom(Refused(RoofAlpacaCommandKind.Close, "scope-unknown:Main")).Should().BeTrue();
    }

    [TestMethod]
    public void AnOpensGrant_IsUnavailableWhileTheControllerSaysSo_AndACloseIsNever()
    {
        var block = new RoofAlpacaStatus { OpenGrantUnavailable = "The safety interlock is Block." };

        RoofAlpacaText.GrantUnavailable(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky"), block).Should().Be("The safety interlock is Block.");
        RoofAlpacaText.GrantUnavailable(Refused(RoofAlpacaCommandKind.Close, "scope-unknown:Main"), block).Should().BeNull();
        RoofAlpacaText.GrantUnavailable(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky"), new RoofAlpacaStatus()).Should().BeNull();
        RoofAlpacaText.GrantUnavailable(Refused(RoofAlpacaCommandKind.Open, "weather-unsafe:Sky"), null).Should().BeNull();
    }

    [TestMethod]
    public void TheOpenGrantQuestion_SaysWhatItLifts_AndThatTheRoofStaysOpenIfTheWeatherTurns()
    {
        var alert = Refused(RoofAlpacaCommandKind.Open, "weather-unknown:Sky") with { ClientId = 7, ClientAddress = "192.0.2.50" };

        RoofAlpacaText.DescribeGrantQuestion(alert, 5).Should().Equal(
            "Allow one Alpaca Open despite: Weather monitor 'Sky' state unknown.",
            "It was refused for ClientID 7 at 192.0.2.50 (reported by the Alpaca server); any Alpaca client's next Open uses the grant.",
            "The grant lasts 5 min and is used up by one Open, which may still be refused for a new reason. It never bypasses the fault latch, "
                + "the interlocks, the limits, the motor protections, a person's operator lease or the Stop hold. It is logged with your name.",
            "Once open, the roof stays open if the weather turns: Alpaca closes it only when a client sends a Close.");
    }

    [TestMethod]
    public void SteppingAway_SaysWhatEveryGrantInForceAllows()
    {
        var open = Grant(RoofAlpacaCommandKind.Open, 290);
        var close = Grant(RoofAlpacaCommandKind.Close, 60);

        RoofAlpacaText.DescribeSteppedAway([open], RoofAlpacaText.ClickToTakeBack).Should().Be(
            "You stepped away from the operator lease: Alpaca may open the roof once in the next 5 min. Click or type on this page to take control back; that cancels the grant.");
        RoofAlpacaText.DescribeSteppedAway([close, open]).Should().Be(
            "You stepped away from the operator lease: Alpaca may open and close the roof once each in the next 5 min. Touch anything to take control back; that cancels the grants.");
        RoofAlpacaText.DescribeSteppedAway([close]).Should().Be(RoofAlpacaText.DescribeSteppedAway(close))
            .And.Be("You stepped away from the operator lease: Alpaca may close the roof once in the next 60 s. Touch anything to take control back; that cancels the grant.");
        RoofAlpacaText.DescribeSteppedAway([]).Should().Be(RoofAlpacaText.DescribeSteppedAway((RoofOverrideGrant?)null))
            .And.Be("You stepped away from the operator lease: Alpaca may move the roof. Touch anything to take control back.");
    }

    private static RoofAlpacaAlert Refused(RoofAlpacaCommandKind command, params string[] keys) => new()
    {
        Id = "1",
        Kind = "Refused",
        Command = command,
        Message = $"An Alpaca {command} was refused.",
        ReasonKeys = keys,
        Count = 1
    };

    private static RoofOverrideGrant Grant(RoofAlpacaCommandKind kind, double seconds) => new()
    {
        Id = kind.ToString(),
        Kind = kind,
        ReasonKeys = ["scope-unknown:Main"],
        GrantedBy = "ada",
        Ui = "session",
        SecondsRemaining = seconds
    };
}
