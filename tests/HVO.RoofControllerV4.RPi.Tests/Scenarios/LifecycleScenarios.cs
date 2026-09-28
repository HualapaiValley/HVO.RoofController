using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// The controller's own life cycle on the emulated plant: the host stopping during travel (as <c>docker stop</c>
/// does), a crash that leaves the relays as they were followed by a restart, and the drive's mains cycled during
/// travel, with the start-too-soon trip (F_UF) the drive raises when a run command arrives within 2 s of power-up.
/// The roof is the documented 2 m (about 21 s from limit to limit), so each event happens in mid-travel.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class LifecycleScenarios
{
    [TestMethod]
    [CommissioningCheck("C11", "1")]
    [CommissioningCheck("C11", "2")]
    [CommissioningCheck("C11", "3")]
    public async Task AHostShutdown_DuringTravel_WithACameraStreamOpen_StopsTheRoof_EndsTheStream_AndTheNextHostStartsIdle()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 2.0) with { Kestrel = true, Camera = true });
        using var viewer = rig.CreateApiClient(TestApiKeys.Viewer);
        using var camera = await viewer.GetAsync("/api/v1.0/Camera/2/mjpeg", HttpCompletionOption.ResponseHeadersRead);
        camera.StatusCode.Should().Be(HttpStatusCode.OK);
        var streamEnded = DrainAsync(await camera.Content.ReadAsStreamAsync());
        using (var client = rig.CreateApiClient(TestApiKeys.Operator))
        {
            await client.AcceptedAsync("Open");
        }

        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");
        rig.Camera.GetStatus().FramesSent.Should().BeGreaterThan(0, "the viewer is watching the roof move");
        var stopping = rig.Controller;
        var logs = rig.Logs;
        var requested = rig.Plant.Elapsed / rig.Options.TimeScale;
        var stopRequested = Stopwatch.GetTimestamp();

        await rig.RestartControllerAsync(crash: false);

        var off = rig.ContactChanges(1).Last().At - requested;
        off.Should().BeLessThan(TimeSpan.FromSeconds(1), "the host's shutdown stop turns the relays off at once");
        rig.LastStopDuration.Should().BeLessThan(TimeSpan.FromSeconds(5), "the open stream does not hold up the host's 20 s shutdown timeout");
        Stopwatch.GetElapsedTime(stopRequested, await streamEnded.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().BeLessThan(TimeSpan.FromSeconds(5), "the host ends the viewer's stream as it stops");
        rig.Camera.GetStatus().OpenStreams.Should().Be(0, "the proxy closed its camera connection");
        var last = stopping.GetCurrentStatusSnapshot();
        last.LastStopReason.Should().Be(RoofControllerStopReason.HostShutdown);
        last.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        last.RelayRegisterMask.Should().Be(0);
        logs.Entries.Should().Contain(e => e.Message.StartsWith("Roof controller shutdown stop completed", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);

        await AtRestInMidTravelAsync(rig);
    }

    [TestMethod]
    [CommissioningCheck("C11")]
    public async Task ACrash_DuringTravel_LeavesTheRelaysHeld_AndTheRestartedControllerTurnsThemOff()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 2.0));
        using (var client = rig.CreateApiClient(TestApiKeys.Operator))
        {
            await client.AcceptedAsync("Open");
        }

        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");
        var crashedAt = rig.Plant.PositionMeters;
        HatEmulatorStatus? whileDown = null;

        await rig.RestartControllerAsync(crash: true, whileStopped: session => whileDown = session.GetStatus());

        whileDown!.RelayRegister.Should().Be(0b1001, "the dead controller's STOP permit and open relays stay on");
        whileDown.PositionMeters.Should().BeGreaterThan(crashedAt, "the roof kept moving with the controller down");
        whileDown.OpenLimitActuated.Should().BeFalse();
        rig.Logs.Entries.Should().Contain(e => e.Message.StartsWith("Roof controller initialized.", StringComparison.Ordinal));
        await AtRestInMidTravelAsync(rig);
    }

    [TestMethod]
    [CommissioningCheck("C11")]
    public async Task ACrash_ThatOutlastsTheTravel_LeavesTheOpenLimitToStopTheDrive_AndTheRestartedControllerReportsOpen()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using (var before = rig.CreateApiClient(TestApiKeys.Operator))
        {
            await before.AcceptedAsync("Open");
        }

        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        await rig.RestartControllerAsync(crash: true, whileStopped: session =>
            SpinWait.SpinUntil(() => session.GetStatus() is { OpenLimitActuated: true, VelocityMetersPerSecond: 0, OutputFrequencyHz: 0 },
                EmulatedRoofRig.MotionTimeout).Should().BeTrue("the open limit's normally closed contact stops the drive"));

        var status = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open, "the restarted controller to read the open limit");
        status.IsFaultLatched.Should().BeFalse();
        status.ShouldBeDeenergized(rig);
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C4")]
    public async Task TheDrivesMainsCycled_DuringTravel_LatchesDriveFault_AndAStartWithinTwoSecondsOfPowerUp_TripsF_UF()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(travelMeters: 2.0));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");

        rig.Session.Plant.SetDrivePower(false);

        var lost = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the power loss to stop the open");
        lost.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault, "the unpowered drive's fault relay reads as a trip");
        lost.ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the power loss");

        // Power back, the latch cleared and Open at once: the drive refuses a start inside its 2 s power-up lockout.
        rig.Session.Plant.SetDrivePower(true);
        var poweredAt = DateTimeOffset.UtcNow;
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse("the powered drive reports no trip");
        await client.AcceptedAsync("Open");
        (DateTimeOffset.UtcNow - poweredAt).Should().BeLessThan(SmVectorSettings.PowerUpStartLockout, "the start came inside the lockout");

        var tripped = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "F_UF to stop the open");
        tripped.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        tripped.ShouldBeDeenergized(rig);
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.StartTooSoonAfterPowerUp);

        // After the lockout, ClearFault resets the trip and the roof opens.
        var wait = SmVectorSettings.PowerUpStartLockout - (DateTimeOffset.UtcNow - poweredAt);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.None);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Open);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    /// <summary>Reads <paramref name="stream"/> until it ends or fails, and returns the <see cref="Stopwatch"/> timestamp then.</summary>
    private static async Task<long> DrainAsync(Stream stream)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
        }

        return Stopwatch.GetTimestamp();
    }

    /// <summary>The restarted controller has turned the relays off, the roof rests between the limits and closes normally.</summary>
    private static async Task AtRestInMidTravelAsync(EmulatedRoofRig rig)
    {
        var status = await rig.WaitForRestAsync("the restart");
        status.IsFaultLatched.Should().BeFalse("a restart in mid-travel is not a fault");
        status.IsMoving.Should().BeFalse();
        status.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse();
        rig.Plant.ClosedLimitActuated.Should().BeFalse();

        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        (await client.StatusAsync()).Status.Should().NotBe(RoofControllerStatus.Error);
        using var anonymous = rig.CreateApiClient();
        using var ready = await anonymous.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        await rig.MoveToLimitAsync(client, RoofControllerStatus.Closed);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
