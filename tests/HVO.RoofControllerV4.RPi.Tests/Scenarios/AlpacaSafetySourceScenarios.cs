using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Safety;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// The controller's ASCOM Alpaca safety sources on the emulated plant (#220, phase 2): the production settings with the
/// default interlock (Warn), a mount and a weather safety monitor served by the emulator over a real loopback socket, and
/// the roof driven through the API. The conditions decide what Open needs, never what the roof does: unsafe weather never
/// stops, closes or opens the roof, and Close is never gated.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class AlpacaSafetySourceScenarios
{
    private const string SafetyPath = "/api/v4.0/Safety";

    [TestMethod]
    public async Task TheConditions_DecideWhatOpenNeeds_AndNeverMoveOrStopTheRoof()
    {
        await using var alpaca = await EmulatedAlpacaHost.StartAsync();
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: Sources(alpaca)));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await WaitForSafetyAsync(client, RoofSafetyState.Safe, "the parked mount and the safe weather");

        // Unsafe weather: Open asks for the override, and nothing moves.
        alpaca.Alpaca.SetSafetyMonitor(0, safe: false);
        var rain = await WaitForSafetyAsync(client, RoofSafetyState.Unsafe, "the unsafe weather");
        var refused = await client.RefusedAsync("Open");
        rain.WeatherState.Should().Be(RoofSafetyState.Unsafe);
        refused.Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.SafetyOverrideRequired));
        rig.Plant.RelayRegister.Should().Be(0, "a refused Open energizes nothing");
        rig.Plant.PositionMeters.Should().BeLessThan(0.01);

        // Safe again: Open goes ahead, and weather turning unsafe while the roof opens does not stop it.
        alpaca.Alpaca.SetSafetyMonitor(0, safe: true);
        await WaitForSafetyAsync(client, RoofSafetyState.Safe, "the weather safe again");
        await client.AcceptedAsync("Open");
        alpaca.Alpaca.SetSafetyMonitor(0, safe: false);
        await WaitForSafetyAsync(client, RoofSafetyState.Unsafe, "the weather unsafe while the roof opens");
        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, "the roof to open fully");
        var opened = await rig.WaitForRestAsync("the Open");
        opened.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached, "only the open limit ended the move: the conditions stop nothing");

        // The weather staying unsafe closes nothing by itself.
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await rig.WaitForRestAsync("the open roof")).Status.Should().Be(RoofControllerStatus.Open, "nothing closes the roof automatically");

        // Close is never gated, not even with the mount unparked and the weather unsafe.
        alpaca.Alpaca.SetTelescope(0, parked: false);
        await WaitForSafetyAsync(client, RoofSafetyState.Unsafe, "the unparked mount", s => s.Reasons.Contains("Telescope 'Main' is not parked"));
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task AMountThatStopsAnswering_MakesTheConditionsUnknown_SoOpenAsksForTheOverride_WhichMovesTheRoof()
    {
        await using var alpaca = await EmulatedAlpacaHost.StartAsync();
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: Sources(alpaca)));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await WaitForSafetyAsync(client, RoofSafetyState.Safe, "the parked mount and the safe weather");

        alpaca.Alpaca.SetTelescope(0, behaviour: EmulatedAlpacaBehaviour.Hangs);
        var lost = await WaitForSafetyAsync(client, RoofSafetyState.Unknown, "the mount's reading to go stale", s => s.Sources[0].IsStale);
        var refused = await client.RefusedAsync("Open");

        lost.Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 5 s; the last read failed (no answer within 0.5 s)");
        lost.WeatherState.Should().Be(RoofSafetyState.Safe, "the weather alone does not wait on the mount");
        refused.Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.SafetyOverrideRequired), "Unknown counts as unsafe");
        rig.Plant.RelayRegister.Should().Be(0);

        await client.AcceptedAsync("Open?overrideSafety=true");
        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, "the roof to open on the override");
        (await rig.WaitForRestAsync("the Open")).LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
    }

    /// <summary>The controller's default interlock (Warn), with the emulator's mount and safety monitor as its sources, read every second.</summary>
    private static Dictionary<string, string?> Sources(EmulatedAlpacaHost alpaca)
    {
        var url = alpaca.BaseAddress.ToString();
        return new Dictionary<string, string?>
        {
            [RoofApiTestHost.SafetyInterlockModeKey] = null,
            ["RoofSafety:PollInterval"] = "00:00:01",
            ["RoofSafety:ReadTimeout"] = "00:00:00.5",
            ["RoofSafety:MaxReadingAge"] = "00:00:05",
            ["RoofSafety:Telescopes:0:Name"] = "Main",
            ["RoofSafety:Telescopes:0:Url"] = url,
            ["RoofSafety:Telescopes:0:DeviceNumber"] = 0.ToString(CultureInfo.InvariantCulture),
            ["RoofSafety:WeatherSources:0:Name"] = "Cloud sensor",
            ["RoofSafety:WeatherSources:0:Url"] = url
        };
    }

    private static async Task<RoofSafetyStatus> WaitForSafetyAsync(HttpClient client, RoofSafetyState state, string what, Func<RoofSafetyStatus, bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        RoofSafetyStatus? safety = null;
        while (DateTime.UtcNow < deadline)
        {
            safety = await ApiJson.ReadAsync<RoofSafetyStatus>(await client.GetAsync(SafetyPath));
            if (safety.State == state && (until?.Invoke(safety) ?? true))
            {
                return safety;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"Waiting for {what}: the safety state did not become {state}: {safety?.State} ({string.Join("; ", safety?.Reasons ?? [])}).");
    }
}
