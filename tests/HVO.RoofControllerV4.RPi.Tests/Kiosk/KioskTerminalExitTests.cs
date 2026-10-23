using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging.Abstractions;
using KioskProgram = HVO.RoofControllerV4.Kiosk.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
public sealed class KioskTerminalExitTests
{
    [TestMethod]
    [DataRow(KioskHarness.Operator)]
    [DataRow(KioskHarness.Admin)]
    public async Task AVerifiedOperatorPin_CanExitOnlyTheUi_AndReturnLocked(string name)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(name);
        var exits = 0;
        var exit = new KioskTerminalExit(harness.Console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        exit.IsConfirming.Should().BeTrue();
        exits.Should().Be(0, "confirmation is required");
        await exit.ConfirmAsync();
        exits.Should().Be(1);
        await exit.ConfirmAsync();
        exits.Should().Be(1, "a confirmed shutdown cannot be queued twice");
        harness.Sent(HttpMethod.Get, RoofApiRoutes.Me).Should().BeGreaterThan(0);

        KioskProgram.Close(harness.Console, NullLogger.Instance);

        harness.Credential.PinSession.Should().BeNull();
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(0);
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);
        harness.Calls(nameof(IRoofControllerServiceV4.Close)).Should().Be(0);
        using var observer = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        (await observer.Roof.GetStatusAsync()).Status.Should().Be(RoofControllerStatus.Closed);

        // A new process has a fresh device credential, with no session restored from disk or the old console.
        using var returningClient = ClientTestSupport.CreateClient(harness.Host, new RoofKioskCredential(RoofClientApiTests.KioskKey));
        await using var returning = new KioskConsole(returningClient);
        returning.Start();
        returning.View.IsUnlocked.Should().BeFalse();
        await returning.StopAsync();
        returning.View.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged);
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
    }

    [TestMethod]
    [DataRow("device")]
    [DataRow("viewer")]
    [DataRow("password")]
    [DataRow("expired")]
    public async Task DeviceOnlyViewerPasswordAndExpiredSessions_CannotExit(string kind)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        if (kind != "device")
        {
            await harness.UnlockAsync(KioskHarness.Operator);
            var session = harness.Credential.PinSession!;
            harness.Credential.UsePinSession(kind switch
            {
                "viewer" => session with { Role = RoofControllerApiContract.ViewerRole },
                "password" => session with { Kind = RoofCredentialKind.Session },
                _ => session with { ExpiresUtc = KioskHarness.Start - TimeSpan.FromSeconds(1) }
            });
        }
        var exits = 0;
        var exit = new KioskTerminalExit(harness.Console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        exit.IsConfirming.Should().BeFalse();
        exit.Message.Should().Be(KioskTerminalExit.NeedsOperator);
        await exit.ConfirmAsync();
        exits.Should().Be(0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RevokedOrServerExpiredSession_IsRevalidatedAndRefused(bool expire)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var exits = 0;
        var exit = new KioskTerminalExit(harness.Console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        if (expire) harness.ServerClock.Advance(TimeSpan.FromDays(2));
        else
        {
            using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
            await admin.Identity.EndSessionAsync(harness.Credential.PinSession!.SessionId);
        }
        await exit.ConfirmAsync();
        exits.Should().Be(0);
        harness.Console.View.IsUnlocked.Should().BeFalse();
    }

    [TestMethod]
    public async Task CancelWhileChecking_IgnoresTheLateAnswer_AndStopStillWorks()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DelayAnswer = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/" + RoofApiRoutes.Me)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var exits = 0;
        var exit = new KioskTerminalExit(harness.Console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        var confirming = exit.ConfirmAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Console.StopAsync();
        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged);
        exit.Cancel();
        release.SetResult();
        await confirming;
        exits.Should().Be(0);
        exit.IsBusy.Should().BeFalse();
        harness.Console.View.IsUnlocked.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NetworkAndShutdownErrors_LeaveTheUiRunning(bool network)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var exit = new KioskTerminalExit(harness.Console, () => Task.FromResult(false));
        exit.Ask();
        harness.Reachable = !network;
        await exit.ConfirmAsync();
        exit.Message.Should().Contain(network ? "could not be verified" : "could not exit");
        exit.IsBusy.Should().BeFalse();
        harness.Console.View.IsUnlocked.Should().BeTrue();
    }

    [TestMethod]
    public async Task ASessionReplacedDuringVerification_CannotUseTheOldConfirmation()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DelayAnswer = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/" + RoofApiRoutes.Me)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var exits = 0;
        var exit = new KioskTerminalExit(harness.Console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        var confirming = exit.ConfirmAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await harness.Console.LockAsync();
            await harness.UnlockAsync(KioskHarness.Admin);
        }
        finally { release.TrySetResult(); }
        await confirming;
        exits.Should().Be(0);
        exit.IsConfirming.Should().BeFalse();
    }

    [TestMethod]
    public async Task CancelBeforeConfirmation_PerformsNoAuthorizationOrShutdown()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var before = harness.Sent(HttpMethod.Get, RoofApiRoutes.Me);
        var exit = new KioskTerminalExit(harness.Console, () => throw new AssertFailedException("Unexpected shutdown"));
        exit.Ask();
        exit.Cancel();
        await exit.ConfirmAsync();
        harness.Sent(HttpMethod.Get, RoofApiRoutes.Me).Should().Be(before);
        harness.Console.View.IsUnlocked.Should().BeTrue();
    }
}
