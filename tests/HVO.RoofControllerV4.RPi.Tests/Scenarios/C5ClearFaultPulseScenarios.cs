using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Drive;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// C5: the ClearFault pulse on RLY3, which resets the drive through its reset input, measured on the emulated relay's
/// contact. The drive's shortest reset pulse is not documented; the plant assumes 20 ms
/// (<see cref="SmVectorAssumptions.MinimumClearFaultPulse"/>), and one scenario raises it to show a pulse that is too
/// short.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class C5ClearFaultPulseScenarios
{
    [TestMethod]
    [CommissioningCheck("C5", "1")]
    [CommissioningCheck("C5", "4")]
    [CommissioningCheck("C4", "5")]
    public async Task TheDefaultPulse_ClosesRly3ForItsLength_AndResetsTheTrippedDrive()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await TripWhileOpeningAsync(rig, client);

        var cleared = await client.AcceptedAsync("ClearFault");

        cleared.IsFaultLatched.Should().BeFalse();
        cleared.IsDriveFaultActive.Should().BeFalse();
        cleared.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "the original cause is kept");
        cleared.ShouldBeDeenergized(rig);
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.None);
        PulseLengths(rig).Should().ContainSingle().Which.TotalMilliseconds.Should().BeInRange(240, 350,
            "RLY3 closes for the default 250 ms, plus the register round trips");

        await client.AcceptedAsync("Close");
        (await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, "the roof to close"))
            .LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C5", "2")]
    [CommissioningCheck("C5", "3")]
    public async Task APulseShorterThanTheDriveNeeds_LeavesTheLatch_AndALongerOneResetsIt_TenTimesInARow()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(
            plant: Scenario.DriveAssumptions(a => a with { MinimumClearFaultPulse = TimeSpan.FromMilliseconds(300) })));
        using var client = rig.CreateApiClient(TestApiKeys.Operator);

        await TripIdleAsync(rig);
        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.InterlockActive),
            "the 250 ms default is shorter than this drive's reset needs, so IN3 still reports the trip");
        var stillLatched = await client.StatusAsync();
        stillLatched.IsFaultLatched.Should().BeTrue();
        stillLatched.IsDriveFaultActive.Should().BeTrue();
        rig.Plant.DriveTrip.Should().Be(SmVectorTrip.External);

        // A value with margin resets the drive on every attempt.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            if (attempt > 1)
            {
                await TripIdleAsync(rig);
            }

            var cleared = await client.AcceptedAsync("ClearFault?pulseMs=500");
            cleared.IsFaultLatched.Should().BeFalse("attempt {0} resets the drive", attempt);
            rig.Plant.DriveTrip.Should().Be(SmVectorTrip.None);
        }

        PulseLengths(rig).Skip(1).Should().AllSatisfy(pulse => pulse.TotalMilliseconds.Should().BeInRange(490, 600));
    }

    [TestMethod]
    [CommissioningCheck("C5", "5")]
    public async Task ClearFault_WhileMoving_IsRefused()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await client.AcceptedAsync("Open");

        (await client.RefusedAsync("ClearFault")).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.OperationInProgress));

        (await client.StatusAsync()).CommandedMotion.Should().Be(RoofMotionDirection.Opening, "the refusal does not disturb the move");
        rig.ContactChanges(3).Should().BeEmpty("RLY3 never closed");
        (await client.AcceptedAsync("Stop")).ShouldBeDeenergized(rig);
    }

    [TestMethod]
    [CommissioningCheck("C5", "5")]
    [CommissioningCheck("C5", "6")]
    public async Task Stop_DuringALongPulse_EndsThePulse_AndNeitherClearsTheLatch()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production());
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await TripIdleAsync(rig);

        var pulse = client.RefusedAsync("ClearFault?pulseMs=2000");
        await rig.WaitForControllerAsync(s => s.IsClearFaultInProgress, "the pulse to start");
        await rig.WaitForPlantAsync(p => p.RelayContacts[2], "RLY3 to close");
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        (await client.AcceptedAsync("Stop")).IsFaultLatched.Should().BeTrue();

        (await pulse).Should().Be((HttpStatusCode.Conflict, RoofControllerErrorCode.OperationInProgress), "the stop preempts the pulse");
        rig.Plant.RelayContacts[2].Should().BeFalse("RLY3 opened");
        PulseLengths(rig).Should().ContainSingle().Which.TotalMilliseconds.Should().BeLessThan(1000, "the stop ended the 2000 ms pulse");
        var status = await client.StatusAsync();
        status.IsFaultLatched.Should().BeTrue("a preempted pulse leaves the latch, even though it reset the drive");
        status.IsDriveFaultActive.Should().BeFalse();
        status.ShouldBeDeenergized(rig);

        // C5 step 6: another Stop does not clear it either; a completed pulse does.
        (await client.AcceptedAsync("Stop")).IsFaultLatched.Should().BeTrue();
        (await client.AcceptedAsync("ClearFault")).IsFaultLatched.Should().BeFalse();
    }

    private static async Task TripWhileOpeningAsync(EmulatedRoofRig rig, System.Net.Http.HttpClient client)
    {
        await client.AcceptedAsync("Open");
        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.08, "the roof to travel");
        rig.Session.Plant.TripDrive(SmVectorTrip.External);
        (await rig.WaitForControllerAsync(s => s.IsFaultLatched && !s.IsMoving, "the trip to latch"))
            .LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
        await rig.WaitForRestAsync("the trip");
    }

    private static async Task TripIdleAsync(EmulatedRoofRig rig)
    {
        rig.Session.Plant.TripDrive(SmVectorTrip.External);
        (await rig.WaitForControllerAsync(s => s.IsFaultLatched && s.IsDriveFaultActive == true, "the trip to latch"))
            .LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    /// <summary>How long RLY3's contact stayed closed, pulse by pulse.</summary>
    private static TimeSpan[] PulseLengths(EmulatedRoofRig rig)
    {
        var changes = rig.ContactChanges(3);
        return changes.Zip(changes.Skip(1))
            .Where(pair => pair.First.Closed && !pair.Second.Closed)
            .Select(pair => pair.Second.At - pair.First.At)
            .ToArray();
    }
}
