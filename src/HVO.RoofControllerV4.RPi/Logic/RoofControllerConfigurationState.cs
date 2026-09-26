using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

/// <summary>
/// Configuration snapshot and its version, read together so a caller can send the version back with an update.
/// </summary>
public sealed record RoofControllerConfigurationState(RoofControllerOptionsV4 Options, long Version);
