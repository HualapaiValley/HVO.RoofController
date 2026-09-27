using System;
using System.IO;
using FluentAssertions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Simulation;

/// <summary>The emulated I2C client, alone and under the real HAT library.</summary>
[TestClass]
public class EmulatedHatRegisterClientTests
{
    [TestMethod]
    public void RealHatLibrary_TreatsTheClientAsHardware_AndReachesThePlant()
    {
        var rig = new PlantRig();
        using var hat = new FourRelayFourInputHat(rig.Bus, ownsClient: false);

        hat.IsHardwareBacked.Should().BeTrue("the controller must take its physical-hardware paths");
        hat.HardwareRevision.Should().Be(((byte)1, (byte)2));
        hat.SoftwareRevision.Should().Be(((byte)1, (byte)4));

        hat.SetRelaysMask(0x09).IsSuccessful.Should().BeTrue();
        rig.Plant.Hat.RelayRegister.Should().Be(0x09);
        hat.GetRelaysMask().Value.Should().Be(0x09);

        hat.GetAllDigitalInputs().Value.Should().Be((false, true, true, false), "the roof rests on the closed limit with the drive healthy");

        hat.SetLedModesMask(0x0F).IsSuccessful.Should().BeTrue();
        rig.Plant.Hat.LedModeBits.Should().Be(0x0F);
    }

    [TestMethod]
    public void ConnectionSettings_FollowTheStackLevel()
    {
        var plant = new RoofPlant(new RoofPlantOptions { Hat = new SmI010Options { StackLevel = 2 } }, new ManualTimeProvider());
        var client = new EmulatedHatRegisterClient(plant, busId: 3);

        client.ConnectionSettings.BusId.Should().Be(3);
        client.ConnectionSettings.DeviceAddress.Should().Be(0x10);
        client.Plant.Should().BeSameAs(plant);
    }

    [TestMethod]
    public void EveryAccess_BringsThePlantUpToTheClock()
    {
        var rig = new PlantRig();
        rig.Time.Advance(TimeSpan.FromMilliseconds(50));

        rig.Bus.ReadByte(SmI010Board.DigitalInputRegister);

        rig.Plant.Elapsed.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    public void WideReadsAndWrites_AreLittleEndianAcrossConsecutiveRegisters()
    {
        var rig = new PlantRig();

        rig.Bus.ReadUInt16(SmI010Board.RevisionRegister).Should().Be(0x0201);
        rig.Bus.ReadUInt32(SmI010Board.RevisionRegister).Should().Be(0x04010201u);

        rig.Bus.WriteUInt16(0x40, 0xBEEF);
        rig.Bus.ReadByte(0x40).Should().Be(0xEF);
        rig.Bus.ReadByte(0x41).Should().Be(0xBE);
    }

    [TestMethod]
    public void FailReadsAndWrites_ThrowIOException_AndAreCounted()
    {
        var rig = new PlantRig();
        rig.Bus.FailReads = true;
        rig.Bus.FailWrites = true;

        rig.Bus.Invoking(b => b.ReadByte(0)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.WriteByte(0, 1)).Should().Throw<IOException>();
        rig.Plant.Hat.RelayRegister.Should().Be(0, "a failed write changes nothing");
        rig.Bus.ReadCount.Should().Be(1);
        rig.Bus.WriteCount.Should().Be(1);

        rig.Bus.FailReads = false;
        rig.Bus.FailWrites = false;
        rig.Bus.ReadByte(0).Should().Be(0);
        rig.Bus.WriteByte(0, 1);
        rig.Plant.Hat.RelayRegister.Should().Be(1);
    }

    [TestMethod]
    public void FailNextReadsAndWrites_FailThatManyAccesses()
    {
        var rig = new PlantRig();
        rig.Bus.FailNextReads = 2;
        rig.Bus.FailNextWrites = 1;

        rig.Bus.Invoking(b => b.ReadByte(0)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.ReadByte(0)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.ReadByte(0)).Should().NotThrow();
        rig.Bus.FailNextReads.Should().Be(0);

        rig.Bus.Invoking(b => b.WriteByte(0, 1)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.WriteByte(0, 1)).Should().NotThrow();
    }

    [TestMethod]
    public void InputReadFailures_AffectOnlyReadsThatCoverRegister3()
    {
        var rig = new PlantRig();
        rig.Bus.FailInputReads = true;

        rig.Bus.Invoking(b => b.ReadByte(0)).Should().NotThrow();
        rig.Bus.Invoking(b => b.ReadByte(4)).Should().NotThrow();
        rig.Bus.Invoking(b => b.ReadByte(3)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.ReadUInt16(2)).Should().Throw<IOException>();

        rig.Bus.FailInputReads = false;
        rig.Bus.FailNextInputReads = 1;
        rig.Bus.Invoking(b => b.ReadByte(0)).Should().NotThrow("other registers do not use up the count");
        rig.Bus.Invoking(b => b.ReadByte(3)).Should().Throw<IOException>();
        rig.Bus.Invoking(b => b.ReadByte(3)).Should().NotThrow();
    }

    [TestMethod]
    public void UnpoweredHat_FailsAccessesWithIOException()
    {
        var rig = new PlantRig();
        rig.Plant.SetHatPower(false);

        rig.Bus.Invoking(b => b.ReadByte(3)).Should().Throw<IOException>().WithMessage("*does not respond*");
        rig.Bus.Invoking(b => b.WriteByte(0, 1)).Should().Throw<IOException>();
    }

    [TestMethod]
    public void AccessPastRegisterFF_Throws()
    {
        var rig = new PlantRig();

        rig.Bus.Invoking(b => b.ReadByte(0xFF)).Should().NotThrow();
        rig.Bus.Invoking(b => b.ReadUInt16(0xFF)).Should().Throw<ArgumentOutOfRangeException>();
        rig.Bus.Invoking(b => b.WriteUInt16(0xFF, 1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void DisposedClient_Throws()
    {
        var rig = new PlantRig();
        rig.Bus.Dispose();

        rig.Bus.Invoking(b => b.ReadByte(0)).Should().Throw<ObjectDisposedException>();
        rig.Bus.Invoking(b => b.WriteByte(0, 0)).Should().Throw<ObjectDisposedException>();
    }
}
