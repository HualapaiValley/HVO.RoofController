using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// Tickets (#220, §10, §16 Y2/Y3, §19 L1, R8-5): single use, for one command kind, one key, one controller instance and
/// generation, valid 5 s on the controller's monotonic clock, remembered 10 minutes after; a duplicate gets the first
/// request's answer and never a second admission; a generation that moved is "superseded"; a changed authorization epoch
/// refuses.
/// </summary>
[TestClass]
public sealed class RoofAlpacaTicketTests
{
    private const byte CloseRelay = 2;

    [TestMethod]
    public async Task ATicket_CarriesTheInstance_TheGeneration_AndTheAnswerDeadline()
    {
        // Right after the controller's start, whose all-off the whole delay runs from (on a clock that does not move).
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: TimeSpan.FromSeconds(1), budgetMs: 700, time: new ManualTimeProvider())).Ready();

        var outcome = rig.Commands.IssueTicket(RoofAlpacaCommandKind.Close, rig.Commander, AlpacaCommandRig.Client);

        var ticket = outcome.Ticket!;
        ticket.InstanceId.Should().Be(rig.Controller.InstanceId);
        ticket.Generation.Should().Be(rig.Controller.CommandGeneration);
        ticket.Kind.Should().Be(RoofAlpacaCommandKind.Close);
        ticket.AnswerDeadlineMilliseconds.Should().Be(700 + 1000 + 400, "budget + what is left of the direction-change delay + 400 ms");
        ticket.TicketId.Should().HaveLength(22);
    }

    [TestMethod]
    public async Task TheAnswerDeadline_CountsOnlyWhatIsLeftOfTheDelay()
    {
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: TimeSpan.FromSeconds(2), budgetMs: 600, time: time)).Ready();
        time.Advance(TimeSpan.FromSeconds(3));
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1500));
        rig.Ticket().AnswerDeadlineMilliseconds.Should().Be(600 + 500 + 400, "half a second of the delay is left");

        time.Advance(TimeSpan.FromSeconds(1));
        rig.Ticket().AnswerDeadlineMilliseconds.Should().Be(600 + 400, "none of the delay is left");
    }

    [TestMethod]
    public async Task ACloseDeliveredPastItsDeadline_WithNoDelayLeft_IsRefused_AndNothingMoves()
    {
        // PR #230 review F5: with the delay long over, the deadline is the budget and the margin alone.
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(directionChangeDelay: TimeSpan.FromSeconds(2), budgetMs: 600, time: time)).Ready();
        time.Advance(TimeSpan.FromSeconds(3));
        (await rig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Opening))).IsSuccessful.Should().BeTrue();
        rig.Controller.Stop().IsSuccessful.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(3));
        rig.Hat.ClearRelayWriteLog();

        var ticket = rig.Ticket();
        time.Advance(TimeSpan.FromMilliseconds(1300));
        var outcome = await rig.CloseAsync(ticket);

        outcome.Rule().Should().Be("DeadlineExceeded");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AnOpenTicket_IsRefused_InThisVersion()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();

        var outcome = rig.Commands.IssueTicket(RoofAlpacaCommandKind.Open, rig.Commander, AlpacaCommandRig.Client);

        outcome.Ticket.Should().BeNull();
        outcome.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        rig.Scope.Rereads.Should().Be(0, "no re-read starts for a refused ticket");
    }

    [TestMethod]
    [DataRow(null, "instance", 1L, "TicketInvalid")]
    [DataRow("ticket", null, 1L, "TicketInvalid")]
    [DataRow("ticket", "instance", null, "TicketInvalid")]
    [DataRow("unknown-ticket", "instance", 1L, "TicketUnknown")]
    public async Task AMissingOrMalformedField_OrAnUnknownTicket_IsRefused_AndNothingMoves(string? ticket, string? instance, long? generation, string rule)
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();

        var outcome = await rig.Commands.CloseAsync(new RoofAlpacaCommandRequest(ticket, instance, generation), rig.Commander, AlpacaCommandRig.Client);

        outcome.Rule().Should().Be(rule);
        outcome.Code().Should().Be(RoofControllerErrorCode.AlpacaCommandRefused);
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task ATicketWhoseCopiesDiffer_OrForAnotherKey_IsRefused()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ticket = rig.Ticket();

        (await rig.Commands.CloseAsync(new RoofAlpacaCommandRequest(ticket.TicketId, "another-instance", ticket.Generation), rig.Commander, AlpacaCommandRig.Client))
            .Rule().Should().Be("TicketMismatch");
        (await rig.Commands.CloseAsync(new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation + 1), rig.Commander, AlpacaCommandRig.Client))
            .Rule().Should().Be("TicketMismatch");
        (await rig.CloseAsync(ticket, rig.Server)).Rule().Should().Be("TicketMismatch", "a ticket is usable only by the key that asked for it");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);

        (await rig.CloseAsync(ticket)).Result.IsSuccessful.Should().BeTrue("the refusals did not use the ticket");
    }

    [TestMethod]
    public async Task ADuplicateOfAnAcceptedCommand_GetsItsAnswer_AndNeverASecondStart()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ticket = rig.Ticket();
        (await rig.CloseAsync(ticket)).Result.IsSuccessful.Should().BeTrue();
        rig.Controller.Stop();
        var stopped = rig.RelayOnWrites(CloseRelay);

        var duplicate = await rig.CloseAsync(ticket);

        duplicate.Result.IsSuccessful.Should().BeTrue("the stored answer of the first request");
        rig.RelayOnWrites(CloseRelay).Should().Be(stopped, "no second admission");
        rig.Controller.IsMoving.Should().BeFalse();
    }

    [TestMethod]
    public async Task ADuplicateOfARefusedCommand_GetsTheRefusal_EvenAfterConditionsImproveAndAGrant()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        var ticket = rig.Ticket();
        (await rig.CloseAsync(ticket)).Rule().Should().Be("NotSafe");

        rig.Scope.Parked = true;
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"]));
        var replayed = await rig.CloseAsync(ticket);

        replayed.Rule().Should().Be("NotSafe", "a refused request is never replayed by a later grant or by conditions turning safe");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Grants().Should().ContainSingle("the replay used nothing");
    }

    [TestMethod]
    public async Task ADuplicateArrivingWhileTheFirstIsProcessed_WaitsForItsAnswer()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Scope.HoldRereads = true;
        var ticket = rig.Ticket();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Held == 1, "the ticket's re-read");

        var first = rig.CloseAsync(ticket);
        var duplicate = rig.CloseAsync(ticket);
        await Task.Delay(100);
        first.IsCompleted.Should().BeFalse();
        duplicate.IsCompleted.Should().BeFalse("it waits for the first request's answer");
        rig.Scope.ReleaseOne(true);

        (await first).Result.IsSuccessful.Should().BeTrue();
        (await duplicate).Result.IsSuccessful.Should().BeTrue();
        rig.RelayOnWrites(CloseRelay).Should().Be(1, "one admission");
    }

    [TestMethod]
    public async Task AnUnusedTicket_ExpiresAfterFiveSeconds()
    {
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(time: time)).Ready();
        var ticket = rig.Ticket();

        time.Advance(RoofAlpacaContract.TicketLifetime + TimeSpan.FromMilliseconds(1));
        var late = await rig.CloseAsync(ticket);

        late.Rule().Should().Be("TicketExpired");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AUsedTicket_KeepsItsAnswer_PastItsDeadline_UntilForgotten_ThenIsUnknown()
    {
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(time: time)).Ready();
        var ticket = rig.Ticket();
        (await rig.CloseAsync(ticket)).Result.IsSuccessful.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(6));
        (await rig.CloseAsync(ticket)).Result.IsSuccessful.Should().BeTrue("a used ticket's stored answer, after the 5 s deadline too");

        time.Advance(RoofAlpacaContract.TicketMemory + TimeSpan.FromSeconds(1));
        rig.Commands.Tick();
        (await rig.CloseAsync(ticket)).Rule().Should().Be("TicketUnknown", "forgotten 10 minutes past its deadline");
        rig.RelayOnWrites(CloseRelay).Should().Be(1);
    }

    [TestMethod]
    public async Task AStopAfterTheTicket_SupersedesTheCommand_AndNothingMoves()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ticket = rig.Ticket();

        rig.Controller.Stop(); // the Alpaca server's own AbortSlew: no hold
        var outcome = await rig.CloseAsync(ticket);

        outcome.Code().Should().Be(RoofControllerErrorCode.Superseded);
        outcome.Rule().Should().Be("Superseded");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AGrantIsUsedUp_EvenByASupersededCommand()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]));
        var ticket = rig.Ticket();
        rig.Controller.Stop();

        (await rig.CloseAsync(ticket)).Code().Should().Be(RoofControllerErrorCode.Superseded);
        rig.Commands.Grants().Should().BeEmpty("the use-up point comes before the generation check");
    }

    [TestMethod]
    public async Task TwoConcurrentCloses_FromTwoClients_GiveOneStart_AndBothSucceed()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();

        var outcomes = await Task.WhenAll(rig.CloseAsync(), rig.CloseAsync(client: new RoofAlpacaClient(8, 1, "192.168.1.51", "header")));

        outcomes.Should().OnlyContain(outcome => outcome.Result.IsSuccessful);
        rig.RelayOnWrites(CloseRelay).Should().Be(1);
    }

    [TestMethod]
    public async Task ARotatedKey_OrCommandsChanged_AfterTheTicket_RefusesTheCommand()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ticket = rig.Ticket();
        rig.RotateCommanderKey();
        (await rig.CloseAsync(ticket, rig.Principal(AlpacaCommandRig.RotatedCommanderKey))).Rule().Should().BeOneOf("TicketMismatch", "AuthorizationChanged");

        await using var other = (await AlpacaCommandRig.StartAsync()).Ready();
        var second = other.Ticket();
        other.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.Off });
        other.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.CloseOnly });
        (await other.CloseAsync(second)).Rule().Should().Be("AuthorizationChanged");
        other.RelayOnWrites(CloseRelay).Should().Be(0);
    }

    [TestMethod]
    public async Task AKeyRotatedDuringTheReRead_WhileSafe_RefusesTheCommand()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        rig.Scope.HoldRereads = true;
        var ticket = rig.Ticket();
        await AlpacaCommandRig.WaitUntilAsync(() => rig.Scope.Held == 1, "the re-read");
        var close = rig.CloseAsync(ticket);

        rig.RotateCommanderKey(); // the epoch moves (the account's key changed); the same key's ticket stays its own
        rig.Scope.ReleaseOne(true);

        (await close).Rule().Should().Be("AuthorizationChanged");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
    }
}
