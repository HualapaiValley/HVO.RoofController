using System;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

[TestClass]
public sealed class HatEmulatorSessionTests
{
    private static HatEmulatorSessionOptions Instant(double timeScale = 1)
        => new() { BusTiming = EmulatedBusTiming.Instant, TimeScale = timeScale };

    [TestMethod]
    public void ANewSession_StartsAtTheDocumentedInstallation_WithTheHatAtPowerOn()
    {
        using var session = new HatEmulatorSession(Instant(), new ManualTimeProvider());

        var status = session.GetStatus();

        status.Generation.Should().Be(0);
        status.Elapsed.Should().Be(TimeSpan.Zero);
        status.TimeScale.Should().Be(1);
        status.PositionMeters.Should().Be(new RoofPlantOptions().InitialPosition);
        status.TravelMeters.Should().Be(new RoofPlantOptions().Mechanics.TravelMeters);
        status.OpenPercent.Should().Be(0, "the position is clamped to 0-100 %");
        status.RelayRegister.Should().Be(0);
        status.RelayContacts.Should().Equal(false, false, false, false);
        status.HatPowered.Should().BeTrue();
        status.DrivePowered.Should().BeTrue();
        status.DriveTrip.Should().Be(SmVectorTrip.None);
        status.ClosedLimitActuated.Should().BeTrue();
        status.OpenLimitActuated.Should().BeFalse();
        status.Wiring.Should().Be(WiringFault.None);
        status.Violations.Should().Be(0);
        (status.BusReads, status.BusWrites, status.InjectedBusFailures).Should().Be((0L, 0L, 0L));
        session.Client.ConnectionSettings.BusId.Should().Be(1);
        session.Client.ConnectionSettings.DeviceAddress.Should().Be(0x0E);
        session.CurrentClient().Should().BeSameAs(session.Client);
    }

    [TestMethod]
    public void TheTicker_BringsThePlantUpToTheScaledClock_WithoutAnyAccess()
    {
        var time = new ManualTimeProvider();
        using var session = new HatEmulatorSession(Instant(timeScale: 4), time);

        time.Advance(TimeSpan.FromSeconds(1));

        session.Plant.Elapsed.Should().Be(TimeSpan.FromSeconds(4), "the ticker synced the plant; nothing read it");
        session.Clock.Scale.Should().Be(4);
    }

    [TestMethod]
    public void TheStatus_ReportsTheControlsAndTheBusCounters()
    {
        using var session = new HatEmulatorSession(Instant(), new ManualTimeProvider());
        session.Client.WriteByte(SmI010Board.RelayValueRegister, 0x08);
        session.Client.ReadByte(SmI010Board.DigitalInputRegister);
        session.Client.FailReads = true;
        session.Client.FailWrites = true;
        session.Client.FailInputReads = true;
        FluentActions.Invoking(() => session.Client.ReadByte(0)).Should().Throw<System.IO.IOException>();
        session.Plant.TripDrive(SmVectorTrip.External);
        session.Plant.SetHatPower(false);
        session.Plant.ExternalStopOpen = true;

        var status = session.GetStatus();

        (status.BusReads, status.BusWrites, status.InjectedBusFailures).Should().Be((2L, 1L, 1L));
        (status.FailReads, status.FailWrites, status.FailInputReads).Should().Be((true, true, true));
        status.DriveTrip.Should().Be(SmVectorTrip.External);
        status.HatPowered.Should().BeFalse();
        status.ExternalStopOpen.Should().BeTrue();
    }

    [TestMethod]
    public void Reset_ReplacesThePlantAndTheClient_AndClearsTheBusControls()
    {
        using var session = new HatEmulatorSession(Instant(), new ManualTimeProvider());
        var oldPlant = session.Plant;
        var oldClient = session.Client;
        oldClient.WriteByte(SmI010Board.RelayValueRegister, 0x08);
        oldClient.FailReads = true;

        session.Reset(initialPosition: 1.5, wiring: WiringFault.SwappedLimitInputs);

        session.Generation.Should().Be(1);
        session.Plant.Should().NotBeSameAs(oldPlant);
        session.Client.Should().NotBeSameAs(oldClient);
        session.Client.FailReads.Should().BeFalse();
        session.Client.ReadCount.Should().Be(0);
        session.Plant.Position.Should().Be(1.5);
        session.Plant.Wiring.Should().Be(WiringFault.SwappedLimitInputs);
        session.Plant.Hat.RelayRegister.Should().Be(0);
        FluentActions.Invoking(() => oldClient.ReadByte(0)).Should().Throw<ObjectDisposedException>("the server must not reach the old plant");

        session.Reset();

        session.Generation.Should().Be(2);
        session.Plant.Position.Should().Be(new RoofPlantOptions().InitialPosition, "a reset without arguments uses the configured start");
        session.Plant.Wiring.Should().Be(WiringFault.None);
    }

    [TestMethod]
    public void Dispose_DisposesTheClient_IsIdempotent_AndStopsResets()
    {
        var session = new HatEmulatorSession(Instant(), new ManualTimeProvider());
        var client = session.Client;

        session.Dispose();
        session.Dispose();

        FluentActions.Invoking(() => client.ReadByte(0)).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => session.Reset()).Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    public void TheDefaultBusTiming_IsTheLibrarys()
    {
        new HatEmulatorSessionOptions().BusTiming.Should().Be(EmulatedBusTiming.LibraryDefault);
        new HatEmulatorSessionOptions().TickInterval.Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [TestMethod]
    [DataRow("tick", 0)]
    [DataRow("tick", 1001)]
    [DataRow("scale", 0.05)]
    [DataRow("scale", 101.0)]
    [DataRow("bus", -1.0)]
    public void InvalidOptions_AreRefused(string setting, double value)
    {
        var options = setting switch
        {
            "tick" => Instant() with { TickInterval = TimeSpan.FromMilliseconds(value) },
            "scale" => Instant(value),
            _ => Instant() with { BusId = (int)value }
        };

        FluentActions.Invoking(() => new HatEmulatorSession(options, new ManualTimeProvider())).Should().Throw<ArgumentOutOfRangeException>();
    }
}

[TestClass]
public sealed class ScaledTimeProviderTests
{
    [TestMethod]
    [DataRow(1.0)]
    [DataRow(0.1)]
    [DataRow(2.5)]
    [DataRow(100.0)]
    public void TheClock_RunsAtItsScale(double scale)
    {
        var inner = new ManualTimeProvider();
        var clock = new ScaledTimeProvider(inner, scale);
        var startUtc = clock.GetUtcNow();
        var startStamp = clock.GetTimestamp();

        inner.Advance(TimeSpan.FromSeconds(10));

        clock.GetUtcNow().Should().Be(startUtc + TimeSpan.FromSeconds(10 * scale));
        clock.GetElapsedTime(startStamp).Should().Be(TimeSpan.FromSeconds(10 * scale));
        startUtc.Should().Be(inner.GetUtcNow() - TimeSpan.FromSeconds(10), "the clock starts at the inner clock's time");
    }

    [TestMethod]
    public void AScaleChange_KeepsTheTimeContinuous()
    {
        var inner = new ManualTimeProvider();
        var clock = new ScaledTimeProvider(inner, 2);
        var start = clock.GetTimestamp();

        inner.Advance(TimeSpan.FromSeconds(1));
        var beforeChange = clock.GetElapsedTime(start);
        clock.Scale = 0.5;
        var afterChange = clock.GetElapsedTime(start);
        inner.Advance(TimeSpan.FromSeconds(2));

        beforeChange.Should().Be(TimeSpan.FromSeconds(2));
        afterChange.Should().Be(beforeChange, "the change does not jump the clock");
        clock.GetElapsedTime(start).Should().Be(TimeSpan.FromSeconds(3));
        clock.Scale.Should().Be(0.5);
    }

    [TestMethod]
    [DataRow(0.09)]
    [DataRow(100.01)]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void AScaleOutOfRange_IsRefused_BothAtConstructionAndLater(double scale)
    {
        FluentActions.Invoking(() => new ScaledTimeProvider(new ManualTimeProvider(), scale)).Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("The time scale must be between 0.1 and 100.*");
        var clock = new ScaledTimeProvider(new ManualTimeProvider(), 3);
        FluentActions.Invoking(() => clock.Scale = scale).Should().Throw<ArgumentOutOfRangeException>();
        clock.Scale.Should().Be(3, "a refused change leaves the scale alone");
    }

    [TestMethod]
    public void TheClock_UsesTheInnerClocksFrequencyAndTimeZone_AndTheSystemClockByDefault()
    {
        var inner = new ManualTimeProvider();
        var clock = new ScaledTimeProvider(inner);

        clock.TimestampFrequency.Should().Be(inner.TimestampFrequency);
        clock.LocalTimeZone.Should().Be(inner.LocalTimeZone);
        clock.Scale.Should().Be(1);

        var system = new ScaledTimeProvider();
        system.TimestampFrequency.Should().Be(TimeProvider.System.TimestampFrequency);
        system.GetUtcNow().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
