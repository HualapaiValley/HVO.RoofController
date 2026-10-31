using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Roof;
using static HVO.RoofControllerV4.RPi.Tests.Web.WebAlpacaSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The roof page's Alpaca panel (#220), from the controller's real Alpaca command state: which commands are on, the human
/// Stop hold and "Allow Alpaca again", the operator lease, grants with Revoke, alerts with Acknowledge and "Allow this
/// Close once", whose question is asked before anything is granted, and the controller's warnings.
/// </summary>
[TestClass]
public sealed class DashboardAlpacaTests
{
    [TestMethod]
    public async Task WithAlpacaCommandsOff_ThePanelIsTheModeAlone()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() =>
        {
            var panel = cut.Find("[data-testid=alpaca]");
            panel.ClassList.Should().Contain("rc2-alpaca--compact");
            panel.GetAttribute("data-mode").Should().Be(nameof(RoofAlpacaCommandMode.Off));
            Text(cut.Find("#rc2-alpaca-title")).Should().Be(RoofAlpacaText.Title);
            Text(cut.Find("[data-testid=alpaca-mode]")).Should().Be(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.Off));
            harness.Console.View.Status!.Alpaca!.Hold.Held.Should().BeTrue("the controller's start sets the hold");
            cut.FindAll("[data-testid=alpaca-hold], [data-testid=alpaca-lease], [data-testid=alpaca-grants], [data-testid=alpaca-alerts], [data-testid=alpaca-warning], [data-testid=alpaca-idle]")
                .Should().BeEmpty("with commands off, the hold and the lease decide nothing");
        });
    }

    [TestMethod]
    public async Task ThePanel_ShowsTheHold_TheLease_TheGrants_TheAlertsNewestFirst_AndTheWarnings()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly(), operatorLease: true);
        var requests = harness.RecordLeaseRequests();
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca]").GetAttribute("data-mode").Should().Be(nameof(RoofAlpacaCommandMode.CloseOnly)));
        await harness.TakeTheLeaseAsync(requests);

        // Taking the lease drops every grant, so the grant comes after it.
        await harness.Session!.Client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-unknown:Guide"]);
        harness.Alpaca().RaiseAlert("NotAuthorised", RoofAlpacaCommandKind.Close, "An Alpaca Close was refused as not authorised: the controller did not accept its key.", [],
            new RoofAlpacaClient(9, 1, "192.0.2.51", "basic"));
        harness.ServerClock.Advance(TimeSpan.FromSeconds(1));
        var refused = harness.RaiseRefusedClose("scope-not-clear:Main");

        await cut.WaitForAssertionAsync(() =>
        {
            var state = harness.Console.View.Status!.Alpaca!;
            state.Alerts.Should().HaveCount(2);
            state.Grants.Should().ContainSingle();
            Text(cut.Find("[data-testid=alpaca-mode]")).Should().Be(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.CloseOnly));
            cut.Find("[data-testid=alpaca]").ClassList.Should().NotContain("rc2-alpaca--compact");
            cut.FindAll("[data-testid=alpaca-warning]").Select(Text).Should().Equal(RoofAlpacaCommands.NoBlockingScopeWarning);

            var hold = cut.Find("[data-testid=alpaca-hold]");
            Text(hold.QuerySelector("span")!).Should().Be(RoofAlpacaText.DescribeHold(state.Hold));
            Text(hold.QuerySelector("[data-testid=alpaca-clear-hold]")!).Should().Be(RoofAlpacaText.ClearHoldLabel);
            Text(cut.Find("[data-testid=alpaca-lease]")).Should().Be(RoofAlpacaText.DescribeLease(state.OperatorLease))
                .And.Be("Operator lease held by olga: Alpaca commands are refused; it lapses after 10 min without input.");

            var grant = cut.Find("[data-testid=alpaca-grant]");
            grant.GetAttribute("data-kind").Should().Be(nameof(RoofAlpacaCommandKind.Close));
            Text(grant.QuerySelector("span")!).Should().Be(RoofAlpacaText.DescribeGrant(state.Grants[0]));
            Text(grant.QuerySelector("[data-testid=alpaca-revoke]")!).Should().Be(RoofAlpacaText.RevokeLabel);

            var alerts = cut.FindAll("[data-testid=alpaca-alert]");
            alerts.Select(alert => alert.GetAttribute("data-id")).Should().Equal([refused.Id, .. state.Alerts.Where(alert => alert.Id != refused.Id).Select(alert => alert.Id)], "newest first");
            alerts.Select(alert => Text(alert.QuerySelector(".rc2-alpaca-text")!)).Should().Equal(state.Alerts.Select(RoofAlpacaText.DescribeAlert));
            Text(alerts[0].QuerySelector("[data-testid=alpaca-grant-ask]")!).Should().Be(RoofAlpacaText.GrantCloseLabel);
            alerts[1].QuerySelector("[data-testid=alpaca-grant-ask]").Should().BeNull("a refusal as not authorised has nothing a grant can lift");
            alerts.Select(alert => Text(alert.QuerySelector("[data-testid=alpaca-acknowledge]")!)).Should().AllBe(RoofAlpacaText.AcknowledgeLabel);
            cut.FindAll("[data-testid=alpaca-question], [data-testid=alpaca-busy]").Should().BeEmpty();
        });
        Text(cut.FindAll("[data-testid=alpaca-alert]")[0]).Should().Contain("From ClientID 7 at 192.0.2.50 (reported by the Alpaca server).");
    }

    [TestMethod]
    public async Task AllowAlpacaAgain_ClearsTheHold_AndSaysSo()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-clear-hold]").HasAttribute("disabled").Should().BeFalse());

        cut.Click("[data-testid=alpaca-clear-hold]");

        await cut.WaitForAssertionAsync(() =>
        {
            harness.Alpaca().GetStatus().Hold.Held.Should().BeFalse("the controller cleared it");
            cut.FindAll("[data-testid=alpaca-hold]").Should().BeEmpty();
            var notice = cut.Find("[data-testid=notice]");
            notice.GetAttribute("data-level").Should().Be(nameof(WebRoofNoticeLevel.Info));
            Text(notice).Should().EndWith(RoofAlpacaText.HoldCleared);
        });
    }

    [TestMethod]
    public async Task AViewer_SeesTheAlpacaState_AndMayOnlyAcknowledge()
    {
        using var harness = await WebRoofHarness.CreateAsync(RoofControllerApiContract.ViewerRole, settings: CloseOnly());
        harness.RaiseRefusedClose("scope-not-clear:Main");
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() =>
        {
            Text(cut.Find("[data-testid=alpaca-hold] span")).Should().Be(RoofAlpacaText.DescribeHold(harness.Console.View.Status!.Alpaca!.Hold));
            cut.FindAll("[data-testid=alpaca-clear-hold], [data-testid=alpaca-grant-ask]").Should().BeEmpty("they need the Operator role");
            cut.FindAll("[data-testid=alpaca-acknowledge]").Should().ContainSingle("anyone signed in may acknowledge an alert");
            harness.Session!.OperatorLease.Should().BeNull("a viewer holds no operator lease");
        });
    }

    [TestMethod]
    public async Task AllowThisCloseOnce_AsksFirst_ThenGrantsExactlyTheAlertsReasons()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        var grants = new List<string>();
        harness.RecordLeaseRequests(grants);
        var alert = harness.RaiseRefusedClose("scope-not-clear:Main", "scope-unknown:Guide");
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-grant-ask]"));

        cut.Click("[data-testid=alpaca-grant-ask]");

        await cut.WaitForAssertionAsync(() =>
        {
            var question = cut.Find("[data-testid=alpaca-question]");
            question.GetAttribute("role").Should().Be("alertdialog");
            Text(question.QuerySelector("h3")!).Should().Be(RoofAlpacaText.GrantQuestionTitle);
            question.QuerySelectorAll("p").Select(Text).Should().Equal(RoofAlpacaText.DescribeGrantQuestion(alert, 5));
            Text(question.QuerySelector("[data-testid=alpaca-question-confirm]")!).Should().Be(RoofAlpacaText.GrantConfirmLabel);
        });
        harness.Alpaca().Grants().Should().BeEmpty("nothing is granted before the person confirms");
        grants.Should().BeEmpty();

        cut.Click("[data-testid=alpaca-question-confirm]");

        await cut.WaitForAssertionAsync(() =>
        {
            var grant = harness.Alpaca().Grants().Should().ContainSingle().Subject;
            grant.Kind.Should().Be(RoofAlpacaCommandKind.Close);
            grant.ReasonKeys.Should().Equal(alert.ReasonKeys);
            grant.GrantedBy.Should().Be("olga (signed in)");
            cut.FindAll("[data-testid=alpaca-question]").Should().BeEmpty();
            Text(cut.Find("[data-testid=alpaca-grant] span")).Should().StartWith("One Close allowed by olga (signed in) (session), ");
            Text(cut.Find("[data-testid=notice]")).Should().Contain("One Close allowed by olga (signed in) (session)");
        });
        grants.Should().ContainSingle().Which.Should().Contain("\"reasonKeys\":[\"scope-not-clear:Main\",\"scope-unknown:Guide\"]");
    }

    [TestMethod]
    public async Task TheGrantQuestion_Cancelled_SendsNothing()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        var grants = new List<string>();
        harness.RecordLeaseRequests(grants);
        harness.RaiseRefusedClose("scope-unknown:Main");
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-grant-ask]"));
        cut.Click("[data-testid=alpaca-grant-ask]");
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-question]"));

        cut.Click("[data-testid=alpaca-question-cancel]");

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("[data-testid=alpaca-question]").Should().BeEmpty();
            Text(cut.Find("[data-testid=notice]")).Should().EndWith(RoofAlpacaText.GrantCancelled);
        });
        await harness.Console.GrantAlpacaCloseAsync();
        harness.Alpaca().Grants().Should().BeEmpty();
        grants.Should().BeEmpty("a cancelled question sends nothing, and a later confirm finds no question");
    }

    [TestMethod]
    public async Task AStop_WithdrawsTheGrantQuestion()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        var grants = new List<string>();
        harness.RecordLeaseRequests(grants);
        var alert = harness.RaiseRefusedClose("scope-not-clear:Main");
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Alpaca?.Alerts.Count == 1, "the alert");
        harness.Console.AskAlpacaGrant(alert.Id);
        harness.Console.View.Alpaca!.Question.Should().NotBeNull();

        await harness.Session!.StopAsync();
        await harness.Console.GrantAlpacaCloseAsync();

        harness.Console.View.Alpaca!.Question.Should().BeNull();
        harness.Console.View.Notices.Select(notice => notice.Text).Should().Contain(RoofAlpacaText.GrantWithdrawn);
        harness.Alpaca().Grants().Should().BeEmpty();
        grants.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Acknowledge_AcknowledgesTheAlert_AtTheController()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        var alert = harness.RaiseRefusedClose("scope-not-clear:Main");
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-acknowledge]"));

        cut.Click("[data-testid=alpaca-acknowledge]");

        await cut.WaitForAssertionAsync(() =>
        {
            var acknowledged = harness.Alpaca().Alerts().Single(item => item.Id == alert.Id);
            acknowledged.Acknowledged.Should().BeTrue();
            acknowledged.AcknowledgedBy.Should().Be("olga (signed in)");
            var shown = cut.Find("[data-testid=alpaca-alert]");
            shown.ClassList.Should().Contain("rc2-alpaca-item--acknowledged");
            Text(shown.QuerySelector("[data-testid=alpaca-alert-acknowledged]")!).Should().Be("Acknowledged by olga (signed in).");
            shown.QuerySelectorAll("button").Should().BeEmpty("an acknowledged alert offers neither a grant nor Acknowledge");
        });
    }

    [TestMethod]
    public async Task Revoke_RevokesTheGrant_AtTheController()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: CloseOnly());
        await harness.Session!.Client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]);
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=alpaca-revoke]"));

        cut.Click("[data-testid=alpaca-revoke]");

        await cut.WaitForAssertionAsync(() =>
        {
            harness.Alpaca().Grants().Should().BeEmpty();
            cut.FindAll("[data-testid=alpaca-grant]").Should().BeEmpty();
            Text(cut.Find("[data-testid=notice]")).Should().EndWith(RoofAlpacaText.DescribeRevoked(RoofAlpacaCommandKind.Close));
        });
    }

    [TestMethod]
    public async Task AGrantTheControllerRefuses_IsSaidAsANotice()
    {
        // With RoofSafety:OverrideRole Admin only an admin may grant: the page does not offer it to an operator (the status
        // names the role in Warn mode), and the controller refuses one sent anyway.
        var settings = CloseOnly();
        settings["RoofSafety:InterlockMode"] = "Warn";
        settings["RoofSafety:OverrideRole"] = "Admin";
        using var harness = await WebRoofHarness.CreateAsync(settings: settings);
        var alert = harness.RaiseRefusedClose("scope-not-clear:Main");
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Alpaca?.Alerts.Count == 1, "the alert");
        harness.Console.View.Alpaca!.Alerts[0].CanGrant.Should().BeFalse();

        harness.Console.AskAlpacaGrant(alert.Id);
        await harness.Console.GrantAlpacaCloseAsync();

        var notice = harness.Console.View.Notices[0];
        notice.Level.Should().Be(WebRoofNoticeLevel.Danger);
        notice.Text.Should().Contain("Only an admin may grant an override on this controller");
        harness.Alpaca().Grants().Should().BeEmpty();
    }
}
