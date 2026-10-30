using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The safety conditions in the client library: the calls (<c>GET Safety</c>, Open with an override), the words every
/// client shows, and the one rule that keeps Open from being offered (Block mode, not safe).
/// </summary>
[TestClass]
public sealed class RoofSafetyClientTests
{
    private static readonly RoofStatusResponse Closed = RoofServiceMock.Snapshot();

    // ---- The calls -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task GetSafety_ReadsTheConditions()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var safety = await client.Roof.GetSafetyAsync();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal(RoofSafetyStatus.NoSourcesReason);
        (await client.Roof.GetStatusAsync()).Safety!.Reasons.Should().Equal(RoofSafetyStatus.NoSourcesReason);
    }

    [TestMethod]
    public async Task OpenWithAnOverride_IsRefusedWithoutIt_AndGoesAheadWithIt()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var host = RoofClientApiTests.CreateHost(
            configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount),
            settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Warn" });
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await WaitUntilAsync(() => client.Roof.GetSafetyAsync().GetAwaiter().GetResult().State == RoofSafetyState.Unsafe, "the mount to be read");

        var refused = await RoofClientApiTests.RefusedAsync(() => client.Roof.OpenAsync());
        var opened = await client.Roof.OpenAsync(overrideSafety: true);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        refused.Code.Should().Be(RoofControllerErrorCode.SafetyOverrideRequired);
        refused.Message.Should().Be("The conditions are not confirmed safe. Open needs a safety override. [SafetyOverrideRequired]");
        refused.RoofStatus!.Safety!.State.Should().Be(RoofSafetyState.Unsafe);
        opened.Safety!.State.Should().Be(RoofSafetyState.Unsafe);
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    [TestMethod]
    public async Task OpenWithoutAnOverride_SendsNone()
    {
        using var host = RoofClientApiTests.CreateHost(settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Warn" });
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var refused = await RoofClientApiTests.RefusedAsync(() => client.Roof.OpenAsync(overrideSafety: false));

        refused.Code.Should().Be(RoofControllerErrorCode.SafetyOverrideRequired);
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    // ---- The words -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void DescribeRows_AddsSafety_AfterTheWatchdog_OnlyWhenTheControllerSendsIt()
    {
        RoofStatusText.DescribeRows(Closed).Select(row => row.Label).Should().NotContain("Safety", "an older controller sends none");

        var rows = RoofStatusText.DescribeRows(Closed with { Safety = Unknown(RoofSafetyInterlockMode.Off) });

        rows.Select(row => row.Label).Should().Equal("Roof", "Last stop", "Fault", "Limits", "Drive fault", "Inputs", "Relays", "Safety", "Controller");
        rows.Single(row => row.Label == "Safety").Value.Should().Be("UNKNOWN: No safety sources configured (advisory: the safety interlock is off)");
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Safe, RoofSafetyInterlockMode.Off, null, "safe (advisory: the safety interlock is off)")]
    [DataRow(RoofSafetyState.Safe, RoofSafetyInterlockMode.Warn, RoofControllerApiContract.AdminRole, "safe (safety interlock: warn)")]
    [DataRow(RoofSafetyState.Safe, RoofSafetyInterlockMode.Block, null, "safe (safety interlock: block)")]
    [DataRow(RoofSafetyState.Unsafe, RoofSafetyInterlockMode.Off, null, "UNSAFE: Telescope 'Main' is not parked; Weather monitor 'Cloud' reports unsafe weather (advisory: the safety interlock is off)")]
    [DataRow(RoofSafetyState.Unsafe, RoofSafetyInterlockMode.Warn, RoofControllerApiContract.AdminRole, "UNSAFE: Telescope 'Main' is not parked; Weather monitor 'Cloud' reports unsafe weather (Open needs a safety override from an admin)")]
    [DataRow(RoofSafetyState.Unknown, RoofSafetyInterlockMode.Warn, RoofControllerApiContract.OperatorRole, "UNKNOWN: Telescope 'Main' is not parked; Weather monitor 'Cloud' reports unsafe weather (Open needs a safety override from an operator)")]
    [DataRow(RoofSafetyState.Unknown, RoofSafetyInterlockMode.Block, null, "UNKNOWN: Telescope 'Main' is not parked; Weather monitor 'Cloud' reports unsafe weather (Open blocked by the safety interlock)")]
    public void DescribeSafety_SaysTheState_WhyNotSafe_AndWhatOpenDoes(RoofSafetyState state, RoofSafetyInterlockMode mode, string? role, string expected)
    {
        var safety = new RoofSafetyStatus
        {
            State = state,
            Reasons = state == RoofSafetyState.Safe ? [] : ["Telescope 'Main' is not parked", "Weather monitor 'Cloud' reports unsafe weather"],
            InterlockMode = mode,
            OverrideRole = role
        };

        RoofStatusText.DescribeSafety(safety).Should().Be(expected);
    }

    [TestMethod]
    public void DescribeSafetyState_IsTheBadgesWord()
    {
        RoofStatusText.DescribeSafetyState(RoofSafetyState.Safe).Should().Be("Safe");
        RoofStatusText.DescribeSafetyState(RoofSafetyState.Unsafe).Should().Be("Unsafe");
        RoofStatusText.DescribeSafetyState(RoofSafetyState.Unknown).Should().Be("Unknown");
    }

    [TestMethod]
    public void TheRefusals_HaveTheSharedWording()
    {
        RoofText.DescribeErrorCode(RoofControllerErrorCode.SafetyInterlockActive)
            .Should().Be("The safety interlock refused Open: the conditions are not confirmed safe.");
        RoofText.DescribeErrorCode(RoofControllerErrorCode.SafetyOverrideRequired)
            .Should().Be("The conditions are not confirmed safe. Open needs a safety override.");
        RoofControllerApiContract.HttpStatusFor(RoofControllerErrorCode.SafetyInterlockActive).Should().Be(409);
        RoofControllerApiContract.HttpStatusFor(RoofControllerErrorCode.SafetyOverrideRequired).Should().Be(409);
    }

    // ---- The rule --------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public void Block_NotSafe_KeepsOpenFromBeingOffered_ButNotClose(RoofSafetyState state)
    {
        var status = Closed with { Status = RoofControllerStatus.Stopped, Safety = new RoofSafetyStatus { State = state, InterlockMode = RoofSafetyInterlockMode.Block } };

        Motion(RoofMotionDirection.Opening, status).Should().Be(RoofCommandRules.SafetyInterlock);
        Motion(RoofMotionDirection.Closing, status).Should().BeNull("closing is never gated by the safety conditions");
        RoofCommandRules.IsOpenBlockedBySafety(status).Should().BeTrue();
        RoofCommandRules.DescribeMotionBlocks(Motion(RoofMotionDirection.Opening, status), null)
            .Should().Be("Open: the safety interlock blocks it: the safety conditions are not confirmed safe.");
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Safe, RoofSafetyInterlockMode.Block)]
    [DataRow(RoofSafetyState.Unsafe, RoofSafetyInterlockMode.Warn)]
    [DataRow(RoofSafetyState.Unknown, RoofSafetyInterlockMode.Off)]
    public void Otherwise_OpenIsOffered_AndTheControllerDecides(RoofSafetyState state, RoofSafetyInterlockMode mode)
    {
        var status = Closed with { Safety = new RoofSafetyStatus { State = state, InterlockMode = mode } };

        Motion(RoofMotionDirection.Opening, status).Should().BeNull();
        RoofCommandRules.IsOpenBlockedBySafety(status).Should().BeFalse();
        Motion(RoofMotionDirection.Opening, Closed).Should().BeNull("an older controller sends no safety conditions");
    }

    [TestMethod]
    public void TheControllersOwnReasons_ComeBeforeTheSafetyInterlock()
    {
        var status = Closed with
        {
            IsFaultLatched = true,
            Safety = new RoofSafetyStatus { State = RoofSafetyState.Unsafe, InterlockMode = RoofSafetyInterlockMode.Block }
        };

        Motion(RoofMotionDirection.Opening, status).Should().Be(RoofCommandRules.FaultLatched);
    }

    private static RoofSafetyStatus Unknown(RoofSafetyInterlockMode mode) => new()
    {
        State = RoofSafetyState.Unknown,
        Reasons = [RoofSafetyStatus.NoSourcesReason],
        InterlockMode = mode
    };

    private static string? Motion(RoofMotionDirection direction, RoofStatusResponse status)
        => RoofCommandRules.GetMotionBlockReason(direction, new RoofCommandState(status, false, true, false));
}
