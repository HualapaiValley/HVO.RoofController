using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// Override grants (#220, §5): UI-only, one per kind, reasons-bound, used up by the next command of their kind that gets
/// past the controller's own checks whatever follows, timed on the monotonic clock, dropped on every change the plan
/// names, and never a bypass of the hard protections, a person's lease or the Stop hold.
/// </summary>
[TestClass]
public sealed class RoofAlpacaGrantTests
{
    private const byte CloseRelay = 2;

    private static RoofOverrideGrantRequest Close(params string[] keys) => new(RoofAlpacaCommandKind.Close, keys);

    [TestMethod]
    public async Task AGrant_LetsOneRefusedClose_Through_ThenIsGone()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        var refused = await rig.CloseAsync();
        var alert = rig.Commands.Alerts().Single();

        var grant = rig.Commands.Grant(AlpacaCommandRig.Person(), Close([.. alert.ReasonKeys])).Grant!;
        var outcome = await rig.CloseAsync();

        refused.Rule().Should().Be("NotSafe");
        outcome.Result.IsSuccessful.Should().BeTrue(outcome.Result.Error?.Message);
        outcome.GrantId.Should().Be(grant.Id, "the history links the command to its grant");
        outcome.ReasonKeys.Should().Equal("scope-not-clear:Main");
        rig.Commands.Grants().Should().BeEmpty("one command each");
        rig.Hat.RelayMask.Should().Be(0x0A);
    }

    [TestMethod]
    public async Task AGrant_LiftsOnlyTheReasonsShown_ANewReasonRefuses_AndTheGrantIsStillUsedUp()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(scopes: 2)).Ready();
        rig.Scopes[0].Parked = false;
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));
        rig.Scopes[1].Fails = true; // a new reason after the grant

        var outcome = await rig.CloseAsync();

        outcome.Rule().Should().Be("NotSafe");
        outcome.ReasonKeys.Should().BeEquivalentTo("scope-not-clear:Main", "scope-unknown:Guide");
        rig.RelayOnWrites(CloseRelay).Should().Be(0);
        rig.Commands.Grants().Should().BeEmpty("used up whatever happens next");
        rig.Commands.Alerts().Should().Contain(alert => alert.ReasonKeys.Contains("scope-unknown:Guide"));
    }

    [TestMethod]
    public async Task AGrant_ForAnUnknownScope_StillAppliesWhenTheReReadFailsForAnotherCause()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(budgetMs: 300)).Ready();
        rig.Scope.RereadDelay = TimeSpan.FromSeconds(2); // the re-read times out: the scope is unknown
        (await rig.CloseAsync()).ReasonKeys.Should().Equal("scope-unknown:Main");
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-unknown:Main"));
        rig.Scope.RereadDelay = TimeSpan.Zero;
        rig.Scope.Fails = true; // now a driver error: the same key

        await Task.Delay(300); // the re-read spacing
        (await rig.CloseAsync()).Result.IsSuccessful.Should().BeTrue("a re-read timeout and a driver error are the same reason key");
    }

    [TestMethod]
    public async Task AGrant_NeverBypassesAPersonsLease_OrTheStopHold_AndIsNotUsedByThem()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));

        rig.Commands.NoteStop(AlpacaCommandRig.Person(), peerTrusted: false);
        rig.Commands.Grants().Should().BeEmpty("a person's Stop drops every grant");
        rig.Commands.ClearHold(AlpacaCommandRig.Person());
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));
        rig.Commands.NoteStop(AlpacaCommandRig.Person(), peerTrusted: false);
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));

        (await rig.CloseAsync()).Rule().Should().Be("StopHold");
        rig.Commands.Grants().Should().ContainSingle("the hold refuses at step 3, before the use-up point");
    }

    [TestMethod]
    public void AGrantNamesTheReasonsItLifts_AndACloseGrantNeverTheWeather()
    {
        var rig = AlpacaCommandRig.StartAsync().GetAwaiter().GetResult();
        try
        {
            var person = AlpacaCommandRig.Person();
            rig.Commands.Grant(person, Close()).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            rig.Commands.Grant(person, Close("weather-unsafe:Sky")).StatusCode.Should().Be(StatusCodes.Status400BadRequest, "weather never gates Close");
            rig.Commands.Grant(person, Close("scope-unknown:")).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            rig.Commands.Grants().Should().BeEmpty();
        }
        finally
        {
            rig.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task GrantingAgain_ReplacesTheGrant_AndRestartsItsWindow_AndRevokeEndsIt()
    {
        var time = new ManualTimeProvider();
        await using var rig = await AlpacaCommandRig.StartAsync(time: time);
        var first = rig.Commands.Grant(AlpacaCommandRig.Person("ada"), Close("scope-unknown:Main")).Grant!;
        time.Advance(TimeSpan.FromMinutes(3));

        var second = rig.Commands.Grant(AlpacaCommandRig.Person("grace", pin: true), Close("scope-not-clear:Main")).Grant!;

        rig.Commands.Grants().Should().ContainSingle().Which.Id.Should().Be(second.Id);
        second.Id.Should().NotBe(first.Id);
        second.SecondsRemaining.Should().Be(300);
        second.Ui.Should().Be("kiosk", "a PIN session is the kiosk's");
        first.Ui.Should().Be("session");
        rig.Commands.Revoke(RoofAlpacaCommandKind.Close, AlpacaCommandRig.Person()).Should().BeTrue();
        rig.Commands.Grants().Should().BeEmpty();
        rig.Commands.Revoke(RoofAlpacaCommandKind.Close, AlpacaCommandRig.Person()).Should().BeFalse();
    }

    [TestMethod]
    public async Task AGrant_LapsesAfterTheOverrideWindow_OnTheMonotonicClock_WhateverTheWallClockDoes()
    {
        var time = new SteppingWallClockTimeProvider();
        await using var rig = await AlpacaCommandRig.StartAsync(time: time);
        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.CloseOnly, OverrideWindowMinutes = 2 });
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-unknown:Main"));

        time.StepWallClock(TimeSpan.FromHours(-1)); // the wall clock goes back: the grant does not live longer
        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        rig.Commands.Tick();
        rig.Commands.Grants().Should().ContainSingle();

        time.StepWallClock(TimeSpan.FromHours(2));
        time.Advance(TimeSpan.FromSeconds(2));
        rig.Commands.Tick();
        rig.Commands.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grants_AreDropped_ByEveryChangeThePlanNames()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync()).Ready();
        var person = AlpacaCommandRig.Person();
        void Grant() => rig.Commands.Grant(person, Close("scope-unknown:Main"));

        Grant();
        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.Off });
        rig.Commands.Grants().Should().BeEmpty("AllowCommands changed");

        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.CloseOnly });
        Grant();
        rig.Options.Set(new RoofAlpacaCommandOptions { AllowCommands = RoofAlpacaCommandMode.CloseOnly, OverrideWindowMinutes = 7 });
        rig.Commands.Grants().Should().BeEmpty("the override window changed");

        Grant();
        rig.SafetyOptions.Set(new RoofSafetyOptions { InterlockMode = RoofSafetyInterlockMode.Warn, PollInterval = RoofSafetyOptions.MaxPollInterval, ReadTimeout = TimeSpan.FromSeconds(2) });
        rig.Commands.Grants().Should().ContainSingle("the same interlock mode is no change");
        var block = new RoofSafetyOptions { InterlockMode = RoofSafetyInterlockMode.Block, PollInterval = RoofSafetyOptions.MaxPollInterval, ReadTimeout = TimeSpan.FromSeconds(2) };
        rig.SafetyOptions.Set(block);
        rig.Commands.Grants().Should().BeEmpty("the interlock mode changed");

        Grant();
        rig.RotateCommanderKey();
        rig.Commands.Tick();
        rig.Commands.Grants().Should().BeEmpty("the External account's key changed");

        Grant();
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true);
        rig.Commands.Grants().Should().BeEmpty("a person took the lease");

        Grant();
        rig.Commands.Heartbeat(AlpacaCommandRig.Person("grace"), input: true);
        rig.Commands.Grants().Should().ContainSingle("keeping a lease already held is no new taking");
        rig.Commands.NoteStop(person, peerTrusted: false);
        rig.Commands.Grants().Should().BeEmpty("a human Stop");
    }

    [TestMethod]
    public async Task ConcurrentCommands_WithOneGrant_GiveExactlyOneStart()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => rig.CloseAsync()));

        rig.RelayOnWrites(CloseRelay).Should().Be(1, "one start");
        outcomes.Count(outcome => outcome.GrantId is not null).Should().Be(1, "one command used the grant");
    }

    [TestMethod]
    public async Task AGrantStaysUsed_AfterAFailedRelayStart()
    {
        await using var rig = (await AlpacaCommandRig.StartAsync(parked: false)).Ready();
        rig.Commands.Grant(AlpacaCommandRig.Person(), Close("scope-not-clear:Main"));
        rig.Hat.Registers.FailNextRelayWrites = 2;

        var outcome = await rig.CloseAsync();

        outcome.Result.IsSuccessful.Should().BeFalse();
        rig.Commands.Grants().Should().BeEmpty();
        rig.Hat.RelayMask.Should().Be(0x00);
    }
}
