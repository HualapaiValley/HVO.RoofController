namespace HVO.RoofControllerV4.Common.Models
{
    // Response wrapper for roof status with extended telemetry
    public sealed record RoofStatusResponse(
        RoofControllerStatus Status,
        bool IsMoving,
        RoofControllerStopReason LastStopReason,
        DateTimeOffset? LastTransitionUtc,
        bool IsWatchdogActive,
        double? WatchdogSecondsRemaining,
        bool IsAtSpeed,
        bool IsUsingPhysicalHardware,
        bool IsIgnoringPhysicalLimitSwitches);
}
