using System;
using System.Collections.Generic;
using HVO.Iot.Devices.Iot.Devices.Sequent;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Fake for the Sequent Microsystems FourRelayFourInput HAT used across the Roof Controller tests.
/// Provides deterministic register behaviour, relay write logging, fault injection (through <see cref="Registers"/>) and
/// helpers to manipulate input state.
/// </summary>
/// <remarks>
/// Relay read-back in these tests proves only what the in-memory register holds, exactly as on hardware it proves only
/// the HAT register and not the relay contacts.
/// </remarks>
internal sealed class FakeRoofHat : FourRelayFourInputHat
{
    public FakeRoofHat(bool hardwareBacked = false) : this(new FakeRoofRegisterClient(), hardwareBacked)
    {
    }

    private FakeRoofHat(FakeRoofRegisterClient registers, bool hardwareBacked)
        : base(hardwareBacked ? new HardwareLikeRegisterClient(registers) : registers, ownsClient: true)
    {
        Registers = registers;
    }

    /// <summary>The register client, for fault injection and write history.</summary>
    public FakeRoofRegisterClient Registers { get; }

    /// <summary>
    /// Updates the raw input register to simulate electrical states for the four digital inputs.
    /// </summary>
    public void SetInputs(bool forwardLimitHigh, bool reverseLimitHigh, bool faultHigh, bool atSpeedHigh)
        => Registers.SetDigitalInputs(forwardLimitHigh, reverseLimitHigh, faultHigh, atSpeedHigh);

    /// <summary>
    /// Clears the relay write log captured during test execution.
    /// </summary>
    public void ClearRelayWriteLog() => Registers.ClearRelayWriteLog();

    /// <summary>
    /// Gets the accumulated relay write log (register/value pairs) for sequencing assertions.
    /// </summary>
    public IReadOnlyList<(byte Register, byte Value)> RelayWriteLog => Registers.RelayWriteLog;

    /// <summary>
    /// Gets the current relay mask register (bits 0-3 represent relays 1-4).
    /// </summary>
    public byte RelayMask => Registers.RelayMask;

    /// <summary>
    /// Gets the current LED mask register value (bits 0-3 map to indicator LEDs 1-4).
    /// </summary>
    public byte LedMask => Registers.LedMask;

    /// <summary>Relay register value after each relay write.</summary>
    public IReadOnlyList<byte> MaskHistory => Registers.MaskHistory;

    public void ClearMaskHistory() => Registers.ClearMaskHistory();

    /// <summary>True if the relay register ever held both direction bits at once.</summary>
    public bool EverBothDirectionBits => Registers.EverBothDirectionBits;
}
