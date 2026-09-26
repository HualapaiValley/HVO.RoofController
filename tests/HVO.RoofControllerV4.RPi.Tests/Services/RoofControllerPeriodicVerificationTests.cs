using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Limit detection without edge events (supervision re-reads the inputs) and through the production edge path
/// (HAT input polling raising events). The real-time tests use generous timeouts and only wait for an outcome; they
/// never rely on a particular interleaving.
/// </summary>
[TestClass]
public class RoofControllerPeriodicVerificationTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new AssertFailedException($"Timed out after {timeout.TotalSeconds:0.#} s waiting until {because}.");
            }

            await Task.Delay(10);
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "Opening, open limit edge lost")]
    [DataRow(false, DisplayName = "Closing, closed limit edge lost")]
    public async Task SupervisionCycle_ShouldDetectTheDestinationLimit_WhenTheEdgeWasLost(bool opening)
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var svc = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        (opening ? svc.Open() : svc.Close()).IsSuccessful.Should().BeTrue();

        // The limit is reached but no edge event is delivered (polling disabled in the test defaults).
        if (opening) hat.SetInputs(false, true, false, false);
        else hat.SetInputs(true, false, false, false);
        svc.IsMoving.Should().BeTrue("nothing has read the inputs yet");

        svc.RunSupervisionCycle();

        svc.IsMoving.Should().BeFalse();
        svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        svc.Status.Should().Be(opening ? RoofControllerStatus.Open : RoofControllerStatus.Closed);
        svc.IsWatchdogActive.Should().BeFalse();
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public async Task BackgroundSupervision_ShouldDetectLimitWithoutEvent()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = SimulatedRoofControllerService.Create(hat, timeProvider: null, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.EnablePeriodicVerificationWhileMoving = true;
            opts.PeriodicVerificationInterval = TimeSpan.FromMilliseconds(100);
        }, backgroundSupervision: true);

        try
        {
            (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
            svc.Open().IsSuccessful.Should().BeTrue();

            hat.SetInputs(false, true, false, false); // open limit, no edge event (polling disabled)

            await WaitUntilAsync(() => !svc.IsMoving, TimeSpan.FromSeconds(5), "the supervision loop stops the roof");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
            svc.Status.Should().Be(RoofControllerStatus.Open);
            hat.RelayMask.Should().Be(0x00);
        }
        finally
        {
            await svc.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task InputPollingEdge_ShouldStopMotion_WithoutSupervision()
    {
        // QA-13: the production edge path. The HAT poll loop raises DigitalInput1Changed; the service's subscription
        // stops the roof. Background supervision is off, so nothing else can stop it before the 10 s watchdog.
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = SimulatedRoofControllerService.Create(hat, timeProvider: null, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.EnableDigitalInputPolling = true;
            opts.DigitalInputPollInterval = TimeSpan.FromMilliseconds(10);
            opts.EnablePeriodicVerificationWhileMoving = false;
        });

        try
        {
            (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
            svc.Open().IsSuccessful.Should().BeTrue();

            // The HAT poll loop raises events relative to its own last read. Let it read the mid-travel level first;
            // otherwise (under load) its first read could already see the limit and no edge would ever be raised.
            // In production that missed-edge case is what periodic supervision covers.
            var readsAfterOpen = hat.Registers.InputReadCount;
            await WaitUntilAsync(() => hat.Registers.InputReadCount >= readsAfterOpen + 2, TimeSpan.FromSeconds(5), "the HAT poll loop is reading");

            hat.SetInputs(false, true, false, false); // open limit

            await WaitUntilAsync(() => !svc.IsMoving, TimeSpan.FromSeconds(5), "the polled edge stops the roof");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
            hat.RelayMask.Should().Be(0x00);
        }
        finally
        {
            await svc.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ProductionConfiguration_PollingAndSupervision_ShouldStopOnLimitAndOnFault()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        var svc = SimulatedRoofControllerService.Create(hat, timeProvider: null, opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.EnableDigitalInputPolling = true;
            opts.DigitalInputPollInterval = TimeSpan.FromMilliseconds(10);
            opts.EnablePeriodicVerificationWhileMoving = true;
            opts.PeriodicVerificationInterval = TimeSpan.FromMilliseconds(100);
        }, backgroundSupervision: true);

        try
        {
            (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

            svc.Close().IsSuccessful.Should().BeTrue();
            hat.SetInputs(true, false, false, false); // closed limit
            await WaitUntilAsync(() => !svc.IsMoving, TimeSpan.FromSeconds(5), "the closed limit stops the roof");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
            svc.Status.Should().Be(RoofControllerStatus.Closed);

            svc.Open().IsSuccessful.Should().BeTrue();
            hat.SetInputs(true, true, false, false); // closed limit releases
            await Task.Delay(200);
            svc.IsMoving.Should().BeTrue();
            hat.SetInputs(true, true, true, false);  // drive fault
            await WaitUntilAsync(() => !svc.IsMoving, TimeSpan.FromSeconds(5), "the drive fault stops the roof");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.DriveFault);
            svc.GetCurrentStatusSnapshot().LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
            hat.RelayMask.Should().Be(0x00);
            hat.EverBothDirectionBits.Should().BeFalse();
        }
        finally
        {
            await svc.DisposeAsync();
        }
    }
}
