using System;
using System.Collections.Generic;
using HVO.Iot.Devices.Iot.Devices.Sequent;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Lightweight fake for the Sequent Microsystems FourRelayFourInput HAT used across the Roof Controller tests.
/// Provides deterministic register behaviour, relay write logging, and helpers to manipulate input state.
/// </summary>
internal sealed class FakeRoofHat : FourRelayFourInputHat
{
    private readonly FourRelayFourInputHatMemoryClient _client;

    public FakeRoofHat() : base(new FourRelayFourInputHatMemoryClient(), ownsClient: true)
    {
        _client = (FourRelayFourInputHatMemoryClient)Client;
    }

    /// <summary>
    /// Updates the raw input register to simulate electrical states for the four digital inputs.
    /// </summary>
    public void SetInputs(bool forwardLimitHigh, bool reverseLimitHigh, bool faultHigh, bool atSpeedHigh)
        => _client.SetDigitalInputs(forwardLimitHigh, reverseLimitHigh, faultHigh, atSpeedHigh);

    /// <summary>
    /// Clears the relay write log captured during test execution.
    /// </summary>
    public void ClearRelayWriteLog() => _client.ClearRelayWriteLog();

    /// <summary>
    /// Gets the accumulated relay write log (register/value pairs) for sequencing assertions.
    /// </summary>
    public IReadOnlyList<(byte Register, byte Value)> RelayWriteLog => _client.RelayWriteLog;

    /// <summary>
    /// Gets the current relay mask register (bits 0-3 represent relays 1-4).
    /// </summary>
    public byte RelayMask => _client.RelayMask;

    /// <summary>
    /// Gets the current LED mask register value (bits 0-3 map to indicator LEDs 1-4).
    /// </summary>
    public byte LedMask => _client.LedMask;

}
