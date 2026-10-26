using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging.Abstractions;
using KioskProgram = HVO.RoofControllerV4.Kiosk.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class KioskTerminalScenarios
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Exit_KeepsControllerAvailable_AndEndsOnlyTheKiosksLeaseRenewals(bool leaseEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = RoofClientApiTests.KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
        };
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: settings, travelMeters: 2));
        var controller = rig.Controller;
        await rig.ConfigureAsync(request => request with { OperatorLeaseTimeoutSeconds = leaseEnabled ? 5 : null });
        using var client = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofKioskCredential(RoofClientApiTests.KioskKey));
        using (var admin = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofApiKeyCredential(TestApiKeys.Admin)))
            await admin.Identity.AddUserAsync(new RoofUserCreateRequest
            {
                Name = KioskHarness.Operator, Role = RoofControllerApiContract.OperatorRole,
                Password = TestSecrets.Password, Pin = TestSecrets.Pin
            });
        await using var console = new KioskConsole(client);
        console.Start();
        await ClientTestSupport.WaitUntilAsync(() => console.View.FeedLabel == "live", "the emulated controller status");
        (await console.UnlockAsync(KioskHarness.Operator, TestSecrets.Pin)).Should().BeNull();
        await console.OpenAsync();
        await rig.WaitForControllerAsync(status => status.IsMoving, "the emulated plant to start");
        var exits = 0;
        var exit = new KioskTerminalExit(console, () => { exits++; return Task.FromResult(true); });
        exit.Ask();
        await exit.ConfirmAsync();
        exits.Should().Be(1);
        KioskProgram.Close(console, NullLogger.Instance);
        ((RoofKioskCredential)client.Credential!).PinSession.Should().BeNull();

        using var observer = rig.CreateApiClient(TestApiKeys.Operator);
        var afterExit = await observer.StatusAsync();
        afterExit.CommandedMotion.Should().Be(RoofMotionDirection.Opening, "exiting sends no Stop or motion command");
        if (leaseEnabled)
        {
            var stopped = await rig.WaitForControllerAsync(status => !status.IsMoving && status.CommandedMotion == RoofMotionDirection.None, "ordinary lease expiry");
            stopped.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
            stopped.IsFaultLatched.Should().BeFalse();
            stopped.ShouldBeDeenergized(rig);
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            (await observer.StatusAsync()).CommandedMotion.Should().Be(RoofMotionDirection.Opening, "closing a UI adds no stop policy when the lease is disabled");
            (await observer.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
        }
        rig.Controller.Should().BeSameAs(controller, "only the client closed; controller supervision never restarted");
        await rig.WaitForRestAsync("the emulated drive to stop");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
