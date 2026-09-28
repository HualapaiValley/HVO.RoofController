using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C2: the relay register the controller verifies against the relay contacts. The emulated HAT reports both, so the
/// scenario compares them in every commanded state, and shows what a welded or dead contact does, which the register
/// cannot show.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C2RelayRegisterScenarios
{
    // Hardware overview, section 10: RLY1 forward (open), RLY2 reverse (close), RLY3 clear fault, RLY4 the STOP permit.
    private const int OpenMask = 0b1001;
    private const int CloseMask = 0b1010;
    private const int ClearFaultMask = 0b0100;

    [TestMethod]
    [CommissioningCheck("C2", "1")]
    [CommissioningCheck("C2", "2")]
    public async Task TheRelayRegister_MatchesTheContacts_InEveryCommandedState()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        (await client.StatusAsync()).ShouldBeDeenergized(rig);

        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to open");
        ShouldMatch(await client.StatusAsync(), rig, OpenMask);

        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the stop");

        await client.AcceptedAsync("Close");
        await rig.WaitForPlantAsync(p => p.VelocityMetersPerSecond < 0, "the roof to close");
        ShouldMatch(await client.StatusAsync(), rig, CloseMask);

        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
        await rig.WaitForRestAsync("the stop");

        var pulse = client.AcceptedAsync("ClearFault?pulseMs=2000");
        await rig.WaitForControllerAsync(s => s.IsClearFaultInProgress, "the clear-fault pulse to start");
        ShouldMatch(await client.StatusAsync(), rig, ClearFaultMask);
        var afterPulse = await pulse;
        afterPulse.IsClearFaultInProgress.Should().BeFalse();
        afterPulse.ShouldBeDeenergized(rig);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C2")]
    public async Task AWeldedDirectionContact_IsInvisibleToTheRegister_AndTheStopPermitStillStopsTheDrive()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");
        rig.Session.Plant.SetRelayFault(1, RelayContactFault.Welded);

        var stopped = await client.AcceptedAsync("Stop");

        stopped.ShouldBeDeenergizedInTheRegister();
        var plant = await rig.WaitForPlantAsync(p => p.VelocityMetersPerSecond == 0 && p.OutputFrequencyHz == 0, "RLY4 (the STOP permit) to stop the drive");
        plant.RelayContacts.Should().Equal([true, false, false, false], "the welded RLY1 contact stays closed; the register cannot show it");
        var position = plant.PositionMeters;

        // Closing closes the permit about 30 ms before RLY2: the drive runs forward on the welded contact for that long,
        // then refuses both directions at once, and the at-speed check stops the move.
        await client.AcceptedAsync("Close");
        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the close");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        await rig.WaitForRestAsync("the close");
        rig.Plant.PositionMeters.Should().BeApproximately(position, 0.005, "the roof moves only while the permit leads RLY2");
        rig.Session.Plant.Violations.Should().ContainSingle().Which.Kind.Should().Be(PlantViolationKind.BothDirectionContactsClosed,
            "a welded contact is a hardware fault the controller cannot detect; the drive refuses both directions");
    }

    [TestMethod]
    [CommissioningCheck("C2")]
    [DataRow(1, "the forward contact")]
    [DataRow(4, "the STOP permit contact")]
    public async Task ADeadContact_TheDriveNeverStarts_AndTheAtSpeedCheckStopsTheMove(int relay, string contact)
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        rig.Session.Plant.SetRelayFault(relay, RelayContactFault.Dead);

        var started = await client.AcceptedAsync("Open");

        started.RelayRegisterMask.Should().Be(OpenMask, "the register verifies; only {0} is dead", contact);
        var latched = await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the at-speed check to stop the open");
        latched.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveNotRunning);
        latched.ShouldBeDeenergized(rig);
        var coilOn = rig.ContactChanges(relay == 1 ? 4 : 1).First(c => c.Closed).At;
        var coilOff = rig.ContactChanges(relay == 1 ? 4 : 1).Last(c => !c.Closed).At;
        (coilOff - coilOn).TotalSeconds.Should().BeApproximately(3, 0.3, "AtSpeedConfirmationTimeout is 3 s in production");
        rig.Plant.PositionMeters.Should().Be(-0.01, "the drive never ran");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    private static void ShouldMatch(RoofStatusResponse status, EmulatedRoofRig rig, int mask)
    {
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(mask);
        var contacts = rig.Plant.RelayContacts;
        for (var relay = 1; relay <= 4; relay++)
        {
            contacts[relay - 1].Should().Be((mask & (1 << (relay - 1))) != 0, "RLY{0}'s contact matches bit {1} of the register", relay, relay - 1);
        }
    }
}

internal static class RelayRegisterAssertions
{
    /// <summary>The controller's view of a stop: the register verified all off, whatever the contacts do.</summary>
    public static void ShouldBeDeenergizedInTheRegister(this RoofStatusResponse status)
    {
        status.CommandedMotion.Should().Be(RoofMotionDirection.None);
        status.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        status.RelayRegisterMask.Should().Be(0);
    }
}
