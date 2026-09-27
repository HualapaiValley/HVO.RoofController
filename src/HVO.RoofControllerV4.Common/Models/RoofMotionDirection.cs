namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// The motion the controller is currently commanding, independent of the displayed position status.
/// </summary>
public enum RoofMotionDirection
{
    /// <summary>No motion is commanded.</summary>
    None = 0,

    /// <summary>Opening (forward) is commanded.</summary>
    Opening = 1,

    /// <summary>Closing (reverse) is commanded.</summary>
    Closing = 2
}
