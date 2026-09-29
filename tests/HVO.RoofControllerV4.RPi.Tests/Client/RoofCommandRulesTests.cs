using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Why a client does not offer Open, Close or Clear fault: the same reasons, in the same order, in every client. The
/// controller still decides; these keep a client from offering what it would refuse, or motion on a roof it cannot watch.
/// </summary>
[TestClass]
public sealed class RoofCommandRulesTests
{
    private static readonly RoofStatusResponse Closed = RoofServiceMock.Snapshot();

    [TestMethod]
    public void AReadyStoppedRoof_OffersEveryMotionItIsNotAlreadyIn()
    {
        var stopped = Closed with { Status = RoofControllerStatus.Stopped };

        Motion(RoofMotionDirection.Opening, stopped).Should().BeNull();
        Motion(RoofMotionDirection.Closing, stopped).Should().BeNull();
        Motion(RoofMotionDirection.Opening, Closed).Should().BeNull();
        Motion(RoofMotionDirection.Closing, Closed).Should().Be(RoofCommandRules.AlreadyClosed);
        Motion(RoofMotionDirection.Opening, Closed with { Status = RoofControllerStatus.Open }).Should().Be(RoofCommandRules.AlreadyOpen);
        Motion(RoofMotionDirection.Opening, Closed with { Status = RoofControllerStatus.Opening }).Should().Be(RoofCommandRules.AlreadyOpen);
        Motion(RoofMotionDirection.Closing, Closed with { Status = RoofControllerStatus.Closing }).Should().Be(RoofCommandRules.AlreadyClosed);
    }

    [TestMethod]
    public void TheReasons_ComeInOrder_TheRoleFirst()
    {
        var everything = Closed with
        {
            Status = RoofControllerStatus.Opening,
            IsMoving = true,
            IsFaultLatched = true,
            IsDriveFaultActive = true,
            IsClearFaultInProgress = true,
        };

        Motion(RoofMotionDirection.Closing, everything, canOperate: false, inFlight: true, stale: true).Should().Be(RoofCommandRules.MotionRoleNeeded);
        Motion(RoofMotionDirection.Closing, null, inFlight: true).Should().Be(RoofCommandRules.NoStatus);
        Motion(RoofMotionDirection.Closing, everything, inFlight: true, stale: true).Should().Be(RoofCommandRules.Stale);
        Motion(RoofMotionDirection.Closing, everything with { IsInitialized = false, IsShuttingDown = true }, inFlight: true).Should().Be(RoofCommandRules.Initializing);
        Motion(RoofMotionDirection.Closing, everything with { IsShuttingDown = true }, inFlight: true).Should().Be(RoofCommandRules.ShuttingDown);
        Motion(RoofMotionDirection.Closing, everything, inFlight: true).Should().Be(RoofCommandRules.CommandInFlight);
        Motion(RoofMotionDirection.Closing, everything).Should().Be(RoofCommandRules.ClearFaultInProgress);
        Motion(RoofMotionDirection.Closing, everything with { IsClearFaultInProgress = false }).Should().Be(RoofCommandRules.FaultLatched);
        Motion(RoofMotionDirection.Closing, everything with { IsClearFaultInProgress = false, IsFaultLatched = false }).Should().Be(RoofCommandRules.DriveFault);
        Motion(RoofMotionDirection.Closing, everything with { IsClearFaultInProgress = false, IsFaultLatched = false, IsDriveFaultActive = null })
            .Should().Be(RoofCommandRules.Moving, "a drive-fault input that could not be read is not a fault");
    }

    [TestMethod]
    public void ARoleNotKnownYet_IsLeftToTheController()
    {
        Motion(RoofMotionDirection.Opening, Closed, canOperate: null).Should().BeNull();
        RoofCommandRules.GetClearFaultBlockReason(new RoofCommandState(Closed with { IsFaultLatched = true }, false, null, false)).Should().BeNull();
    }

    [TestMethod]
    public void OnlyOpeningOrClosing_IsAMotion()
    {
        var act = () => RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.None, new RoofCommandState(Closed, false, true, false));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ClearFault_IsOfferedForAnyFault_AndOnlyThen()
    {
        ClearFault(Closed).Should().Be(RoofCommandRules.NoFault);
        ClearFault(Closed with { IsFaultLatched = true }).Should().BeNull();
        ClearFault(Closed with { IsDriveFaultActive = true }).Should().BeNull();
        ClearFault(Closed with { Status = RoofControllerStatus.Error }).Should().BeNull();
        ClearFault(Closed with { IsFaultLatched = true, IsMoving = true }).Should().Be(RoofCommandRules.Moving);
        ClearFault(Closed with { IsFaultLatched = true, IsClearFaultInProgress = true }).Should().Be(RoofCommandRules.ClearFaultInProgress);
        ClearFault(Closed with { IsFaultLatched = true }, inFlight: true).Should().Be(RoofCommandRules.CommandInFlight);
        ClearFault(Closed with { IsFaultLatched = true }, stale: true).Should().Be(RoofCommandRules.Stale);
        ClearFault(null).Should().Be(RoofCommandRules.NoStatus);
        ClearFault(Closed with { IsFaultLatched = true }, canOperate: false).Should().Be(RoofCommandRules.ClearFaultRoleNeeded);
    }

    [TestMethod]
    public void HasFault_IsALatchedFault_TheDrivesFaultInput_OrTheErrorStatus()
    {
        RoofCommandRules.HasFault(Closed).Should().BeFalse();
        RoofCommandRules.HasFault(Closed with { IsDriveFaultActive = false }).Should().BeFalse();
        RoofCommandRules.HasFault(Closed with { IsDriveFaultActive = null }).Should().BeFalse();
        RoofCommandRules.HasFault(Closed with { IsFaultLatched = true }).Should().BeTrue();
        RoofCommandRules.HasFault(Closed with { IsDriveFaultActive = true }).Should().BeTrue();
        RoofCommandRules.HasFault(Closed with { Status = RoofControllerStatus.Error }).Should().BeTrue();
    }

    [TestMethod]
    public void MayHaveReachedController_OnlyWhenItMayHaveBeenSent()
    {
        RoofCommandRules.MayHaveReachedController(new HttpRequestException(HttpRequestError.ConnectionError, "refused")).Should().BeFalse();
        RoofCommandRules.MayHaveReachedController(new HttpRequestException(HttpRequestError.NameResolutionError, "no name")).Should().BeFalse();
        RoofCommandRules.MayHaveReachedController(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")).Should().BeFalse();
        RoofCommandRules.MayHaveReachedController(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new TimeoutException()).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new TaskCanceledException("t", new TimeoutException())).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new TaskCanceledException()).Should().BeFalse("the caller cancelled it");
        RoofCommandRules.MayHaveReachedController(new RoofProtocolException("garbled")).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new RoofApiException(HttpStatusCode.BadGateway)).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new RoofApiException(HttpStatusCode.GatewayTimeout)).Should().BeTrue();
        RoofCommandRules.MayHaveReachedController(new RoofApiException(HttpStatusCode.Conflict, RoofControllerErrorCode.FaultLatched)).Should().BeFalse();
    }

    [TestMethod]
    public void DescribeUnanswered_SaysTheRoofMayBeMoving_AndHowToStopIt()
    {
        RoofCommandRules.DescribeUnanswered(new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), "Open", "press F9")
            .Should().Be("The connection to the controller ended before its answer arrived. The Open may have reached the controller, and the roof may be moving. To stop it, press F9.");
        RoofCommandRules.DescribeUnanswered(new RoofProtocolException("garbled"), "Close", "press Stop")
            .Should().Be($"{RoofText.AnswerUnreadable} The Close may have reached the controller, and the roof may be moving. To stop it, press Stop.");
    }

    [TestMethod]
    [DataRow(null, null, null)]
    [DataRow("a", null, "Open: a.")]
    [DataRow(null, "b", "Close: b.")]
    [DataRow("a", "a", "Open and Close: a.")]
    [DataRow("a", "b", "Open and Close: a; b.")]
    public void DescribeMotionBlocks_IsOneLine(string? open, string? close, string? expected)
        => RoofCommandRules.DescribeMotionBlocks(open, close).Should().Be(expected);

    [TestMethod]
    public void Capitalize_StartsWithACapital_AndChangesNothingElse()
    {
        RoofCommandRules.Capitalize(RoofCommandRules.NoStatus).Should().Be("There is no status yet");
        RoofCommandRules.Capitalize(string.Empty).Should().BeEmpty();
        RoofCommandRules.Capitalize("Already").Should().Be("Already");
    }

    private static string? Motion(RoofMotionDirection direction, RoofStatusResponse? status, bool? canOperate = true, bool inFlight = false, bool stale = false)
        => RoofCommandRules.GetMotionBlockReason(direction, new RoofCommandState(status, stale, canOperate, inFlight));

    private static string? ClearFault(RoofStatusResponse? status, bool? canOperate = true, bool inFlight = false, bool stale = false)
        => RoofCommandRules.GetClearFaultBlockReason(new RoofCommandState(status, stale, canOperate, inFlight));
}
