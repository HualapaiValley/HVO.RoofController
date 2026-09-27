using System;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// A <see cref="RoofPlant"/> on a <see cref="ManualTimeProvider"/>, driven through the emulated I2C client with raw
/// register writes (no controller). For tests of the plant models themselves.
/// </summary>
internal sealed class PlantRig
{
    public PlantRig(RoofPlantOptions? options = null)
    {
        Time = new ManualTimeProvider();
        Plant = new RoofPlant(options ?? new RoofPlantOptions(), Time);
        Bus = new EmulatedHatRegisterClient(Plant);
    }

    public ManualTimeProvider Time { get; }

    public RoofPlant Plant { get; }

    public EmulatedHatRegisterClient Bus { get; }

    /// <summary>Opto input levels the HAT reports, bit 0 = IN1.</summary>
    public byte Inputs => Plant.InputBits;

    /// <summary>Writes the relay register (bit 0 = RLY1) over the emulated bus.</summary>
    public void Relays(byte mask) => Bus.WriteByte(SmI010Board.RelayValueRegister, mask);

    public void Advance(TimeSpan duration)
    {
        Time.Advance(duration);
        Plant.Sync();
    }

    public void AdvanceMs(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Advances in 1 ms steps until <paramref name="condition"/> holds; false when <paramref name="timeout"/> passes first.</summary>
    public bool RunUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = Plant.Elapsed + timeout;
        while (!condition())
        {
            if (Plant.Elapsed >= end)
            {
                return false;
            }

            AdvanceMs(1);
        }

        return true;
    }
}
