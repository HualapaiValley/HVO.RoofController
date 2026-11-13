using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
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
        using var harness = await PlantHarness.StartAsync();
        harness.Open().IsSuccessful.Should().BeTrue();
        harness.RunUntil(() => harness.Plant.Position > 0.08, TimeSpan.FromSeconds(5)).Should().BeTrue();
        TripIdle(harness);
        harness.RunUntilStopped(TimeSpan.FromSeconds(5)).Should().BeTrue();

        (await harness.ClearFaultAsync()).IsSuccessful.Should().BeTrue();
        var cleared = harness.Snapshot;
        cleared.IsFaultLatched.Should().BeFalse();
        cleared.IsDriveFaultActive.Should().BeFalse();
        cleared.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault, "the original cause is kept");
        cleared.ShouldBeDeenergizedInTheRegister();
        harness.RelayRegister.Should().Be(0, "the HAT's relay register is all off");
        Enumerable.Range(1, 4).Should().OnlyContain(relay => !harness.Plant.Hat.IsContactClosed(relay),
            "every relay contact is open");
        harness.Plant.Drive.Trip.Should().Be(SmVectorTrip.None);
        PulseLengths(harness).Should().ContainSingle().Which.TotalMilliseconds.Should().BeInRange(240, 350,
            "RLY3 closes for the default 250 ms, plus the register round trips on the manual clock");

        harness.Close().IsSuccessful.Should().BeTrue();
        harness.RunUntil(() => harness.Status == RoofControllerStatus.Closed && !harness.Snapshot.IsMoving,
            TimeSpan.FromSeconds(10)).Should().BeTrue();
        harness.Snapshot.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        harness.Violations.Should().BeEmpty();
    }

    [TestMethod]
    [CommissioningCheck("C5", "2")]
    [CommissioningCheck("C5", "3")]
    public async Task APulseShorterThanTheDriveNeeds_LeavesTheLatch_AndALongerOneResetsIt_TenTimesInARow()
    {
        var minimumPulse = TimeSpan.FromMilliseconds(350);
        using var harness = await PlantHarness.StartAsync(new RoofPlantOptions
        {
            DriveAssumptions = new SmVectorAssumptions { MinimumClearFaultPulse = minimumPulse }
        });

        TripIdle(harness);
        // The timer starts after RLY3's write/read-back, so the measured contact pulse exceeds the requested
        // 250 ms. Give those transactions margin below the test drive's 350 ms minimum. The harness stops
        // advancing the clock at the timer's deadline until the asynchronous relay release has completed.
        (await harness.ClearFaultAsync()).ErrorCode().Should().Be(RoofControllerErrorCode.InterlockActive,
            "the 250 ms default is shorter than this drive's reset needs, so IN3 still reports the trip");
        var stillLatched = harness.Snapshot;
        stillLatched.IsFaultLatched.Should().BeTrue();
        stillLatched.IsDriveFaultActive.Should().BeTrue();
        harness.Plant.Drive.Trip.Should().Be(SmVectorTrip.External);
        var measuredShortPulse = PulseLengths(harness).Should().ContainSingle().Which;
        (minimumPulse - measuredShortPulse).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "the measured contact pulse needs margin for the HAT register transactions, beyond the requested delay");

        // A value with margin resets the drive on every attempt.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            if (attempt > 1)
            {
                TripIdle(harness);
            }

            (await harness.ClearFaultAsync(500)).IsSuccessful.Should().BeTrue("attempt {0} resets the drive", attempt);
            harness.Snapshot.IsFaultLatched.Should().BeFalse("attempt {0} resets the drive", attempt);
            harness.Plant.Drive.Trip.Should().Be(SmVectorTrip.None);
        }

        PulseLengths(harness).Skip(1).Should().HaveCount(10).And.AllSatisfy(pulse =>
        {
            pulse.Should().BeGreaterThan(minimumPulse);
            pulse.TotalMilliseconds.Should().BeInRange(490, 600);
        });
        harness.Violations.Should().BeEmpty();
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
        using var harness = await PlantHarness.StartAsync();
        TripIdle(harness);

        var pulse = harness.Controller.ClearFault(2000);
        harness.Snapshot.IsClearFaultInProgress.Should().BeTrue();
        harness.Plant.Hat.IsContactClosed(3).Should().BeTrue();
        harness.RunFor(TimeSpan.FromMilliseconds(200));
        pulse.IsCompleted.Should().BeFalse("the manual clock has not reached the 2000 ms pulse deadline");
        harness.Stop().IsSuccessful.Should().BeTrue();

        (await pulse.WaitAsync(TimeSpan.FromSeconds(10))).ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress,
            "the stop preempts the pulse");
        harness.Plant.Hat.IsContactClosed(3).Should().BeFalse("RLY3 opened");
        PulseLengths(harness).Should().ContainSingle().Which.TotalMilliseconds.Should().BeLessThan(1000,
            "the stop ended the 2000 ms pulse, measured on the manual clock");
        var status = harness.Snapshot;
        status.IsFaultLatched.Should().BeTrue("a preempted pulse leaves the latch, even though it reset the drive");
        status.IsDriveFaultActive.Should().BeFalse();
        status.ShouldBeDeenergizedInTheRegister();
        harness.RelayRegister.Should().Be(0);
        Enumerable.Range(1, 4).Should().OnlyContain(relay => !harness.Plant.Hat.IsContactClosed(relay));

        // C5 step 6: another Stop does not clear it either; a completed pulse does.
        harness.Stop().IsSuccessful.Should().BeTrue();
        harness.Snapshot.IsFaultLatched.Should().BeTrue();
        (await harness.ClearFaultAsync()).IsSuccessful.Should().BeTrue();
        harness.Snapshot.IsFaultLatched.Should().BeFalse();
        harness.Violations.Should().BeEmpty();
    }

    private static void TripIdle(PlantHarness harness)
    {
        harness.Plant.TripDrive(SmVectorTrip.External);
        harness.RunFor(TimeSpan.FromSeconds(1));
        harness.Snapshot.IsFaultLatched.Should().BeTrue();
        harness.Snapshot.IsDriveFaultActive.Should().BeTrue();
        harness.Snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    private static TimeSpan[] PulseLengths(PlantHarness harness)
    {
        var changes = harness.Plant.History
            .Where(e => e.Kind == PlantEventKind.Relay && e.Detail.StartsWith("RLY3 contact ", StringComparison.Ordinal))
            .ToArray();
        return changes.Zip(changes.Skip(1))
            .Where(pair => pair.First.Detail.EndsWith("closed", StringComparison.Ordinal)
                && pair.Second.Detail.EndsWith("open", StringComparison.Ordinal))
            .Select(pair => pair.Second.At - pair.First.At)
            .ToArray();
    }
}
