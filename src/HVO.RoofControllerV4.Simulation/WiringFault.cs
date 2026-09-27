namespace HVO.RoofControllerV4.Simulation;

/// <summary>
/// Departures from the documented wiring (<c>docs/projects/roof-controller-v4-rpi/hardware-overview.md</c>, with the
/// IN1-IN3 commons returned to TB-2). <see cref="None"/> is the documented wiring.
/// </summary>
[Flags]
public enum WiringFault
{
    None = 0,

    /// <summary>
    /// IN1 and IN2 are fed through a normally closed contact instead of the ME-8108 NO pair (3-4), so the input is HIGH
    /// away from the limit and LOW at it.
    /// </summary>
    MonitorOnNcContacts = 1 << 0,

    /// <summary>RLY1 feeds the reverse path (TB-13B) and RLY2 the forward path (TB-13A).</summary>
    SwappedDirectionRelays = 1 << 1,

    /// <summary>The FWD limit monitor goes to IN2 and the REV limit monitor to IN1.</summary>
    SwappedLimitInputs = 1 << 2,

    /// <summary>Two motor leads are swapped: the drive's forward rotation closes the roof.</summary>
    SwappedMotorLeads = 1 << 3,

    /// <summary>TB-1 is jumpered to TB-4, so RLY4 no longer removes the STOP permit.</summary>
    StopPermitBypassed = 1 << 4,

    /// <summary>
    /// The IN1-IN3 commons return to TB-4 as the wiring doc first showed. With active-high inputs (P120 = 2) TB-4 is
    /// +15 V, so IN1-IN3 never conduct.
    /// </summary>
    InputCommonsOnTb4 = 1 << 5,

    /// <summary>The ME-8108 NC pairs are bypassed: the limits no longer remove the run inputs.</summary>
    NoHardwiredEndStops = 1 << 6,

    /// <summary>The IN3 wire from TB-17 is broken.</summary>
    FaultMonitorWireBroken = 1 << 7,

    /// <summary>The IN4 wire to TB-14 is broken.</summary>
    RunMonitorWireBroken = 1 << 8,

    /// <summary>
    /// RLY4 NO was left on the 0 V block after the block was moved to TB-2, so closing RLY4 ties TB-1 to 0 V and the
    /// drive always sees STOP.
    /// </summary>
    StopPermitOnTb2 = 1 << 9
}
