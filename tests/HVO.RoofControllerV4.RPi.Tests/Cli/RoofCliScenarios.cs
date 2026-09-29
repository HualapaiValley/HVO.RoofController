using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Terminal.Gui.Input;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof</c> against the emulated roof (#45). The controller holds each motion on a 5 s operator lease, and the
/// roof takes longer than that to travel, so a motion reaches its limit only if the command line (or the terminal
/// interface) renews the lease as it follows the roof.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class RoofCliScenarios
{
    private const double Lease = 5;

    [TestMethod]
    public async Task TheCommandLine_OpensTheRoofToItsLimit_RenewingTheLease_ThenClosesAndStopsIt()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        (await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = Lease })).OperatorLeaseTimeoutSeconds.Should().Be(Lease);
        using var cli = new CliRig(() => rig.App.Server);
        cli.UseApiKey(TestApiKeys.Operator);

        var status = await cli.RunAsync("status");
        status.Code.Should().Be(RoofExitCode.Success, status.ToString());
        status.Out.Should().Contain(RoofText.DescribePosition(RoofControllerStatus.Closed));

        var started = DateTime.UtcNow;
        var open = await cli.RunAsync("open");
        var took = DateTime.UtcNow - started;

        open.Code.Should().Be(RoofExitCode.Success, open.ToString());
        open.Out.Should().Contain("Open accepted. Following the motion; Ctrl+C sends Stop.");
        open.Out.Should().Contain($"Done. Roof: {RoofText.DescribePosition(RoofControllerStatus.Open)}");
        took.Should().BeGreaterThan(TimeSpan.FromSeconds(Lease), "the travel outlasts one lease, so the command renewed it");
        var opened = await rig.WaitForRestAsync("the open");
        opened.Status.Should().Be(RoofControllerStatus.Open);
        opened.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached, "the lease never ran out");

        var close = await cli.RunAsync("close", "--no-wait");
        close.Code.Should().Be(RoofExitCode.Success, close.ToString());
        close.Out.Should().Contain("Close accepted.");
        await rig.WaitForPlantAsync(plant => plant.PositionMeters < 0.9, "the roof to travel");

        var stop = await cli.RunAsync("stop");
        stop.Code.Should().Be(RoofExitCode.Success, stop.ToString());
        stop.Out.Should().Contain(RoofStopText.AcknowledgedVerified, "the emulated HAT reads the relays back");
        var stopped = await rig.WaitForRestAsync("the stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);

        var json = await cli.RunAsync("status", "--json");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("status").GetString().Should().Be(stopped.Status.ToString()).And.StartWith("Partially");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheTerminalInterface_OpensTheRoof_RenewsTheLease_AndF9StopsIt()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 2.0));
        (await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = Lease })).OperatorLeaseTimeoutSeconds.Should().Be(Lease);
        using var cli = new CliRig(() => rig.App.Server);
        cli.UseApiKey(TestApiKeys.Operator);

        // From here the test drives the interface on this thread, so it blocks rather than awaits.
        using (var tui = new TuiDriver(cli))
        {
            tui.WaitIdle("the closed roof", () => tui.Ui.Status is { IsInitialized: true, Status: RoofControllerStatus.Closed }, TimeSpan.FromSeconds(20));
            var page = (HVO.RoofControllerV4.Cli.Ui.RoofUiRoofPage)tui.Ui.CurrentPage;

            page.OpenButton.InvokeCommand(Command.Accept);
            tui.WaitIdle("the roof to move", () => tui.Ui.HoldsLease && tui.Ui.Status is { IsMoving: true }, TimeSpan.FromSeconds(20));

            // Two leases' worth of time, in real time for the roof and in one-second ticks for the interface's timer.
            for (var second = 0; second < 2 * Lease; second++)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                tui.Tick();
            }

            tui.WaitIdle("the renewals");
            tui.Ui.Status!.IsMoving.Should().BeTrue("the interface renewed the lease: {0}", tui.Ui.Message);
            rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue();

            tui.Press(Key.F9);
            tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending, TimeSpan.FromSeconds(20));
            tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
            tui.WaitIdle("the stopped roof", () => tui.Ui.Status is { IsMoving: false, CommandedMotion: RoofMotionDirection.None }, TimeSpan.FromSeconds(20));

            tui.Ui.HoldsLease.Should().BeFalse();
            tui.Ui.Status!.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
            tui.Screen.Should().Contain(RoofText.DescribePosition(RoofControllerStatus.PartiallyOpen));
        }

        var stopped = await rig.WaitForRestAsync("the stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
