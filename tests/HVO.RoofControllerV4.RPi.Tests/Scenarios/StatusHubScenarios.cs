using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Hubs;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Drive;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// The status hub on the emulated plant, over the network as a remote client uses it (#40): what a viewer sees while an
/// operator opens, stops and the drive trips, and what it sees when the controller restarts.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class StatusHubScenarios
{
    [TestMethod]
    public async Task AViewer_SeesAnOpen_AStop_AndADriveTrip_InOrder_AndTheLastMessageAgreesWithTheStatusEndpoint()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 1.0) with { Kestrel = true });
        using var operatorClient = rig.CreateApiClient(TestApiKeys.Operator);
        await using var viewer = await StatusHub.ConnectAsync(rig.BaseAddress, TestApiKeys.Viewer);
        (await viewer.NextAsync()).Status.Status.Should().Be(RoofControllerStatus.Closed);

        await operatorClient.AcceptedAsync("Open");
        await viewer.UntilAsync(m => m.Status.IsMoving && m.Status.CommandedMotion == RoofMotionDirection.Opening);
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.05, "the roof to travel");

        await operatorClient.AcceptedAsync("Stop");
        var stopped = (await viewer.UntilAsync(m => !m.Status.IsMoving && m.Status.CommandedMotion == RoofMotionDirection.None))[^1];
        stopped.Status.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);

        await operatorClient.AcceptedAsync("Open");
        await viewer.UntilAsync(m => m.Status.IsMoving && m.Status.CommandedMotion == RoofMotionDirection.Opening);
        rig.Session.Plant.TripDrive(SmVectorTrip.External);
        var tripped = (await viewer.UntilAsync(m => m.Status.IsFaultLatched && !m.Status.IsMoving))[^1];
        tripped.Status.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        tripped.Status.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);

        // Messages queued before the GET are older than it, and the status can still change after it (relay readback,
        // the drive coasting down), so compare the endpoint with the message that has its version.
        RoofStatusResponse? endpoint = null;
        RoofStatusHubMessage? same = null;
        for (var attempt = 0; attempt < 10 && same is null; attempt++)
        {
            var read = await operatorClient.StatusAsync();
            var caughtUp = (await viewer.UntilAsync(m => m.Status.StatusVersion >= read.StatusVersion))[^1];
            (endpoint, same) = (read, caughtUp.Status.StatusVersion == read.StatusVersion ? caughtUp : null);
        }

        same.Should().NotBeNull("the status settles after the trip, and the hub then sends the version GET Status reports");
        Steady(same!.Status).Should().Be(Steady(endpoint!), "the hub and GET Status report the same state");

        var received = viewer.Received;
        received.Select(m => m.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        received.Select(m => m.Status.StatusVersion).Should().BeInAscendingOrder("the status never goes backwards");
        received.Select(m => m.InstanceId).Distinct().Should().ContainSingle();
        received.Select(m => m.ServerTimeUtc).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task WhenTheControllerRestarts_TheConnectionCloses_AndANewOneSeesANewInstance()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production() with { Kestrel = true });
        string before;
        await using (var viewer = await StatusHub.ConnectAsync(rig.BaseAddress, TestApiKeys.Viewer))
        {
            var received = await viewer.UntilAsync(_ => false, count: 2);
            before = received[0].InstanceId;
            received[1].Sequence.Should().BeGreaterThan(received[0].Sequence);

            await rig.RestartControllerAsync(crash: false);

            await viewer.ClosedAsync();
        }

        await using var again = await StatusHub.ConnectAsync(rig.BaseAddress, TestApiKeys.Viewer);
        var first = await again.NextAsync();
        first.InstanceId.Should().NotBe(before, "the sequence starts again in the new process, which only the instance tells apart");
        first.Status.IsInitialized.Should().BeTrue();
    }

    /// <summary>The fields that change only when the roof's state does (not its clocks or countdowns).</summary>
    private static object Steady(RoofStatusResponse status) => new
    {
        status.Status,
        status.IsMoving,
        status.CommandedMotion,
        status.LastStopReason,
        status.IsFaultLatched,
        status.LatchedFaultReason,
        status.IsDriveFaultActive,
        status.RelayRegisterState,
        status.RelayRegisterMask,
        status.IsInitialized,
        status.HatMode
    };
}
