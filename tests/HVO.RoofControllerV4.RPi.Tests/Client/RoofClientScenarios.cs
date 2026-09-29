using System.Collections.Concurrent;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.TestHost;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client library against the emulated roof (#44): an operator opens and stops the roof while the status feed
/// follows it, and the feed follows the controller through a restart.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class RoofClientScenarios
{
    [TestMethod]
    public async Task AnOperator_OpensAndStopsTheRoof_AndTheFeedFollowsItThroughARestart()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0));
        TestServer? server = rig.App.Server;
        TestServer Current() => Volatile.Read(ref server) ?? throw new ObjectDisposedException("controller");
        using var client = CreateClient(Current, new RoofApiKeyCredential(TestApiKeys.Operator));
        await using var feed = client.CreateStatusFeed();
        var received = new ConcurrentQueue<RoofStatusReceivedEventArgs>();
        feed.StatusReceived += (_, e) => received.Enqueue(e);
        feed.Start();
        await WaitUntilAsync(() => feed.Status is { IsInitialized: true, Status: RoofControllerStatus.Closed }, "the closed roof", TimeSpan.FromSeconds(20));

        await client.Roof.OpenAsync();
        await WaitUntilAsync(() => feed.Status is { IsMoving: true, CommandedMotion: RoofMotionDirection.Opening }, "the feed to show the roof opening", TimeSpan.FromSeconds(20));
        await rig.WaitForPlantAsync(plant => plant.PositionMeters > 0.05, "the roof to travel");
        var stop = await client.StopAsync();

        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        stop.Message.Should().Be(RoofStopText.AcknowledgedVerified, "the emulated HAT reads the relays back");
        await WaitUntilAsync(() => feed.Status is { IsMoving: false, CommandedMotion: RoofMotionDirection.None }, "the feed to show the roof stopped", TimeSpan.FromSeconds(20));
        feed.Status!.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        var before = feed.Current!.InstanceId;

        Volatile.Write(ref server, null);
        await rig.RestartControllerAsync(crash: false);
        Volatile.Write(ref server, rig.App.Server);

        await WaitUntilAsync(() => feed.Current?.InstanceId != before && !feed.IsStale, "the restarted controller's snapshot", TimeSpan.FromSeconds(20));
        feed.Status!.IsInitialized.Should().BeTrue();
        feed.Status.IsMoving.Should().BeFalse();
        received.Where(e => e.IsNewInstance).Should().ContainSingle();
        feed.ConnectionCount.Should().BeGreaterThanOrEqualTo(2);
        var messages = received.ToList();
        messages.Skip(messages.FindIndex(e => e.IsNewInstance)).Should().OnlyContain(
            e => e.Message.InstanceId == feed.Current!.InstanceId, "nothing from the stopped controller is shown after the restarted one's first snapshot");
        (await client.StopAsync()).Outcome.Should().Be(RoofStopOutcome.Acknowledged, "Stop works against the restarted controller");
    }
}
