using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The safety conditions in <c>hvo-roof</c>: the Safety row of <c>status</c>, the watch line, and
/// <c>open --override-safety</c> against the in-process controller with a simulated mount.
/// </summary>
[TestClass]
public sealed class RoofCliSafetyTests
{
    [TestMethod]
    public async Task Status_ShowsTheSafetyConditions()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Safety:      UNKNOWN: No safety sources configured (advisory: the safety interlock is off)");
    }

    [TestMethod]
    public async Task Status_Json_CarriesTheSafetyConditions()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("status", "--json");

        var safety = result.Json.GetProperty("safety");
        safety.GetProperty("state").GetString().Should().Be("Unknown");
        safety.GetProperty("reasons")[0].GetString().Should().Be("No safety sources configured");
        safety.GetProperty("interlockMode").GetString().Should().Be("Off");
    }

    [TestMethod]
    public void TheWatchLine_EndsWithTheSafetyConditions_AndSaysWhenOpenIsBlocked()
    {
        var status = RoofServiceMock.Snapshot() with { Safety = new RoofSafetyStatus { State = RoofSafetyState.Unsafe, InterlockMode = RoofSafetyInterlockMode.Block } };

        RoofCliFormat.DescribeState(status).Should().Be("Closed; last stop: Stopped by operator; safety unsafe, Open blocked");
        RoofCliFormat.DescribeState(status with { Safety = status.Safety! with { InterlockMode = RoofSafetyInterlockMode.Off } })
            .Should().Be("Closed; last stop: Stopped by operator; safety unsafe");
        RoofCliFormat.DescribeState(RoofServiceMock.Snapshot()).Should().Be("Closed; last stop: Stopped by operator", "an older controller sends none");
    }

    [TestMethod]
    public void TheWatchJson_LeavesOutTheReadingTimes_WhichMoveWithEveryRead()
    {
        var status = RoofServiceMock.Snapshot() with
        {
            Safety = new RoofSafetyStatus
            {
                State = RoofSafetyState.Safe,
                Sources = [new RoofSafetySourceStatus { Name = "Main", State = RoofSafetyState.Safe, LastReadingUtc = DateTimeOffset.UtcNow }]
            }
        };
        var later = status with { Safety = status.Safety! with { Sources = [status.Safety.Sources[0] with { LastReadingUtc = DateTimeOffset.UtcNow.AddSeconds(10) }] } };

        RoofCli.CommandBuilder.DescribeJsonState(later).Should().Be(RoofCli.CommandBuilder.DescribeJsonState(status));
        RoofCli.CommandBuilder.DescribeJsonState(later with { Safety = later.Safety! with { State = RoofSafetyState.Unsafe } })
            .Should().NotBe(RoofCli.CommandBuilder.DescribeJsonState(status));
    }

    [TestMethod]
    public async Task Open_InWarnMode_IsRefusedWithoutTheOverride_AndGoesAheadWithIt()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var host = RoofClientApiTests.CreateHost(
            configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount),
            settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Warn", ["RoofSafety:OverrideRole"] = "Operator" });
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using (var client = CreateClient(host, new HVO.RoofControllerV4.Client.RoofApiKeyCredential(TestApiKeys.Viewer)))
        {
            await WaitUntilAsync(() => client.Roof.GetSafetyAsync().GetAwaiter().GetResult().State == RoofSafetyState.Unsafe, "the mount to be read");
        }

        var refused = await rig.RunAsync("open", "--no-wait");
        var opened = await rig.RunAsync("open", "--no-wait", "--override-safety");

        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("Open needs a safety override. [SafetyOverrideRequired]").And.Contain("Telescope 'Main' is not parked");
        opened.Code.Should().Be(RoofExitCode.Success, opened.ToString());
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    [TestMethod]
    public async Task Close_TakesNoSafetyOverride()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("close", "--override-safety");

        result.Code.Should().NotBe(RoofExitCode.Success);
        host.RoofService.Verify(service => service.Close(), Times.Never());
    }
}
