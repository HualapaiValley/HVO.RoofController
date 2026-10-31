using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// The safety path with the controller's default interlock (Warn, #215), through the real pipeline with simulated
/// sources: the conditions decide what Open needs, Unknown counts as unsafe, Close and Stop are never gated, and unsafe
/// conditions never move the roof.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SafetyDefaultApiTests
{
    private const string RoofPath = "/api/v4.0/RoofControl";
    private const string SafetyPath = "/api/v4.0/Safety";

    private RoofApiTestHost? _host;

    [TestCleanup]
    public void TestCleanup() => _host?.Dispose();

    [TestMethod]
    public async Task TheDefaultInterlock_IsWarn_AndWithTheMountParkedAndTheWeatherSafe_OpenNeedsNoOverride()
    {
        var host = Host(new SimulatedTelescopeParkSource("Main"), new SimulatedWeatherSafetySource("Cloud sensor"));
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var safety = await WaitForAsync(client, RoofSafetyState.Safe);
        var response = await client.PostAsync($"{RoofPath}/Open", null);

        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Warn, "Warn is the default once every UI can ask for the override");
        safety.OverrideRole.Should().Be(RoofControllerApiContract.OperatorRole);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    [TestMethod]
    public async Task AnUnparkedMount_AsksOpenForTheOverride_WhichOpens_AndCloseAndStopAreNeverGated()
    {
        var host = Host(new SimulatedTelescopeParkSource("Main", isParked: false));
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var refused = await client.PostAsync($"{RoofPath}/Open", null);
        var close = await client.PostAsync($"{RoofPath}/Close", null);
        var stop = await client.PostAsync($"{RoofPath}/Stop", null);
        var opened = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired));
        problem.GetProperty("detail").GetString().Should().Contain("Telescope 'Main' is not parked");
        close.StatusCode.Should().Be(HttpStatusCode.OK, "closing is never gated by the conditions");
        stop.StatusCode.Should().Be(HttpStatusCode.OK, "Stop is always available");
        opened.StatusCode.Should().Be(HttpStatusCode.OK, "anyone who may open may override");
        host.RoofService.Verify(service => service.Open(), Times.Once());
        host.RoofService.Verify(service => service.Close(), Times.Once());
    }

    [TestMethod]
    public async Task UnsafeWeather_NeverMovesTheRoof()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        var host = Host(new SimulatedTelescopeParkSource("Main"), weather, poll: "00:00:01");
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        weather.IsSafe = false;
        await WaitForAsync(client, RoofSafetyState.Unsafe);
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        host.RoofService.Verify(service => service.Close(), Times.Never(), "unsafe weather does not close the roof by itself");
        host.RoofService.Verify(service => service.Open(), Times.Never());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task ASourceThatCannotBeRead_IsUnknown_WithWhy_AndCountsAsUnsafe()
    {
        var mount = new SimulatedTelescopeParkSource("Main") { Behaviour = SimulatedSourceBehaviour.Hangs };
        var host = Host(mount, poll: "00:00:01", timeout: "00:00:00.5");
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var safety = await WaitForAsync(client, RoofSafetyState.Unknown, until: current => current.Reasons.Any(reason => reason.Contains("cannot be read", StringComparison.Ordinal)));
        var refused = await client.PostAsync($"{RoofPath}/Open", null);

        safety.Reasons.Should().Equal("Telescope 'Main' cannot be read (no answer within 0.5 s)");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiJson.ReadElementAsync(refused)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired),
            "Unknown counts as unsafe, and asks for the override");
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public async Task TheDefault_WithNoSource_IsUnknown_AndAsksEveryOpenForTheOverride()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var safety = await ApiJson.ReadAsync<RoofSafetyStatus>(await client.GetAsync(SafetyPath));
        var refused = await client.PostAsync($"{RoofPath}/Open", null);
        var opened = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        safety.Should().BeEquivalentTo(new { State = RoofSafetyState.Unknown, InterlockMode = RoofSafetyInterlockMode.Warn, Reasons = new[] { RoofSafetyStatus.NoSourcesReason } });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        opened.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    private RoofApiTestHost Host(ITelescopeParkSource? telescope = null, IWeatherSafetySource? weather = null, string poll = "00:00:10", string timeout = "00:00:05")
    {
        var settings = new Dictionary<string, string?>
        {
            // The controller's default interlock, not the test host's Off.
            [RoofApiTestHost.SafetyInterlockModeKey] = null,
            ["RoofSafety:PollInterval"] = poll,
            ["RoofSafety:ReadTimeout"] = timeout,
            ["RoofSafety:MaxReadingAge"] = "00:01:00"
        };
        _host = new RoofApiTestHost(settings: settings, configureServices: services =>
        {
            if (telescope is not null)
            {
                services.AddSingleton(telescope);
            }

            if (weather is not null)
            {
                services.AddSingleton(weather);
            }
        });
        return _host;
    }

    private static async Task<RoofSafetyStatus> WaitForAsync(HttpClient client, RoofSafetyState state, Func<RoofSafetyStatus, bool>? until = null)
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

        throw new AssertFailedException($"The safety state did not become {state}: {safety?.State} ({string.Join("; ", safety?.Reasons ?? [])}).");
    }
}
