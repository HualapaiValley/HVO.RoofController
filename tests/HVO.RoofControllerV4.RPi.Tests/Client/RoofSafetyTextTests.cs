using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client library's shared rule for the safety override (#215) and the words every client uses for it and for the
/// indicator (#207): Unknown is asked about as unsafe, Close never is, and an operator is not asked when only admins may
/// override.
/// </summary>
[TestClass]
public sealed class RoofSafetyTextTests
{
    private static readonly RoofStatusResponse Closed = RoofServiceMock.Snapshot();

    [TestMethod]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Unknown, true)]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Unsafe, true)]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Safe, false)]
    [DataRow(RoofSafetyInterlockMode.Off, RoofSafetyState.Unsafe, false)]
    [DataRow(RoofSafetyInterlockMode.Off, RoofSafetyState.Unknown, false)]
    [DataRow(RoofSafetyInterlockMode.Block, RoofSafetyState.Unsafe, false)]
    public void Open_NeedsTheOverride_OnlyInWarnMode_WhileTheConditionsAreNotSafe(RoofSafetyInterlockMode mode, RoofSafetyState state, bool needs)
    {
        RoofCommandRules.NeedsSafetyOverride(Closed with { Safety = new RoofSafetyStatus { State = state, InterlockMode = mode } }).Should().Be(needs);
    }

    [TestMethod]
    public void AControllerThatSendsNoConditions_OrNoStatus_IsNeverAskedAbout()
    {
        RoofCommandRules.NeedsSafetyOverride(Closed).Should().BeFalse("an older controller decides for itself, and its refusal asks");
        RoofCommandRules.NeedsSafetyOverride(null).Should().BeFalse();
    }

    [TestMethod]
    public void OverridesNarrowedToAdmins_BlockOpen_ForACallerKnownNotToBeOne_ButNotClose()
    {
        var status = Closed with
        {
            Status = RoofControllerStatus.Stopped,
            Safety = new RoofSafetyStatus { State = RoofSafetyState.Unknown, InterlockMode = RoofSafetyInterlockMode.Warn, OverrideRole = RoofControllerApiContract.AdminRole }
        };

        RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, new RoofCommandState(status, false, true, false, IsAdmin: false))
            .Should().Be(RoofCommandRules.SafetyOverrideNeedsAdmin);
        RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, new RoofCommandState(status, false, true, false, IsAdmin: true)).Should().BeNull();
        RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, new RoofCommandState(status, false, true, false)).Should().BeNull("the controller decides while the role is not known");
        RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Closing, new RoofCommandState(status, false, true, false, IsAdmin: false)).Should().BeNull("Close is never gated");
        RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, new RoofCommandState(status with { Safety = status.Safety! with { OverrideRole = RoofControllerApiContract.OperatorRole } }, false, true, false, IsAdmin: false))
            .Should().BeNull("anyone who may open may override by default");
    }

    [TestMethod]
    public void ARefusalForWantOfTheOverride_IsKnownAsSuch()
    {
        RoofCommandRules.IsSafetyOverrideRequest(new RoofApiException(System.Net.HttpStatusCode.Conflict, RoofControllerErrorCode.SafetyOverrideRequired)).Should().BeTrue();
        RoofCommandRules.IsSafetyOverrideRequest(new RoofApiException(System.Net.HttpStatusCode.Conflict, RoofControllerErrorCode.SafetyInterlockActive)).Should().BeFalse();
        RoofCommandRules.IsSafetyOverrideRequest(new HttpRequestException()).Should().BeFalse();
    }

    [TestMethod]
    public void TheQuestion_SaysTheConditions_WhatUnknownMeans_AndWhatTheOverrideDoes()
    {
        var unsafeLines = RoofSafetyText.DescribeOverrideQuestion(new RoofSafetyStatus
        {
            State = RoofSafetyState.Unsafe,
            Reasons = ["Telescope 'Main' is not parked", "Weather monitor 'Cloud' reports unsafe weather"],
            InterlockMode = RoofSafetyInterlockMode.Warn
        });
        var unknownLines = new RoofSafetyOverrideQuestion(new RoofSafetyStatus { State = RoofSafetyState.Unknown, Reasons = [RoofSafetyStatus.NoSourcesReason] }).Lines;

        unsafeLines.Should().Equal(
            "The safety conditions are UNSAFE: Telescope 'Main' is not parked; Weather monitor 'Cloud' reports unsafe weather.",
            "Open the roof anyway? The override is logged with your name. Close and Stop are never affected.");
        unknownLines.Should().Equal(
            "The safety conditions are UNKNOWN: No safety sources configured.",
            "Unknown counts as unsafe: nothing confirms that the telescopes are parked and the weather is safe.",
            RoofSafetyText.OverrideConsequence);
        RoofSafetyText.DescribeOverrideQuestion(null)[0].Should().Be("The safety conditions are UNKNOWN: the controller did not report them.");
        new RoofSafetyOverrideQuestion(null).Title.Should().Be("Safety override");
    }

    [TestMethod]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Unknown, null, "Open asks for a safety override (logged)")]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Unsafe, RoofControllerApiContract.AdminRole, "Open asks an admin for a safety override (logged)")]
    [DataRow(RoofSafetyInterlockMode.Warn, RoofSafetyState.Safe, null, "Open goes ahead")]
    [DataRow(RoofSafetyInterlockMode.Block, RoofSafetyState.Unsafe, null, "Open is blocked by the safety interlock")]
    [DataRow(RoofSafetyInterlockMode.Block, RoofSafetyState.Safe, null, "Open goes ahead")]
    [DataRow(RoofSafetyInterlockMode.Off, RoofSafetyState.Unsafe, null, "Advisory: the safety interlock is off")]
    public void WhatOpenDoes_IsSaidWithTheIndicator(RoofSafetyInterlockMode mode, RoofSafetyState state, string? overrideRole, string words)
    {
        RoofSafetyText.DescribeOpen(new RoofSafetyStatus { InterlockMode = mode, State = state, OverrideRole = overrideRole }).Should().Be(words);
    }

    [TestMethod]
    public void EachSource_AndTheFirstReason_AreSaidAsTheControllerGaveThem()
    {
        RoofSafetyText.DescribeSource(new RoofSafetySourceStatus { State = RoofSafetyState.Unsafe, Reason = "Weather monitor 'Cloud' reports unsafe weather", Detail = "rain" })
            .Should().Be("Unsafe: Weather monitor 'Cloud' reports unsafe weather (rain)");
        RoofSafetyText.DescribeSource(new RoofSafetySourceStatus { State = RoofSafetyState.Unknown, Reason = "Telescope 'Main' cannot be read (the device is not connected)" })
            .Should().Be("Unknown: Telescope 'Main' cannot be read (the device is not connected)");
        RoofSafetyText.DescribeFirstReason(new RoofSafetyStatus { State = RoofSafetyState.Unsafe, Reasons = ["a", "b", "c"] }).Should().Be("a (+2 more)");
        RoofSafetyText.DescribeFirstReason(new RoofSafetyStatus { State = RoofSafetyState.Unknown, Reasons = ["a"] }).Should().Be("a");
        RoofSafetyText.DescribeFirstReason(new RoofSafetyStatus { State = RoofSafetyState.Safe }).Should().BeEmpty();
    }

    [TestMethod]
    public void ASourceThatDoesNotCount_SaysWhy()
    {
        RoofSafetyText.DescribeSource(new RoofSafetySourceStatus
        {
            Kind = RoofSafetySourceKind.TelescopePark,
            State = RoofSafetyState.Unsafe,
            Reason = "Telescope 'Solar' is not parked",
            Counts = false
        }).Should().Be("Unsafe: Telescope 'Solar' is not parked [does not block the roof]");
        RoofSafetyText.DescribeSource(new RoofSafetySourceStatus
        {
            Kind = RoofSafetySourceKind.Weather,
            State = RoofSafetyState.Unknown,
            Reason = "Weather monitor 'Old sensor' cannot be read",
            Detail = "spare",
            Counts = false
        }).Should().Be("Unknown: Weather monitor 'Old sensor' cannot be read (spare) [ignored]");
        new RoofSafetySourceStatus().Counts.Should().BeTrue("a source counts unless the controller says otherwise, as every one did before the setting");
        System.Text.Json.JsonSerializer.Deserialize<RoofSafetySourceStatus>("{\"name\":\"Main\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!
            .Counts.Should().BeTrue("an older controller sends no counts");
    }

    [TestMethod]
    public void TheStatusRows_ListEachSource_AfterTheSafetyRow()
    {
        var rows = RoofStatusText.DescribeRows(Closed with
        {
            Safety = new RoofSafetyStatus
            {
                State = RoofSafetyState.Safe,
                InterlockMode = RoofSafetyInterlockMode.Warn,
                Sources =
                [
                    new RoofSafetySourceStatus { Name = "Main", State = RoofSafetyState.Safe, Reason = "Telescope 'Main' is parked" },
                    new RoofSafetySourceStatus { Name = "Cloud", Kind = RoofSafetySourceKind.Weather, State = RoofSafetyState.Safe, Reason = "Weather monitor 'Cloud' reports safe weather" }
                ]
            }
        });

        var labels = rows.Select(row => row.Label).ToList();
        labels.IndexOf("Safety").Should().Be(labels.IndexOf(RoofStatusText.SafetySourceLabel) - 1);
        rows.Where(row => row.Label == RoofStatusText.SafetySourceLabel).Select(row => row.Value).Should().Equal(
            "Safe: Telescope 'Main' is parked",
            "Safe: Weather monitor 'Cloud' reports safe weather");
    }
}
