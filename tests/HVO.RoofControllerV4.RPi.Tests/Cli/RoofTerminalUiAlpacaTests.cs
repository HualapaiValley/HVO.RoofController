using System.Collections.Concurrent;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TestSecrets = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The terminal interface's Alpaca section (#220) against the controller in process with Alpaca Close on: the state in
/// place of the roof's, Allow Alpaca again, Allow this Close once (asked again with what it lifts, sending exactly the
/// alert's reasons, or nothing on Cancel, Esc or Stop), Acknowledge and Revoke, each refused for an API key; and the
/// operator lease held for a person who may operate, renewed on their input only, warned of in the banner, and released
/// when the interface closes.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofTerminalUiAlpacaTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Alpaca_ShowsTheAlpacaState_InPlaceOfTheRoofsStateAndReadings_AndReadingsBringsThemBack()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.WaitIdle("the operator lease", () => tui.Ui.Status?.Alpaca?.OperatorLease.Held == true);
        page.Describe().Should().Contain("Alpaca:  Alpaca may close the roof (never open it)")
            .And.Contain("Alpaca alerts:  1 not acknowledged; Alpaca shows them.");

        Click(tui, RoofAlpacaText.Title);

        page.ShowsAlpaca.Should().BeTrue();
        page.AlpacaButton.Text.Should().Be("Readings");
        page.Alpaca.List.HasFocus.Should().BeTrue("the lines are read and picked with the keyboard");
        page.Alpaca.Items.Select(item => item.Text).Should().Equal(
            "Alpaca may close the roof (never open it)",
            "Alpaca commands are held after a Stop (the controller's start): a person must allow them again.",
            "Operator lease held by ada: Alpaca commands are refused; it lapses after 10 min without input.",
            $"Warning: {HVO.RoofControllerV4.RPi.Alpaca.RoofAlpacaCommands.NoBlockingScopeWarning}",
            RoofCliAlpaca.NoGrants,
            $"Alert {alert.Id} (NEW): {RoofAlpacaText.DescribeAlert(alert)}");
        tui.Screen.Should().Contain(RoofUiAlpacaSection.FrameTitle).And.Contain("Stop roof (F9)").And.NotContain("ROOF STATE");
        page.Describe().Should().Contain("Operator lease held by ada");

        Click(tui, "Readings");
        page.ShowsAlpaca.Should().BeFalse();
        tui.Screen.Should().Contain("ROOF STATE").And.NotContain(RoofUiAlpacaSection.FrameTitle);
    }

    [TestMethod]
    public void History_And_Alpaca_TakeTurns()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        Click(tui, "History");
        Click(tui, RoofAlpacaText.Title);
        page.ShowsAlpaca.Should().BeTrue();
        page.ShowsHistory.Should().BeFalse();

        Click(tui, "History");
        page.ShowsAlpaca.Should().BeFalse();
        page.ShowsHistory.Should().BeTrue();
        tui.WaitIdle("the history");
    }

    [TestMethod]
    public void AllowAlpacaAgain_OnTheHold_ClearsIt()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Hold);

        var panel = tui.Ui.Panel!;
        panel.Prompt.Title.Should().Be(RoofUiAlpacaSection.HoldTitle);
        panel.Prompt.Message.Should().Be(
            "Alpaca commands are held after a Stop (the controller's start): a person must allow them again.\n" + RoofUiAlpacaSection.ClearHoldExplains);
        panel.Buttons.Select(button => button.Text).Should().Equal(RoofAlpacaText.ClearHoldLabel, "Cancel");
        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeTrue("nothing is sent before the person chooses");

        panel.Press(RoofAlpacaText.ClearHoldLabel);
        tui.WaitIdle("the hold to clear", () => tui.Ui.Message == RoofCliAlpaca.HoldCleared);

        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeFalse();
        tui.Ui.Panel.Should().BeNull();
        tui.WaitIdle("the new state", () => page.Alpaca.Items.Any(item => item.Text == RoofCliAlpaca.HoldClear));
    }

    [TestMethod]
    public void AllowThisCloseOnce_AsksWithWhatItLifts_AndSendsExactlyTheAlertsReasonKeys()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main", "scope-not-clear:Guide");
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        var actions = tui.Ui.Panel!;
        actions.Prompt.Title.Should().Be(RoofUiAlpacaSection.AlertTitle);
        actions.Buttons.Select(button => button.Text).Should().Equal(RoofAlpacaText.AcknowledgeLabel, RoofAlpacaText.GrantCloseLabel, "Cancel");

        actions.Press(RoofAlpacaText.GrantCloseLabel);

        var question = tui.Ui.Panel!;
        question.Should().NotBeSameAs(actions);
        question.Prompt.Title.Should().Be($"{RoofAlpacaText.GrantCloseLabel}?");
        question.Prompt.Message.Should().Be(string.Join('\n', [.. RoofAlpacaText.DescribeGrantQuestion(alert, 5), RoofCliAlpaca.StepAwayQuestion]),
            "this interface holds the operator lease, so the person is told that granting steps them away from it");
        question.Buttons.Select(button => button.Text).Should().Equal(RoofAlpacaText.GrantCloseLabel, "Cancel");
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty("nothing is sent before the person confirms");

        question.Press(RoofAlpacaText.GrantCloseLabel);
        tui.WaitIdle("the grant", () => tui.Ui.Message.StartsWith("Granted.", StringComparison.Ordinal));

        var grant = AlpacaCliSupport.Commands(host).Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Close);
        grant.ReasonKeys.Should().Equal(alert.ReasonKeys);
        grant.GrantedBy.Should().Contain("ada");
        tui.Ui.Panel.Should().BeNull();
        tui.WaitIdle("the grant shown", () => page.Alpaca.Items.Any(item => item.Kind == RoofUiAlpacaItemKind.Grant));
    }

    [TestMethod]
    public void TheGrantQuestion_CancelledOrEscaped_SendsNothing()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.GrantCloseLabel);
        var question = tui.Ui.Panel!;
        question.Press("Cancel");
        tui.Ui.Panel.Should().BeNull();

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.GrantCloseLabel);
        tui.Press(Key.Esc);
        tui.Ui.Panel.Should().BeNull();

        Settle(tui);
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public void F9_WithdrawsTheGrantQuestion_AndItsActionThenSendsNothing()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);
        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.GrantCloseLabel);
        var question = tui.Ui.Panel!.Prompt;

        tui.Press(Key.F9);
        tui.WaitIdle("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        tui.Ui.Panel.Should().BeNull("Stop withdraws the question");
        question.Actions.Single().Run([]).Should().BeNull();
        Settle(tui);
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty("an action from before the Stop sends nothing");
    }

    [TestMethod]
    public void Acknowledge_AcknowledgesTheAlert_AndThenOnlyReadingIsOffered()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada", RoofControllerApiContract.ViewerRole);
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.AcknowledgeLabel);
        tui.WaitIdle("the acknowledgement", () => tui.Ui.Message == $"Acknowledged alert {alert.Id}.");

        var acknowledged = AlpacaCliSupport.Commands(host).Alerts().Single();
        acknowledged.Acknowledged.Should().BeTrue();
        acknowledged.AcknowledgedBy.Should().Contain("ada");
        tui.WaitIdle("the acknowledged alert", () => page.Alpaca.Items.Any(item => item.Alert?.Acknowledged == true));
        page.Alpaca.Items.Single(item => item.Kind == RoofUiAlpacaItemKind.Alert).Text.Should().StartWith($"Alert {alert.Id} (acknowledged by ");

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Buttons.Select(button => button.Text).Should().Equal("Done");
    }

    [TestMethod]
    public void Revoke_OnAGrant_RevokesIt()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        // Granted once this interface holds the operator lease: taking the lease drops every grant.
        tui.WaitIdle("the operator lease", () => AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Held);
        OffTheLoop(async () =>
        {
            using var other = ClientTestSupport.CreateClient(host, await SessionAsync(host, "grace"));
            await other.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]);
        });
        tui.WaitIdle("the grant", () => page.Alpaca.Items.Any(item => item.Kind == RoofUiAlpacaItemKind.Grant));

        Enter(tui, page, RoofUiAlpacaItemKind.Grant);
        tui.Ui.Panel!.Prompt.Title.Should().Be(RoofUiAlpacaSection.GrantTitle);
        tui.Ui.Panel.Press(RoofAlpacaText.RevokeLabel);
        tui.WaitIdle("the revocation", () => tui.Ui.Message == "Revoked the Close grant.");

        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public void WithAnApiKey_EveryActionIsRefusedInThePrompt_AndNoOperatorLeaseIsHeld()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);
        Settle(tui);

        tui.Ui.OperatorLease.Should().BeNull("an API key is not a person");
        AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Held.Should().BeFalse();

        Enter(tui, page, RoofUiAlpacaItemKind.Hold);
        var panel = tui.Ui.Panel!;
        panel.Press(RoofAlpacaText.ClearHoldLabel);
        tui.Ui.Panel.Should().BeSameAs(panel, "the prompt stays open to say why");
        panel.Error.Should().Be(RoofCliAlpaca.PersonOnly);

        tui.Press(Key.Esc);
        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.GrantCloseLabel);
        tui.Ui.Panel!.Error.Should().Be(RoofCliAlpaca.PersonOnly);
        Settle(tui);

        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeTrue();
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public void EnterOnALineWithNothingToDo_SaysSo()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Info);

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().Be(RoofUiAlpacaSection.NoAction);
    }

    // ---- The operator lease -------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheOperatorLease_IsHeldForAnOperatorsSession_AndReleasedWhenTheInterfaceCloses()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        var tui = Started(rig);
        try
        {
            tui.WaitIdle("the operator lease", () => AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Held);
            tui.Ui.OperatorLease.Should().NotBeNull();
            AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Holder.Should().Be("ada");
        }
        finally
        {
            tui.Dispose();
        }

        AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Held.Should().BeFalse("closing the interface releases it");
    }

    [TestMethod]
    public void TheOperatorLease_IsNotHeldForAViewersSession()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada", RoofControllerApiContract.ViewerRole);
        using var tui = Started(rig);
        Settle(tui);

        tui.Ui.OperatorLease.Should().BeNull();
        AlpacaCliSupport.Commands(host).GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    public void TheOperatorLease_IsRenewedWithInput_OnlyAfterThePersonsKeysOrClicks_NeverTheTimerOrTheStatus()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { WrapHandler = inner => new RequestLog(inner, requests) };
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        tui.WaitIdle("the lease taken", () => Beats(requests).Count == 1);
        Beats(requests).Single().Should().EndWith("?input=true", "the first heartbeat takes the lease");

        // Ten seconds of the interface's clock, with statuses arriving: a heartbeat, without input.
        tui.Tick(10);
        tui.WaitIdle("the second heartbeat", () => Beats(requests).Count == 2);
        Beats(requests)[1].Should().NotContain("input=true");

        tui.Press(Key.F1);
        tui.Tick(10);
        tui.WaitIdle("the third heartbeat", () => Beats(requests).Count == 3);
        Beats(requests)[2].Should().EndWith("?input=true", "a key is the person's input");

        tui.Tick(10);
        tui.WaitIdle("the fourth heartbeat", () => Beats(requests).Count == 4);
        Beats(requests)[3].Should().NotContain("input=true");

        tui.App.InjectMouse(new Mouse { Flags = MouseFlags.LeftButtonPressed, ScreenPosition = new System.Drawing.Point(2, 2) });
        tui.Pump();
        tui.Tick(10);
        tui.WaitIdle("the fifth heartbeat", () => Beats(requests).Count == 5);
        Beats(requests)[4].Should().EndWith("?input=true", "a press is the person's input");

        // A release, or the click made of it, arrives after the press it ends, and may come after a grant it confirmed.
        tui.App.InjectMouse(new Mouse { Flags = MouseFlags.LeftButtonReleased, ScreenPosition = new System.Drawing.Point(2, 2) });
        tui.Pump();
        tui.Tick(10);
        tui.WaitIdle("the sixth heartbeat", () => Beats(requests).Count == 6);
        Beats(requests)[5].Should().NotContain("input=true");
    }

    [TestMethod]
    public void TheIdleWarning_AndAlpacaMayNowMove_AreInTheBanner_AndAKeyThenRenewsTheLeaseAtOnce()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { WrapHandler = inner => new RequestLog(inner, requests) };
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        tui.WaitIdle("the lease taken", () => Beats(requests).Count == 1 && tui.Ui.Status?.Alpaca?.OperatorLease.Held == true);

        // The controller's word that the lease is about to lapse; later statuses are older and are not shown over it.
        var current = tui.Ui.Status!;
        tui.Ui.Apply(current with
        {
            StatusVersion = current.StatusVersion + 100,
            Alpaca = current.Alpaca! with { OperatorLease = new RoofOperatorLeaseState { Held = true, Holder = "ada", IdleSecondsRemaining = 42 } }
        });
        tui.Ui.Banner.Should().Contain("Your operator lease lapses in 42 s without input; then Alpaca may move the roof. Touch anything to keep it.");

        // Then that it lapsed: the banner says Alpaca may now move the roof. The heartbeats go on without input (the
        // controller takes the lease back only with input), and the person's key sends one with it at once.
        tui.Ui.Apply(tui.Ui.Status! with
        {
            StatusVersion = current.StatusVersion + 101,
            Alpaca = current.Alpaca! with { OperatorLease = new RoofOperatorLeaseState { Held = false, IdleLapsedHolder = "ada" } }
        });
        tui.Ui.Banner.Should().Contain(RoofAlpacaText.MayNowMove);
        tui.Tick(10);
        tui.WaitIdle("a heartbeat", () => Beats(requests).Count == 2);
        Beats(requests)[1].Should().NotContain("input=true");

        tui.Press(Key.F1);
        tui.WaitIdle("the lease taken again", () => Beats(requests).Count == 3);
        Beats(requests)[2].Should().EndWith("?input=true", "a key while the lease has lapsed takes it back at once");
    }

    [TestMethod]
    public void AllowThisCloseOnce_StepsThePersonAwayFromTheLease_HeartbeatsGoOnWithoutTakingItBack_AndTheirNextKeyDoes()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { WrapHandler = inner => new RequestLog(inner, requests) };
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var commands = AlpacaCliSupport.Commands(host);
        tui.WaitIdle("the operator lease", () => commands.GetStatus().OperatorLease.Held);
        var page = ShowAlpaca(tui);

        Enter(tui, page, RoofUiAlpacaItemKind.Alert);
        tui.Ui.Panel!.Press(RoofAlpacaText.GrantCloseLabel);
        var question = tui.Ui.Panel!;
        question.Prompt.Message.Should().EndWith("\n" + RoofCliAlpaca.StepAwayQuestion);

        // Confirmed with Enter, as the person would: that key is input, and must not count after the grant.
        question.Buttons[0].HasFocus.Should().BeTrue();
        tui.Press(Key.Enter);
        tui.WaitIdle("the grant", () => tui.Ui.Message.StartsWith("Granted.", StringComparison.Ordinal));

        commands.Grants().Should().ContainSingle();
        commands.GetStatus().OperatorLease.Held.Should().BeFalse("the person stepped away from the lease with the grant");
        tui.Ui.SteppedAway.Should().NotBeNull();
        tui.WaitIdle("the banner", () => tui.Ui.Banner.Contains(
            "You stepped away from the operator lease: Alpaca may close the roof once in the next 5 min. Press any key to take control back; that cancels the grant.",
            StringComparison.Ordinal));
        page.Alpaca.Items.Should().Contain(item => item.Text.StartsWith("You stepped away", StringComparison.Ordinal));

        // The heartbeats go on, without input, and the lease stays released: the grant can be used.
        var beats = Beats(requests).Count;
        tui.Tick(10);
        tui.WaitIdle("a heartbeat", () => Beats(requests).Count == beats + 1);
        tui.Tick(10);
        tui.WaitIdle("another heartbeat", () => Beats(requests).Count == beats + 2);
        var afterGrant = requests.SkipWhile(request => !request.StartsWith("POST /api/v4.0/alpaca/grants", StringComparison.Ordinal)).Skip(1)
            .Where(request => request.StartsWith("POST ", StringComparison.Ordinal) && request.Contains("/alpaca/lease", StringComparison.Ordinal)).ToList();
        afterGrant.Should().HaveCount(2).And.OnlyContain(request => !request.Contains("input=true"), "no input crosses or follows the grant");
        commands.GetStatus().OperatorLease.Held.Should().BeFalse("heartbeats alone never take it back");
        commands.Grants().Should().ContainSingle();
        tui.Ui.Banner.Should().Contain("You stepped away from the operator lease");

        // The person's next key takes control back at once, and that cancels the grant.
        tui.Press(Key.F1);
        tui.WaitIdle("the lease taken back", () => commands.GetStatus().OperatorLease.Held);
        Beats(requests)[^1].Should().EndWith("?input=true");
        commands.GetStatus().OperatorLease.Holder.Should().Be("ada");
        commands.Grants().Should().BeEmpty("taking the lease back drops the grant");
        tui.Ui.SteppedAway.Should().BeNull();
        tui.WaitIdle("the banner cleared", () => !tui.Ui.Banner.Contains("stepped away", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SteppedAway_WhileAnotherPersonHoldsTheLease_SaysAlpacaStaysRefused()
    {
        RoofCliAlpaca.DescribeSteppedAway(null, new RoofOperatorLeaseState { Held = true, Holder = "grace" })
            .Should().Be("You stepped away from the operator lease, but grace holds it: Alpaca commands stay refused. Press any key to take control back.");
        RoofCliAlpaca.DescribeSteppedAway(null, new RoofOperatorLeaseState())
            .Should().Be("You stepped away from the operator lease: Alpaca may move the roof. Press any key to take control back.");
    }

    [TestMethod]
    public void TheIdleWarning_IsOnlyForTheSignedInPerson()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada");
        using var tui = Started(rig);
        var current = tui.Ui.Status!;

        tui.Ui.Apply(current with
        {
            StatusVersion = current.StatusVersion + 100,
            Alpaca = current.Alpaca! with { OperatorLease = new RoofOperatorLeaseState { Held = true, Holder = "grace", IdleSecondsRemaining = 42 } }
        });

        tui.Ui.Banner.Should().NotContain("operator lease");
    }

    [TestMethod]
    public void TheIdleWarning_IsOnlyForALeaseThisInterfaceHolds()
    {
        // A viewer's interface holds no lease: input here would keep nothing, so it does not ask for any.
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        SignedIn(host, rig, "ada", RoofControllerApiContract.ViewerRole);
        using var tui = Started(rig);
        var current = tui.Ui.Status!;

        tui.Ui.Apply(current with
        {
            StatusVersion = current.StatusVersion + 100,
            Alpaca = current.Alpaca! with { OperatorLease = new RoofOperatorLeaseState { Held = true, Holder = "ada", IdleSecondsRemaining = 42 } }
        });

        tui.Ui.OperatorLease.Should().BeNull();
        tui.Ui.Banner.Should().NotContain("operator lease");
    }

    // ---- Support ------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> Beats(ConcurrentQueue<string> requests)
        => [.. requests.Where(request => request.StartsWith("POST ", StringComparison.Ordinal) && request.Contains("/alpaca/lease", StringComparison.Ordinal))];

    /// <summary>Saves a password session for a new <paramref name="name"/>, as <c>hvo-roof login</c> would.</summary>
    internal static void SignedIn(RoofApiTestHost host, CliRig rig, string name, string role = RoofControllerApiContract.OperatorRole)
        => OffTheLoop(() => AlpacaCliSupport.SignInAsync(host, rig, name, role));

    private static async Task<RoofSessionCredential> SessionAsync(RoofApiTestHost host, string name)
    {
        await RoofClientApiTests.AddUserAsync(host, name, RoofControllerApiContract.OperatorRole);
        using var anonymous = ClientTestSupport.CreateClient(host);
        return await anonymous.Auth.SignInAsync(name, TestSecrets.Password);
    }

    private static void OffTheLoop(Func<Task> call) => Task.Run(call).GetAwaiter().GetResult();

    internal static TuiDriver Started(CliRig rig, int width = 120, int height = 36)
    {
        var tui = new TuiDriver(rig, width, height);
        try
        {
            tui.WaitIdle("the first status", () => tui.Ui.Status?.Alpaca is not null && !tui.Ui.IsStale && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }

    /// <summary>The Roof page with its Alpaca section shown.</summary>
    internal static RoofUiRoofPage ShowAlpaca(TuiDriver tui)
    {
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        Click(tui, RoofAlpacaText.Title);
        page.ShowsAlpaca.Should().BeTrue();
        return page;
    }

    /// <summary>Enter on the first line of <paramref name="kind"/>, as the person would.</summary>
    internal static void Enter(TuiDriver tui, RoofUiRoofPage page, RoofUiAlpacaItemKind kind)
    {
        page.Alpaca.Select(item => item.Kind == kind);
        page.Alpaca.List.SetFocus();
        tui.Press(Key.Enter);
    }

    internal static void Click(TuiDriver tui, string label)
    {
        View root = tui.Ui.Panel is { } panel ? panel : tui.Ui.CurrentPage;
        var button = Find(root, label) ?? throw new AssertFailedException($"No visible button '{label}'. Screen:\n{tui.Screen}");
        button.InvokeCommand(Command.Accept);
        tui.Pump();
    }

    private static Button? Find(View view, string label)
    {
        foreach (var child in view.SubViews)
        {
            if (child is Button { Visible: true } button && button.Text == label)
            {
                return button;
            }

            if (child.Visible && Find(child, label) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Lets any request a withdrawn action might have sent arrive.</summary>
    private static void Settle(TuiDriver tui)
    {
        tui.WaitIdle("anything on its way");
        Thread.Sleep(100);
        tui.WaitIdle("anything on its way");
    }
}
