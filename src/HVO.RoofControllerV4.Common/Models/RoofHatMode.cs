namespace HVO.RoofControllerV4.Common.Models;

/// <summary>What answers the controller's HAT register accesses.</summary>
public enum RoofHatMode
{
    /// <summary>Not reported (a controller older than this field).</summary>
    Unknown = 0,

    /// <summary>The physical Sequent SM-I-010 HAT on the Pi's I2C bus.</summary>
    Physical = 1,

    /// <summary>
    /// The HAT emulator (<c>HVO.RoofControllerV4.Emulator</c>) over TCP: an emulated HAT, drive, limit switches and roof.
    /// No relay or input on the Pi is used.
    /// </summary>
    Emulated = 2,

    /// <summary>The in-memory register simulation: registers only, with no roof, drive or limit switches behind them.</summary>
    Simulation = 3
}
