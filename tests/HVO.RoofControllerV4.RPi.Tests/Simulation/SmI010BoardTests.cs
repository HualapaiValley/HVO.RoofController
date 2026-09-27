using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Simulation;

/// <summary>The SM-I-010 register model and relay timing on their own.</summary>
[TestClass]
public class SmI010BoardTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static void StepRelays(SmI010Board board, int milliseconds)
    {
        for (var i = 0; i < milliseconds; i++)
        {
            board.StepRelays(Ms);
        }
    }

    [TestMethod]
    public void RelayValueRegister_KeepsTheLowFourBits()
    {
        var board = new SmI010Board(new SmI010Options());

        board.WriteRegister(SmI010Board.RelayValueRegister, 0xF9);

        board.RelayRegister.Should().Be(0x09);
        board.ReadRegister(SmI010Board.RelayValueRegister).Should().Be(0x09);
    }

    [TestMethod]
    public void RelaySetAndClearRegisters_TakeARelayNumber()
    {
        var board = new SmI010Board(new SmI010Options());

        board.WriteRegister(SmI010Board.RelaySetRegister, 1);
        board.WriteRegister(SmI010Board.RelaySetRegister, 4);
        board.RelayRegister.Should().Be(0x09);

        board.WriteRegister(SmI010Board.RelayClearRegister, 1);
        board.RelayRegister.Should().Be(0x08);

        board.WriteRegister(SmI010Board.RelaySetRegister, 0);
        board.WriteRegister(SmI010Board.RelaySetRegister, 5);
        board.WriteRegister(SmI010Board.RelayClearRegister, 9);
        board.RelayRegister.Should().Be(0x08, "relay numbers outside 1-4 are ignored");
    }

    [TestMethod]
    public void InputRegister_IsReadOnly_AndReportsTheInputs()
    {
        var board = new SmI010Board(new SmI010Options()) { InputBits = 0x05 };

        board.WriteRegister(SmI010Board.DigitalInputRegister, 0xFF);

        board.ReadRegister(SmI010Board.DigitalInputRegister).Should().Be(0x05);
    }

    [TestMethod]
    public void LedRegisters_AndTheDisplayedLeds()
    {
        var board = new SmI010Board(new SmI010Options()) { InputBits = 0x0A };

        board.DisplayedLeds.Should().Be(0x0A, "at power-up every LED follows its input");

        board.WriteRegister(SmI010Board.LedModeRegister, 0xF3);
        board.LedModeBits.Should().Be(0x03);
        board.WriteRegister(SmI010Board.LedValueRegister, 0xF1);
        board.ReadRegister(SmI010Board.LedValueRegister).Should().Be(0x01);
        board.DisplayedLeds.Should().Be(0x09, "LED1-2 show register 5, LED3-4 their inputs");

        board.WriteRegister(SmI010Board.LedSetRegister, 2);
        board.WriteRegister(SmI010Board.LedClearRegister, 1);
        board.ReadRegister(SmI010Board.LedValueRegister).Should().Be(0x02);
        board.DisplayedLeds.Should().Be(0x0A);
    }

    [TestMethod]
    public void RevisionRegisters_AreReadOnly_AndOtherRegistersAreMemory()
    {
        var board = new SmI010Board(new SmI010Options { Revision = [4, 3, 2, 1] });

        board.WriteRegister(SmI010Board.RevisionRegister, 99);
        board.ReadRegister(SmI010Board.RevisionRegister).Should().Be(4);
        board.ReadRegister(SmI010Board.RevisionRegister + 3).Should().Be(1);

        board.WriteRegister(0x40, 0x5A);
        board.ReadRegister(0x40).Should().Be(0x5A);
    }

    [TestMethod]
    public void Relays_OperateIn10Ms_AndReleaseIn5Ms()
    {
        var board = new SmI010Board(new SmI010Options());
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x01);
        board.IsCoilEnergized(1).Should().BeTrue();

        StepRelays(board, 9);
        board.IsContactClosed(1).Should().BeFalse();
        StepRelays(board, 1);
        board.IsContactClosed(1).Should().BeTrue();

        board.WriteRegister(SmI010Board.RelayValueRegister, 0x00);
        StepRelays(board, 4);
        board.IsContactClosed(1).Should().BeTrue();
        StepRelays(board, 1);
        board.IsContactClosed(1).Should().BeFalse();
    }

    [TestMethod]
    public void CoilPulseShorterThanTheOperateTime_NeverClosesTheContact()
    {
        var board = new SmI010Board(new SmI010Options());
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x01);
        StepRelays(board, 9);
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x00);
        StepRelays(board, 1);

        board.WriteRegister(SmI010Board.RelayValueRegister, 0x01);
        StepRelays(board, 9);

        board.IsContactClosed(1).Should().BeFalse("the operate time starts again");
    }

    [TestMethod]
    public void PowerLoss_FailsEveryAccess_DropsTheRelays_AndResetsTheRegisters()
    {
        var board = new SmI010Board(new SmI010Options());
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x0F);
        board.WriteRegister(SmI010Board.LedModeRegister, 0x0F);
        StepRelays(board, 10);

        board.SetPower(false);

        board.Powered.Should().BeFalse();
        board.Invoking(b => b.ReadRegister(0)).Should().Throw<IOException>();
        board.Invoking(b => b.WriteRegister(0, 1)).Should().Throw<IOException>();
        board.IsCoilEnergized(1).Should().BeFalse();
        StepRelays(board, 5);
        board.IsContactClosed(1).Should().BeFalse();

        board.SetPower(true);
        board.RelayRegister.Should().Be(0);
        board.LedModeBits.Should().Be(0, "power-up loads all-zero registers");
        board.ReadRegister(SmI010Board.RevisionRegister).Should().Be(1);
    }

    [TestMethod]
    public void StuckRelayBits_OverrideWrites()
    {
        var board = new SmI010Board(new SmI010Options()) { StuckOnRelayBits = 0x04, StuckOffRelayBits = 0x01 };

        board.WriteRegister(SmI010Board.RelayValueRegister, 0x09);

        board.RelayRegister.Should().Be(0x0C);
    }

    [TestMethod]
    public void IgnoredRelayWrites_ChangeNothing()
    {
        var board = new SmI010Board(new SmI010Options());
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x08);
        board.IgnoreRelayWrites = true;

        board.WriteRegister(SmI010Board.RelayValueRegister, 0x09);
        board.WriteRegister(SmI010Board.RelaySetRegister, 1);
        board.WriteRegister(SmI010Board.RelayClearRegister, 4);

        board.RelayRegister.Should().Be(0x08);
    }

    [TestMethod]
    public void ContactFaults_OverrideTheCoil()
    {
        var board = new SmI010Board(new SmI010Options());
        board.SetRelayFault(1, RelayContactFault.Welded);
        board.SetRelayFault(2, RelayContactFault.Dead);

        board.IsContactClosed(1).Should().BeTrue();
        board.WriteRegister(SmI010Board.RelayValueRegister, 0x02);
        StepRelays(board, 20);
        board.IsContactClosed(2).Should().BeFalse();
        board.GetRelayFault(1).Should().Be(RelayContactFault.Welded);

        board.SetRelayFault(2, RelayContactFault.None);
        board.IsContactClosed(2).Should().BeTrue("the relay itself operated");
    }

    [TestMethod]
    public void RelayNumbers_OutsideOneToFour_Throw()
    {
        var board = new SmI010Board(new SmI010Options());

        board.Invoking(b => b.IsContactClosed(0)).Should().Throw<ArgumentOutOfRangeException>();
        board.Invoking(b => b.IsCoilEnergized(5)).Should().Throw<ArgumentOutOfRangeException>();
        board.Invoking(b => b.SetRelayFault(5, RelayContactFault.Dead)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    [DataRow(0, 0x0E)]
    [DataRow(3, 0x11)]
    public void I2cAddress_IsTheBaseAddressPlusTheStackLevel(int stackLevel, int address)
    {
        new SmI010Options { StackLevel = stackLevel }.I2cAddress.Should().Be(address);
    }

    [TestMethod]
    public void Revision_MustBeFourBytes()
    {
        FluentActions.Invoking(() => new SmI010Board(new SmI010Options { Revision = [1, 2] }))
            .Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Changed_ReportsRegisterAndContactChanges()
    {
        var board = new SmI010Board(new SmI010Options());
        var changes = new List<string>();
        board.Changed += changes.Add;

        board.WriteRegister(SmI010Board.RelayValueRegister, 0x01);
        StepRelays(board, 10);
        board.SetPower(false);

        changes.Should().Equal("relay register 0x0 -> 0x1", "RLY1 contact closed", "HAT power lost");
    }
}
