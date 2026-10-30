using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>The state colours every client gives Stop, Open and Close (#208): colour only, never eligibility.</summary>
[TestClass]
public sealed class RoofActionStatesTests
{
    [TestMethod]
    [DataRow(RoofControllerStatus.Closed, RoofMotionDirection.None, false)]
    [DataRow(RoofControllerStatus.Open, RoofMotionDirection.None, false)]
    [DataRow(RoofControllerStatus.Stopped, RoofMotionDirection.None, false)]
    [DataRow(RoofControllerStatus.PartiallyOpen, RoofMotionDirection.None, false)]
    [DataRow(RoofControllerStatus.Error, RoofMotionDirection.None, false)]
    [DataRow(RoofControllerStatus.Opening, RoofMotionDirection.Opening, true)]
    [DataRow(RoofControllerStatus.Closing, RoofMotionDirection.Closing, true)]
    [DataRow(RoofControllerStatus.PartiallyOpen, RoofMotionDirection.Opening, true)]
    [DataRow(RoofControllerStatus.Opening, RoofMotionDirection.None, true)]
    public void Stop_IsBright_WhileTheRoofMovesOrIsCommandedTo(RoofControllerStatus status, RoofMotionDirection motion, bool active)
        => RoofActionStates.IsStopActive(RoofServiceMock.Snapshot(status, motion)).Should().Be(active);

    [TestMethod]
    public void Stop_IsIdle_WithNoStatus() => RoofActionStates.IsStopActive(null).Should().BeFalse();

    [TestMethod]
    public void Motion_IsRunningOnlyInItsOwnDirection_OnALiveStatus()
    {
        var opening = RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening);
        RoofActionStates.ForMotion(RoofMotionDirection.Opening, opening, false, RoofCommandRules.Moving).Should().Be(RoofActionState.Running);
        RoofActionStates.ForMotion(RoofMotionDirection.Closing, opening, false, RoofCommandRules.Moving).Should().Be(RoofActionState.Disabled);
        RoofActionStates.ForMotion(RoofMotionDirection.Opening, opening, true, RoofCommandRules.Stale)
            .Should().Be(RoofActionState.Disabled, "a stale status cannot show the roof moving now");

        var closing = RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing);
        RoofActionStates.ForMotion(RoofMotionDirection.Closing, closing, false, RoofCommandRules.Moving).Should().Be(RoofActionState.Running);
        RoofActionStates.ForMotion(RoofMotionDirection.Opening, closing, false, RoofCommandRules.Moving).Should().Be(RoofActionState.Disabled);
    }

    [TestMethod]
    public void Motion_IsAvailableOrDisabled_ByTheSharedCommandRules()
    {
        var closed = RoofServiceMock.Snapshot(RoofControllerStatus.Closed);
        var state = new RoofCommandState(closed, false, true, false);
        var open = RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, state);
        var close = RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Closing, state);
        RoofActionStates.ForMotion(RoofMotionDirection.Opening, closed, false, open).Should().Be(RoofActionState.Available);
        RoofActionStates.ForMotion(RoofMotionDirection.Closing, closed, false, close).Should().Be(RoofActionState.Disabled);
        RoofActionStates.ForMotion(RoofMotionDirection.Opening, null, true, RoofCommandRules.NoStatus).Should().Be(RoofActionState.Disabled);
    }

    [TestMethod]
    public void Motion_RefusesADirectionThatIsNotAButton()
        => FluentActions.Invoking(() => RoofActionStates.ForMotion(RoofMotionDirection.None, null, false, null))
            .Should().Throw<ArgumentOutOfRangeException>();
}
