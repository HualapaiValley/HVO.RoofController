using System.Net;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client's Stop helper (#44): sent at once on its own connection, and reported as acknowledged, unverified or failed
/// in the shared wording, never claiming more than the controller said.
/// </summary>
[TestClass]
public sealed class RoofStopTests
{
    private static Mock<IRoofControllerServiceV4> RoofReporting(RoofRelayRegisterState relayState)
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(relayState: relayState));
        return roof;
    }

    [TestMethod]
    [DataRow(RoofRelayRegisterState.Verified, RoofStopOutcome.Acknowledged, RoofStopText.AcknowledgedVerified)]
    [DataRow(RoofRelayRegisterState.Unverified, RoofStopOutcome.RelayUnverified, RoofStopText.AcknowledgedUnverified)]
    [DataRow(RoofRelayRegisterState.Unknown, RoofStopOutcome.Acknowledged, RoofStopText.Acknowledged)]
    public async Task AnAcceptedStop_ClaimsOnlyWhatTheRelayRegisterShowed(RoofRelayRegisterState relayState, RoofStopOutcome outcome, string message)
    {
        using var host = RoofClientApiTests.CreateHost(RoofReporting(relayState));
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var result = await client.StopAsync();

        result.Outcome.Should().Be(outcome);
        result.Message.Should().Be(message);
        result.IsAcknowledged.Should().Be(outcome == RoofStopOutcome.Acknowledged);
        result.Status!.RelayRegisterState.Should().Be(relayState);
        result.Error.Should().BeNull();
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public async Task AStopTheControllerCouldNotVerify_SendsTheOperatorToTheRoof()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.RelayUnverified);
        result.Message.Should().Be(RoofStopText.SentUnverified);
        result.Status!.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        result.Error.Should().BeOfType<RoofApiException>().Which.Code.Should().Be(RoofControllerErrorCode.RelayStateUnverified);
    }

    [TestMethod]
    public async Task AStopTheControllerRefused_IsAFailure_WithTheReason()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.HardwareUnavailable, "The relay HAT did not answer (test).")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        result.Message.Should().Be("Stop failed: The relay hardware is unavailable. [HardwareUnavailable] Use the stop control at the roof.");
    }

    [TestMethod]
    public async Task AStopWithAnEndedSession_SaysSo_AndEndsTheSessionOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var client = CreateClient(host);
        var session = await client.Auth.SignInAsync("alice", TestSecrets.Password);
        client.Credential = session;
        var ended = 0;
        session.Ended += (_, _) => ended++;
        await client.Auth.SignOutAsync();
        client.Credential = new RoofSessionCredential(session.Token, session.Name);
        var stale = (RoofSessionCredential)client.Credential;
        stale.Ended += (_, _) => ended++;

        var first = await client.StopAsync();
        var second = await client.StopAsync();

        first.Outcome.Should().Be(RoofStopOutcome.Failed);
        first.Message.Should().Be(RoofStopText.SignedOut);
        first.Error.Should().BeOfType<RoofApiException>().Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        second.Message.Should().Be(RoofStopText.SignedOut, "a second Stop is sent again, not skipped");
        stale.IsEnded.Should().BeTrue();
        ended.Should().Be(2, "the signed-out session and the stale copy each end once");
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task AnUnreachableController_IsAFailure_ThatSendsTheOperatorToTheRoof()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: FailingHandler.Refused);

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        result.Message.Should().Be("Stop failed: The controller could not be reached. Use the stop control at the roof.");
        result.Error.Should().BeOfType<HttpRequestException>();
    }

    [TestMethod]
    public async Task AControllerThatDoesNotAnswer_FailsWithinTheStopTimeout()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(
            host,
            new RoofApiKeyCredential(TestApiKeys.Viewer),
            stopTimeout: TimeSpan.FromMilliseconds(200),
            handler: FailingHandler.Silent);

        var result = await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        result.Message.Should().Be("Stop failed: The controller did not answer in time. Use the stop control at the roof.");
        result.Error.Should().BeOfType<TimeoutException>();
    }

    [TestMethod]
    public async Task ACallerCancellation_IsNotReportedAsAFailure()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: FailingHandler.Silent);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await client.Invoking(c => c.StopAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task AnAcceptedStopWhoseAnswerCannotBeRead_IsAcknowledged_WithoutARelayClaim()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: () => new FailingHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("not json") })));

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        result.Message.Should().Be(RoofStopText.Acknowledged);
        result.Status.Should().BeNull();
    }

    [TestMethod]
    public async Task Stop_IsNotQueuedBehindACommandInFlight()
    {
        using var openEntered = new SemaphoreSlim(0);
        using var releaseOpen = new SemaphoreSlim(0);
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Open())
            .Callback(() =>
            {
                openEntered.Release();
                releaseOpen.Wait(TimeSpan.FromSeconds(30));
            })
            .Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        var open = client.Roof.OpenAsync();
        (await openEntered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("Open reached the roof");
        RoofStopResult stop;
        try
        {
            stop = await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            open.IsCompleted.Should().BeFalse("Stop was answered while Open was still in flight");
        }
        finally
        {
            releaseOpen.Release();
        }

        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        (await open).Should().NotBeNull();
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public async Task TwoStops_AreTwoRequests()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var results = await Task.WhenAll(client.StopAsync(), client.StopAsync());

        results.Should().OnlyContain(result => result.Outcome == RoofStopOutcome.Acknowledged);
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(2));
    }

    [TestMethod]
    public async Task AKioskStop_StillWorks_AfterItsPinSessionWasEnded()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        var kiosk = new RoofKioskCredential(RoofClientApiTests.KioskKey);
        using var client = CreateClient(host, kiosk);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var session = await client.Auth.SignInWithPinAsync("olive", TestSecrets.Pin);
        await admin.Identity.EndSessionAsync(session.SessionId);

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Acknowledged, "the controller falls back to the device key for Stop");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public async Task AStopWithNoCredential_IsRefused_AsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var client = CreateClient(host);

        var result = await client.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        result.Message.Should().Be(RoofStopText.SignedOut);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }
}
