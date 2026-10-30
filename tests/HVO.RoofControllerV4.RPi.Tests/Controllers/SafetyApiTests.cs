using System.Net;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// The safety conditions through the real pipeline: <c>GET Safety</c>, the <c>Safety</c> on every status and refusal,
/// and the Open interlock in each mode with simulated sources. Close and Stop are never gated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SafetyApiTests
{
    private const string RoofPath = "/api/v4.0/RoofControl";
    private const string SafetyPath = "/api/v4.0/Safety";

    private RoofApiTestHost? _host;

    [TestCleanup]
    public void TestCleanup() => _host?.Dispose();

    // ---- GET Safety and the status ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task Safety_WithoutAKey_Returns401()
    {
        using var client = Host().CreateApiClient();

        var response = await client.GetAsync(SafetyPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Safety_WithNoSourcesConfigured_IsUnknown_AndTheInterlockIsOff()
    {
        using var client = Host().CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync(SafetyPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var safety = await ApiJson.ReadAsync<RoofSafetyStatus>(response);
        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("No safety sources configured");
        safety.Sources.Should().BeEmpty();
        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Off);
        var body = await ApiJson.ReadElementAsync(await client.GetAsync(SafetyPath));
        body.GetProperty("state").GetString().Should().Be("Unknown", "enums are written as names, as on every other route");
    }

    [TestMethod]
    public async Task Status_CarriesTheSafetyConditions()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var client = Host(telescopes: [mount]).CreateApiClient(TestApiKeys.Viewer);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var status = await ApiJson.ReadAsync<RoofStatusResponse>(await client.GetAsync($"{RoofPath}/Status"));

        status.Safety.Should().NotBeNull();
        status.Safety!.State.Should().Be(RoofSafetyState.Unsafe);
        status.Safety.Reasons.Should().Equal("Telescope 'Main' is not parked");
        status.Safety.Sources.Single().Should().BeEquivalentTo(new { Name = "Main", Kind = RoofSafetySourceKind.TelescopePark, IsStale = false });
    }

    // ---- Off (the default) -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Off_OpenGoesAhead_WhateverTheConditions()
    {
        var host = Host(telescopes: [new SimulatedTelescopeParkSource("Main", isParked: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var response = await client.PostAsync($"{RoofPath}/Open", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the default interlock mode leaves Open as it was");
        host.RoofService.Verify(s => s.Open(), Times.Once());
        (await ApiJson.ReadAsync<RoofStatusResponse>(response)).Safety!.State.Should().Be(RoofSafetyState.Unsafe);
    }

    [TestMethod]
    public async Task Off_WithNoSources_OpenGoesAhead()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{RoofPath}/Open", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Open(), Times.Once());
    }

    // ---- Block -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Block_Unknown_RefusesOpen_WithTheConditionsInTheRoofStatus()
    {
        var host = Host(mode: "Block");
        using var client = host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(response);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyInterlockActive));
        problem.GetProperty("title").GetString().Should().Be("Safety interlock active");
        problem.GetProperty("type").GetString().Should().Be("urn:hvo:roof-controller:SafetyInterlockActive");
        problem.GetProperty("detail").GetString().Should().Contain("No safety sources configured");
        var safety = problem.GetProperty("roofStatus").GetProperty("safety");
        safety.GetProperty("state").GetString().Should().Be("Unknown");
        safety.GetProperty("interlockMode").GetString().Should().Be("Block");
        host.RoofService.Verify(s => s.Open(), Times.Never());
    }

    [TestMethod]
    public async Task Block_Unsafe_RefusesOpen_ButNotCloseOrStop()
    {
        var host = Host(mode: "Block", weather: [new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var open = await client.PostAsync($"{RoofPath}/Open", null);
        var close = await client.PostAsync($"{RoofPath}/Close", null);
        var stop = await client.PostAsync($"{RoofPath}/Stop", null);

        open.StatusCode.Should().Be(HttpStatusCode.Conflict);
        close.StatusCode.Should().Be(HttpStatusCode.OK, "closing is never gated by the safety conditions");
        stop.StatusCode.Should().Be(HttpStatusCode.OK, "Stop is always available");
        host.RoofService.Verify(s => s.Open(), Times.Never());
        host.RoofService.Verify(s => s.Close(), Times.Once());
        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public async Task Block_Safe_OpenGoesAhead()
    {
        var host = Host(mode: "Block", telescopes: [new SimulatedTelescopeParkSource("Main")], weather: [new SimulatedWeatherSafetySource("Cloud sensor")]);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        var response = await client.PostAsync($"{RoofPath}/Open", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Open(), Times.Once());
    }

    [TestMethod]
    public async Task Block_TheControllersOwnRefusal_StillComesThrough_WhenSafe()
    {
        var host = Host(mode: "Block", telescopes: [new SimulatedTelescopeParkSource("Main")]);
        host.RoofService.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new HVO.RoofControllerV4.RPi.Logic.RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched (test).")));
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        var response = await client.PostAsync($"{RoofPath}/Open", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiJson.ReadElementAsync(response)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.FaultLatched));
    }

    // ---- Warn ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Warn_WithoutAnOverride_RefusesOpen_AskingForOne()
    {
        var host = Host(mode: "Warn", telescopes: [new SimulatedTelescopeParkSource("Main", isParked: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Admin);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var response = await client.PostAsync($"{RoofPath}/Open", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(response);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired));
        problem.GetProperty("detail").GetString().Should().Contain("Telescope 'Main' is not parked").And.Contain("overrideSafety=true");
        problem.GetProperty("roofStatus").GetProperty("safety").GetProperty("overrideRole").GetString().Should().Be(RoofControllerApiContract.OperatorRole, "anyone who may open may override by default");
        host.RoofService.Verify(s => s.Open(), Times.Never());
    }

    [TestMethod]
    public async Task Warn_NarrowedToAdmins_AnAdminsOverride_OpensTheRoof()
    {
        var host = Host(mode: "Warn", overrideRole: "Admin", telescopes: [new SimulatedTelescopeParkSource("Main", isParked: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Admin);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var response = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Open(), Times.Once());
    }

    [TestMethod]
    public async Task Warn_NarrowedToAdmins_AnOperatorsOverride_IsRefused()
    {
        var host = Host(mode: "Warn", overrideRole: "Admin", telescopes: [new SimulatedTelescopeParkSource("Main", isParked: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var response = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiJson.ReadElementAsync(response)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyInterlockActive));
        host.RoofService.Verify(s => s.Open(), Times.Never());
    }

    [TestMethod]
    public async Task Warn_ByDefault_AnOperatorsOverride_OpensTheRoof()
    {
        var host = Host(mode: "Warn", telescopes: [new SimulatedTelescopeParkSource("Main", isParked: false)]);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unsafe);

        var response = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Open(), Times.Once());
    }

    [TestMethod]
    public async Task Warn_Unknown_IsTreatedAsUnsafe_AndAnOperatorsOverride_OpensTheRoof()
    {
        var host = Host(mode: "Warn");
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var refused = await client.PostAsync($"{RoofPath}/Open", null);
        var opened = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "with no source the conditions are Unknown, which counts as Unsafe");
        (await ApiJson.ReadElementAsync(refused)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired));
        opened.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Open(), Times.Once());
    }

    [TestMethod]
    public async Task Warn_AnOverride_IsAuditedOnlyWhenTheControllerAcceptsOpen()
    {
        var logs = new RecordingLoggerProvider();
        var host = Host(mode: "Warn", logs: logs);
        host.RoofService.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new HVO.RoofControllerV4.RPi.Logic.RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched (test).")));
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var refused = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        logs.Entries.Should().NotContain(entry => entry.Message.StartsWith("AUDIT safety override:", StringComparison.Ordinal),
            "the controller refused Open, so no override took effect");
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("with a safety override was refused by the controller (FaultLatched)", StringComparison.Ordinal));

        host.RoofService.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening));
        var opened = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        opened.StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Entries.Where(entry => entry.Message.StartsWith("AUDIT safety override:", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [TestMethod]
    public async Task Off_AnOpenWhileNotSafe_IsLoggedOnlyWhenTheControllerAcceptsIt()
    {
        var logs = new RecordingLoggerProvider();
        var host = Host(logs: logs);
        host.RoofService.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new HVO.RoofControllerV4.RPi.Logic.RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched (test).")));
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        (await client.PostAsync($"{RoofPath}/Open", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        logs.Entries.Should().NotContain(entry => entry.Message.Contains("interlock Off, advisory only", StringComparison.Ordinal));

        host.RoofService.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening));
        (await client.PostAsync($"{RoofPath}/Open", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Entries.Should().ContainSingle(entry => entry.Message.Contains("interlock Off, advisory only", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OverrideSafety_ThatIsNotABoolean_IsABadRequest()
    {
        var host = Host(mode: "Warn");
        using var client = host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsync($"{RoofPath}/Open?overrideSafety=please", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.RoofService.Verify(s => s.Open(), Times.Never());
    }

    [TestMethod]
    public void AnUnknownInterlockMode_StopsTheStart()
    {
        var host = Host(mode: "Sometimes");

        var start = () => host.CreateApiClient(TestApiKeys.Viewer);

        start.Should().Throw<Exception>("a safety setting the controller cannot use must not be read as Off");
    }

    private RoofApiTestHost Host(
        string? mode = null,
        string? overrideRole = null,
        ITelescopeParkSource[]? telescopes = null,
        IWeatherSafetySource[]? weather = null,
        RecordingLoggerProvider? logs = null)
    {
        var settings = new Dictionary<string, string?>();
        if (mode is not null)
        {
            settings["RoofSafety:InterlockMode"] = mode;
        }

        if (overrideRole is not null)
        {
            settings["RoofSafety:OverrideRole"] = overrideRole;
        }

        _host = new RoofApiTestHost(settings: settings, configureServices: services =>
        {
            foreach (var source in telescopes ?? [])
            {
                services.AddSingleton(source);
            }

            foreach (var source in weather ?? [])
            {
                services.AddSingleton(source);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
        return _host;
    }

    /// <summary>Waits for the safety service's first reads, which start with the host.</summary>
    private static async Task WaitForAsync(HttpClient client, RoofSafetyState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        RoofSafetyStatus? safety = null;
        while (DateTime.UtcNow < deadline)
        {
            safety = await ApiJson.ReadAsync<RoofSafetyStatus>(await client.GetAsync(SafetyPath));
            if (safety.State == state)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new AssertFailedException($"The safety state did not become {state}: {safety?.State} ({string.Join("; ", safety?.Reasons ?? [])}).");
    }
}
