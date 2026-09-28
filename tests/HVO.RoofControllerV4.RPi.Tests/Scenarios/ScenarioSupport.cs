using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// Names the commissioning check (<c>docs/commissioning.md</c>, C1-C15) a scenario replaces, and the step when it
/// replaces one step. <c>ScenarioCoverageTests</c> fails unless every check has a scenario.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CommissioningCheckAttribute(string check, string? step = null) : Attribute
{
    public string Check { get; } = check;

    public string? Step { get; } = step;
}

/// <summary>What the commissioning scenarios share.</summary>
internal static class Scenario
{
    /// <summary>
    /// The category of the commissioning scenarios. They start the whole host against the emulated plant and take
    /// seconds each, so the unit test step leaves them out and the scenario job runs them.
    /// </summary>
    public const string Category = "Scenario";

    public const string RoofApi = "/api/v4.0/RoofControl";

    /// <summary>
    /// The production host settings (<c>appsettings.json</c>) against the emulated plant, as the compose
    /// <c>emulator</c> profile runs them: the documented installation with the roof shortened to
    /// <paramref name="travelMeters"/> (25 cm: about 3.5 s from limit to limit, with the drive's 2 s acceleration),
    /// in real time, so the drive, the limits and the controller keep the timing they have on the installation.
    /// </summary>
    public static EmulatedRoofRigOptions Production(
        Func<RoofPlantOptions, RoofPlantOptions>? plant = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        double travelMeters = 0.25,
        double timeScale = 1)
        => new()
        {
            Environment = "Production",
            TravelMeters = travelMeters,
            TimeScale = timeScale,
            Plant = plant,
            Settings = settings
        };

    /// <summary>The drive assumptions of the documented installation, changed by <paramref name="change"/>.</summary>
    public static Func<RoofPlantOptions, RoofPlantOptions> DriveAssumptions(Func<SmVectorAssumptions, SmVectorAssumptions> change)
        => plant => plant with { DriveAssumptions = change(plant.DriveAssumptions) };

    /// <summary>The drive settings of the documented installation, changed by <paramref name="change"/>.</summary>
    public static Func<RoofPlantOptions, RoofPlantOptions> DriveSettings(Func<SmVectorSettings, SmVectorSettings> change)
        => plant => plant with { Drive = change(plant.Drive) };

    /// <summary>Sends a command the controller must accept and returns the status snapshot it answers with.</summary>
    public static async Task<RoofStatusResponse> AcceptedAsync(this HttpClient client, string command)
    {
        using var response = await client.PostAsync($"{RoofApi}/{command}", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} must be accepted: {1}", command, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofStatusResponse>(response);
    }

    /// <summary>Sends a command the controller must refuse and returns the HTTP status and the problem's error code.</summary>
    public static async Task<(HttpStatusCode Status, RoofControllerErrorCode? Code)> RefusedAsync(this HttpClient client, string command)
    {
        using var response = await client.PostAsync($"{RoofApi}/{command}", content: null);
        response.IsSuccessStatusCode.Should().BeFalse("{0} must be refused", command);
        var problem = await ApiJson.ReadElementAsync(response);
        RoofControllerErrorCode? code = problem.TryGetProperty(RoofControllerApiContract.ProblemCodeExtension, out var value)
            && Enum.TryParse<RoofControllerErrorCode>(value.GetString(), out var parsed)
                ? parsed
                : null;
        return (response.StatusCode, code);
    }

    /// <summary>
    /// The times a relay's contact closed and opened, from the plant's history, in real time since the plant started
    /// (the plant's own time divided by its time scale).
    /// </summary>
    public static IReadOnlyList<(TimeSpan At, bool Closed)> ContactChanges(this EmulatedRoofRig rig, int relay)
        => rig.Session.Plant.History
            .Where(e => e.Kind == PlantEventKind.Relay && e.Detail.StartsWith($"RLY{relay} contact ", StringComparison.Ordinal))
            .Select(e => (e.At / rig.Options.TimeScale, e.Detail.EndsWith("closed", StringComparison.Ordinal)))
            .ToArray();

    public static async Task<RoofStatusResponse> StatusAsync(this HttpClient client)
    {
        using var response = await client.GetAsync($"{RoofApi}/Status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ApiJson.ReadAsync<RoofStatusResponse>(response);
    }

    /// <summary>Waits until the controller has stopped a move and the plant's roof and drive are at rest.</summary>
    public static async Task<RoofStatusResponse> WaitForRestAsync(this EmulatedRoofRig rig, string what)
    {
        var stopped = await rig.WaitForControllerAsync(s => !s.IsMoving && s.CommandedMotion == RoofMotionDirection.None, what);
        await rig.WaitForPlantAsync(p => p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0, $"the roof to come to rest after {what}");
        return stopped;
    }

    /// <summary>The assertions every stop must pass: relays verified off in the controller and in the emulated HAT.</summary>
    public static void ShouldBeDeenergized(this RoofStatusResponse status, EmulatedRoofRig rig)
    {
        status.CommandedMotion.Should().Be(RoofMotionDirection.None);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(0);
        rig.Plant.RelayRegister.Should().Be(0, "the HAT's relay register is all off");
        rig.Plant.RelayContacts.Should().AllBeEquivalentTo(false, "every relay contact is open");
    }
}

