namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// How the controller drives the roof, for a page nobody has signed in to yet (anonymous: no position, state or name).
/// Every client warns while the controller does not drive the observatory roof as in normal use.
/// </summary>
/// <param name="HatMode">What answers the HAT register accesses.</param>
/// <param name="IsIgnoringPhysicalLimitSwitches">True while the controller ignores the limit switch inputs.</param>
public sealed record RoofModeResponse(RoofHatMode HatMode, bool IsIgnoringPhysicalLimitSwitches);
