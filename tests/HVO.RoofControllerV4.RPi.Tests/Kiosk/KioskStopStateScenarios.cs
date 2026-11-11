using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// Stop's state colours on the kiosk's screen against the emulated plant (#208): the production host and settings, the
/// emulated HAT, drive and roof, and the kiosk's own console and shell at the Touch Display 2's size. Every move is
/// commanded through the controller by a touch on the screen, and each state is checked while the plant is in it: Stop
/// is the dim red and enabled while the roof rests, and the bright red while it opens and while it closes.
/// <see cref="KioskPolishTests"/> draws the same states on <see cref="KioskHarness"/>, whose roof is a mock.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class KioskStopStateScenarios
{
    private const string Operator = "olga";

    // The documented 2 m roof: about 21 s from limit to limit, so a move outlasts the render that checks it.
    private const double TravelMeters = 2;

    // Where Stop is touched: far enough from the closed limit that closing again takes several seconds.
    private const double MidTravelMeters = 0.6;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Stop_IsDimAndEnabledWhileTheRoofRests_AndBrightWhileTheEmulatedRoofOpensAndCloses()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: KioskKeySettings(), travelMeters: TravelMeters));
        using (var admin = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await admin.Identity.AddUserAsync(new RoofUserCreateRequest
            {
                Name = Operator,
                Role = RoofControllerApiContract.OperatorRole,
                Password = TestSecrets.Password,
                Pin = TestSecrets.Pin
            });
        }

        using var client = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofKioskCredential(RoofClientApiTests.KioskKey));
        await using var console = new KioskConsole(client);
        console.Start();
        await UntilAsync(() => console.View.FeedLabel == "live", "the emulated controller's live status");
        await using var screen = await KioskScreen.ShowAsync(console, 1280, 720, 8.2);

        // Closed and at rest, locked and then unlocked. RenderAsync also checks the drawn Stop is in its state's colour.
        await screen.RenderAsync("emulated-locked", TestContext);
        await OnUiAsync(() => KioskPolishTests.ShouldBeIdle(screen.Shell));
        (await console.UnlockAsync(Operator, TestSecrets.Pin)).Should().BeNull("the operator's PIN unlocks the kiosk");
        await UntilAsync(() => console.View is { UnlockedBy: Operator, OpenBlock: null }, "Open to be offered");
        await screen.RenderAsync("emulated-idle", TestContext);
        await OnUiAsync(() => KioskPolishTests.ShouldBeIdle(screen.Shell));
        rig.Plant.VelocityMetersPerSecond.Should().Be(0, "the roof rests at its closed limit");

        // Opening: the touch on Open goes through the controller to the drive.
        await screen.TouchAsync(await OnUiAsync(() => screen.Find("open")!));
        await rig.WaitForControllerAsync(status => status is { Status: RoofControllerStatus.Opening, IsMoving: true }, "the controller to open the roof");
        await rig.WaitForPlantAsync(plant => plant.VelocityMetersPerSecond > 0, "the emulated roof to travel towards open");
        await UntilAsync(() => console.View.Status?.Status == RoofControllerStatus.Opening, "the kiosk to show the opening roof");
        await screen.RenderAsync("emulated-opening", TestContext);
        await OnUiAsync(() =>
        {
            KioskPolishTests.ShouldBeActive(screen.Shell);
            screen.Shell.RoofPage.OpenState.Should().Be(RoofActionState.Running);
        });
        StillMoving(rig, RoofControllerStatus.Opening);

        // Stop, once the roof is well under way (so the Close that follows has travel to check): bright when touched,
        // dim again once the roof is at rest.
        await rig.WaitForPlantAsync(plant => plant.PositionMeters > MidTravelMeters, "the roof to be well under way");
        await OnUiAsync(() => KioskPolishTests.ShouldBeActive(screen.Shell));
        await screen.TouchAsync(screen.Shell.Stop);
        await UntilAsync(() => console.View is { StopInFlight: false, StopOutcome: RoofStopOutcome.Acknowledged }, "Stop's acknowledgement");
        var stopped = await rig.WaitForRestAsync("the kiosk's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("Stop ended the move before the open limit");
        await UntilAsync(() => console.View is { Status.IsMoving: false, CloseBlock: null }, "the kiosk to show the stopped roof and offer Close");
        await screen.RenderAsync("emulated-stopped", TestContext);
        await OnUiAsync(() => KioskPolishTests.ShouldBeIdle(screen.Shell));

        // Closing, back to the closed limit.
        await screen.TouchAsync(await OnUiAsync(() => screen.Find("close")!));
        await rig.WaitForControllerAsync(status => status is { Status: RoofControllerStatus.Closing, IsMoving: true }, "the controller to close the roof");
        await rig.WaitForPlantAsync(plant => plant.VelocityMetersPerSecond < 0, "the emulated roof to travel towards closed");
        await UntilAsync(() => console.View.Status?.Status == RoofControllerStatus.Closing, "the kiosk to show the closing roof");
        await screen.RenderAsync("emulated-closing", TestContext);
        await OnUiAsync(() =>
        {
            KioskPolishTests.ShouldBeActive(screen.Shell);
            screen.Shell.RoofPage.CloseState.Should().Be(RoofActionState.Running);
        });
        StillMoving(rig, RoofControllerStatus.Closing);

        var closed = await rig.WaitForControllerAsync(status => status.Status == RoofControllerStatus.Closed && !status.IsMoving, "the roof to reach its closed limit");
        closed.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        await rig.WaitForRestAsync("the Close");
        await UntilAsync(() => console.View.Status is { Status: RoofControllerStatus.Closed, IsMoving: false }, "the kiosk to show the closed roof");
        await screen.RenderAsync("emulated-closed", TestContext);
        await OnUiAsync(() => KioskPolishTests.ShouldBeIdle(screen.Shell));

        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    // The state was drawn and checked while the plant was in it: the controller still commands the move afterwards.
    private static void StillMoving(EmulatedRoofRig rig, RoofControllerStatus direction)
    {
        var status = rig.Controller.GetCurrentStatusSnapshot();
        status.IsMoving.Should().BeTrue("the roof must still be moving once Stop's colour was checked");
        status.Status.Should().Be(direction);
    }

    /// <summary>The kiosk's device key, as the installation configures it: a viewer key that may unlock with a PIN.</summary>
    private static Dictionary<string, string?> KioskKeySettings() => new()
    {
        ["RoofControllerSecurity:ApiKeys:4:Name"] = "stop-state-kiosk",
        ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:4:Key"] = RoofClientApiTests.KioskKey,
        ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
    };
}
