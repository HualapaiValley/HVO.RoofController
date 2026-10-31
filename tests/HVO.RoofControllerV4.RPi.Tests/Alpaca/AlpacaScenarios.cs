using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server in front of the emulated roof, drive and HAT (#220, phase 1): an Alpaca client watches the roof an
/// operator opened, stops it with AbortSlew, sees it stop, and cannot open or close it.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class AlpacaScenarios
{
    [TestMethod]
    public async Task AnAlpacaClient_StopsTheEmulatedRoofWithAbortSlew_AndCannotMoveIt()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        await using var alpaca = await AlpacaTestHost.StartAsync(() => rig.App.Server);
        await alpaca.ConnectAsync();
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Closed);

        // An operator opens the roof through the controller's own API: this phase's Alpaca server cannot.
        using var api = rig.CreateApiClient(TestApiKeys.Operator);
        (await api.PostAsync("api/v4.0/RoofControl/Open", null)).EnsureSuccessStatusCode();
        await WaitForAsync(alpaca, "shutterstatus", value => value.GetInt32() == (int)DomeShutterState.Opening, "Alpaca to show the roof opening");
        (await alpaca.GetAsync("slewing")).GetProperty("Value").GetBoolean().Should().BeTrue();
        await rig.WaitForPlantAsync(plant => plant.PositionMeters > 0.05, "the roof to travel");

        var stop = await alpaca.PutAsync("abortslew");

        stop.GetProperty("ErrorNumber").GetInt32().Should().Be(0, stop.GetProperty("ErrorMessage").GetString());
        var stopped = await rig.WaitForControllerAsync(status => !status.IsMoving, "the controller to stop the roof");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        await rig.WaitForPlantAsync(plant => Math.Abs(plant.VelocityMetersPerSecond) < 1e-6, "the roof to come to rest");
        await WaitForAsync(alpaca, "slewing", value => !value.GetBoolean(), "Alpaca to show the roof at rest");
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Error, "a roof stopped part way reads as Error");

        var position = rig.Plant.PositionMeters;
        (await alpaca.PutAsync("openshutter")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.InvalidOperation);
        (await alpaca.PutAsync("closeshutter")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.InvalidOperation);
        await Task.Delay(TimeSpan.FromSeconds(1));
        rig.Plant.PositionMeters.Should().BeApproximately(position, 0.001, "neither OpenShutter nor CloseShutter moves the roof");
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeFalse();
    }

    private static async Task WaitForAsync(AlpacaTestHost alpaca, string member, Func<JsonElement, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + EmulatedRoofRig.MotionTimeout;
        while (true)
        {
            var answer = await alpaca.GetAsync(member);
            if (answer.GetProperty("ErrorNumber").GetInt32() == 0 && condition(answer.GetProperty("Value")))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}. Last answer: {answer}");
            }

            await Task.Delay(20);
        }
    }
}
